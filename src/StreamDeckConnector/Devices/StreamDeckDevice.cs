using System.Security.Cryptography;
using Serilog;
using StreamDeckConnector.Hid;

namespace StreamDeckConnector.Devices;

/// <summary>
/// One opened Stream Deck. A dedicated reader raises key events straight from input reports and never
/// waits behind writes. One writer owns every output and feature report, so reports of different keys
/// never interleave. Pictures arrive already encoded for the model.
/// </summary>
public sealed class StreamDeckDevice : IAsyncDisposable
{
	private const int InputBufferLength = 1024;
	private static readonly TimeSpan _stopTimeout = TimeSpan.FromSeconds(2);
	private static readonly TimeSpan _keepAliveInterval = TimeSpan.FromMilliseconds(500);

	// Marks a key that was blanked by a feature report. Never equals a SHA-256 hash.
	private static readonly byte[] _blankMarker = [];

	private readonly IStreamDeckProtocol _protocol;
	private readonly IHidConnection _connection;
	private readonly TimeProvider _timeProvider;
	private readonly ByteRateLimiter? _rateLimiter;
	private readonly ILogger _logger;
	private readonly RawTrace _trace;
	private readonly ITimer? _keepAliveTimer;
	private readonly CancellationTokenSource _stop = new();
	private readonly SemaphoreSlim _signal = new(0);
	private readonly object _gate = new();
	private readonly List<WorkItem> _queue = [];
	private readonly byte[]?[] _shownKeys;
	private readonly Task _reader;
	private readonly Task _writer;
	private int _ended;
	private int _disposed;

	public StreamDeckDevice(
		StreamDeckModel model,
		string serialNumber,
		IHidConnection connection,
		TimeProvider timeProvider,
		ILogger logger,
		StreamDeckDiagnosticsOptions options,
		DeviceCounters? counters = null)
	{
		ArgumentNullException.ThrowIfNull(model);
		ArgumentException.ThrowIfNullOrEmpty(serialNumber);
		ArgumentNullException.ThrowIfNull(connection);
		ArgumentNullException.ThrowIfNull(timeProvider);
		ArgumentNullException.ThrowIfNull(logger);
		ArgumentNullException.ThrowIfNull(options);

		Model = model;
		Counters = counters ?? new DeviceCounters();
		_logger = logger.ForContext<StreamDeckDevice>();
		_trace = new RawTrace(_logger, options, timeProvider);
		SerialNumber = serialNumber;
		_connection = connection;
		_timeProvider = timeProvider;
		_protocol = StreamDeckProtocols.Create(model);
		_shownKeys = new byte[]?[model.KeyCount];
		if (model.BytesPerSecond > 0)
		{
			// Bursts of a few reports pass unthrottled, the long run is held to the model's rate.
			_rateLimiter = new ByteRateLimiter(timeProvider, model.BytesPerSecond, Math.Max(_protocol.OutputReportLength * 2, model.BytesPerSecond / 20));
		}

		if (model.SendsKeepAlive && !_protocol.KeepAliveReport.IsEmpty)
		{
			_keepAliveTimer = timeProvider.CreateTimer(_ => EnqueueKeepAlive(), null, _keepAliveInterval, _keepAliveInterval);
		}

		// The reader gets its own thread because a blocking read would otherwise pin a pool thread forever.
		_reader = Task.Factory.StartNew(ReadLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
		_writer = Task.Run(WriteLoopAsync);
	}

	public StreamDeckModel Model { get; }

	public string SerialNumber { get; }

	public bool IsHardwareVerified => Model.VerifiedOnHardware;

	public DeviceCounters Counters { get; }

	/// <summary>Raised on the reader thread with the logical key index.</summary>
	public event Action<int>? KeyDown;

	/// <summary>Raised on the reader thread with the logical key index.</summary>
	public event Action<int>? KeyUp;

	/// <summary>Raised exactly once, on a device thread, when a read or write fails. Not raised by disposal.</summary>
	public event Action<Exception>? Disconnected;

	/// <summary>
	/// Queues an encoded picture for a key. A newer picture for the same key replaces one that was not
	/// sent yet, and a picture the key already shows is not sent again. Ignored once the device ended.
	/// </summary>
	public void SetKeyImage(int key, ReadOnlyMemory<byte> encoded)
	{
		RequireDisplay();
		StreamDeckProtocols.CheckKey(key, Model.KeyCount);
		_protocol.ValidateImageLength(encoded.Length);
		// Copied because the caller is free to reuse its buffer before the writer gets here.
		Enqueue(new WorkItem(WorkKind.KeyImage, key) { Data = encoded.ToArray() });
	}

	/// <summary>Blanks a key without an image. Throws <see cref="NotSupportedException"/> on protocols without that command.</summary>
	public void ClearKey(int key)
	{
		RequireDisplay();
		StreamDeckProtocols.CheckKey(key, Model.KeyCount);
		if (!_protocol.SupportsClearKey)
		{
			throw new NotSupportedException($"{Model.Name} cannot blank a key without an image.");
		}

		Enqueue(new WorkItem(WorkKind.KeyClear, key));
	}

	public Task SetBrightnessAsync(int percent, CancellationToken cancellationToken = default)
	{
		RequireDisplay();
		ArgumentOutOfRangeException.ThrowIfNegative(percent);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
		return EnqueueAsync(new WorkItem(WorkKind.Brightness, -1) { Percent = percent }, cancellationToken);
	}

	/// <summary>Shows the built-in logo. Afterwards no key is assumed to show anything.</summary>
	public Task ShowLogoAsync(CancellationToken cancellationToken = default)
	{
		RequireDisplay();
		return EnqueueAsync(new WorkItem(WorkKind.ShowLogo, -1), cancellationToken);
	}

	public Task<string> GetFirmwareVersionAsync(CancellationToken cancellationToken = default) =>
		EnqueueTextAsync(new WorkItem(WorkKind.FirmwareVersion, -1), cancellationToken);

	public Task<string> GetSerialNumberAsync(CancellationToken cancellationToken = default) =>
		EnqueueTextAsync(new WorkItem(WorkKind.SerialNumber, -1), cancellationToken);

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}

		End(null);
		try
		{
			await Task.WhenAll(_reader, _writer).WaitAsync(_stopTimeout, _timeProvider).ConfigureAwait(false);
		}
		catch (TimeoutException)
		{
			// A read the platform could not interrupt. The thread is a background thread and ends with the process.
			return;
		}

		_stop.Dispose();
		_signal.Dispose();
	}

	private void RequireDisplay()
	{
		if (!Model.HasKeyImages)
		{
			throw new NotSupportedException($"{Model.Name} has no key displays.");
		}
	}

	private void ReadLoop()
	{
		var token = _stop.Token;
		var buffer = new byte[InputBufferLength];
		var pressed = new bool[Model.KeyCount];
		var next = new bool[Model.KeyCount];
		try
		{
			while (!token.IsCancellationRequested)
			{
				var length = _connection.ReadInputReport(buffer, token);
				Counters.ReportRead();
				_trace.Report("input", buffer.AsSpan(0, length));
				if (!_protocol.TryParseInputReport(buffer.AsSpan(0, length), next))
				{
					continue;
				}

				for (var key = 0; key < next.Length; key++)
				{
					if (next[key] == pressed[key])
					{
						continue;
					}

					pressed[key] = next[key];
					Counters.KeyEvent();
					RaiseKey(next[key] ? KeyDown : KeyUp, key);
				}
			}
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			End(ex, "read");
		}
	}

	private static void RaiseKey(Action<int>? handler, int key)
	{
		try
		{
			handler?.Invoke(key);
		}
		catch (Exception)
		{
			// The transport has no logger and a throwing subscriber must not end the reader.
		}
	}

	private async Task WriteLoopAsync()
	{
		var token = _stop.Token;
		var outputBuffer = new byte[_protocol.OutputReportLength];
		try
		{
			if (Model.HasKeyImages && !_protocol.ResetKeyStreamReport.IsEmpty)
			{
				await ThrottleAsync(_protocol.ResetKeyStreamReport.Length, token).ConfigureAwait(false);
				_trace.Report("output", _protocol.ResetKeyStreamReport.Span);
				_connection.WriteOutputReport(_protocol.ResetKeyStreamReport.Span);
			}

			while (true)
			{
				await _signal.WaitAsync(token).ConfigureAwait(false);
				var item = TakeNext();
				if (item is not null)
				{
					await ExecuteAsync(item, outputBuffer, token).ConfigureAwait(false);
				}
			}
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			Counters.WriteFailed();
			End(ex, "write");
		}
	}

	private async Task ExecuteAsync(WorkItem item, byte[] outputBuffer, CancellationToken token)
	{
		if (item.Done?.Task.IsCompleted == true)
		{
			return;
		}

		string? text = null;
		switch (item.Kind)
		{
			case WorkKind.KeyImage:
				await SendKeyImageAsync(item.Key, item.Data!, outputBuffer, token).ConfigureAwait(false);
				break;
			case WorkKind.KeyClear:
				if (!ReferenceEquals(_shownKeys[item.Key], _blankMarker))
				{
					var clear = new byte[_protocol.FeatureReportLength];
					_protocol.BuildClearKeyReport(item.Key, clear);
					await ThrottleAsync(clear.Length, token).ConfigureAwait(false);
					_trace.Report("feature", clear);
					_connection.SetFeatureReport(clear);
					_shownKeys[item.Key] = _blankMarker;
				}

				break;
			case WorkKind.Brightness:
				var brightness = new byte[_protocol.FeatureReportLength];
				_protocol.BuildBrightnessReport(item.Percent, brightness);
				await ThrottleAsync(brightness.Length, token).ConfigureAwait(false);
				_trace.Report("feature", brightness);
				_connection.SetFeatureReport(brightness);
				break;
			case WorkKind.ShowLogo:
				var logo = new byte[_protocol.FeatureReportLength];
				_protocol.BuildShowLogoReport(logo);
				await ThrottleAsync(logo.Length, token).ConfigureAwait(false);
				_trace.Report("feature", logo);
				_connection.SetFeatureReport(logo);
				Array.Clear(_shownKeys);
				break;
			case WorkKind.KeepAlive:
				await ThrottleAsync(_protocol.KeepAliveReport.Length, token).ConfigureAwait(false);
				_trace.Report("feature", _protocol.KeepAliveReport.Span);
				_connection.SetFeatureReport(_protocol.KeepAliveReport.Span);
				break;
			case WorkKind.SerialNumber:
			case WorkKind.FirmwareVersion:
				try
				{
					text = item.Kind == WorkKind.SerialNumber
						? ReadFeatureText(_protocol.SerialRequests, _protocol.ParseSerialNumber)
						: ReadFeatureText(_protocol.FirmwareRequests, _protocol.ParseFirmwareVersion);
				}
				catch (IOException ex)
				{
					item.Fail(ex);
					return;
				}

				break;
		}

		item.Complete(text);
	}

	private async Task SendKeyImageAsync(int key, byte[] image, byte[] outputBuffer, CancellationToken token)
	{
		var hash = SHA256.HashData(image);
		if (_shownKeys[key] is { } shown && shown.AsSpan().SequenceEqual(hash))
		{
			Counters.ImageSkipped();
			return;
		}

		// All chunks go out back to back: the writer takes nothing else until the last one is written.
		var count = _protocol.GetKeyImageReportCount(image.Length);
		for (var index = 0; index < count; index++)
		{
			_protocol.BuildKeyImageReport(key, image, index, outputBuffer);
			if (index == 0)
			{
				_trace.Image(key, image.Length, count, hash, outputBuffer.AsSpan(0, _protocol.KeyImageHeaderLength));
			}

			await ThrottleAsync(outputBuffer.Length, token).ConfigureAwait(false);
			_connection.WriteOutputReport(outputBuffer);
		}

		_shownKeys[key] = hash;
		Counters.ImageWritten();
	}

	// A failed read is not fatal here: families disagree on report ids and lengths, so candidates are tried in turn.
	private string ReadFeatureText(IReadOnlyList<FeatureRequest> requests, TextParser parse)
	{
		IOException? last = null;
		foreach (var request in requests)
		{
			var buffer = new byte[request.Length];
			buffer[0] = request.ReportId;
			try
			{
				_connection.GetFeatureReport(buffer);
				return parse(buffer);
			}
			catch (IOException ex)
			{
				last = ex;
			}
		}

		throw last ?? new IOException("No feature request is defined.");
	}

	private async ValueTask ThrottleAsync(int bytes, CancellationToken token)
	{
		var wait = _rateLimiter?.Reserve(bytes) ?? TimeSpan.Zero;
		if (wait > TimeSpan.Zero)
		{
			await Task.Delay(wait, _timeProvider, token).ConfigureAwait(false);
		}
	}

	private void Enqueue(WorkItem item)
	{
		lock (_gate)
		{
			if (Volatile.Read(ref _ended) != 0)
			{
				return;
			}

			if (item.Kind is WorkKind.KeyImage or WorkKind.KeyClear)
			{
				// Latest wins, and it also takes the latest place so it cannot jump ahead of a later logo or brightness.
				_queue.RemoveAll(queued => queued.Kind is WorkKind.KeyImage or WorkKind.KeyClear && queued.Key == item.Key);
			}

			_queue.Add(item);
		}

		Signal();
	}

	private Task<string?> EnqueueAsync(WorkItem item, CancellationToken cancellationToken)
	{
		var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
		item.Done = done;
		if (cancellationToken.CanBeCanceled)
		{
			item.CancelRegistration = cancellationToken.Register(static state => ((TaskCompletionSource<string?>)state!).TrySetCanceled(), done);
		}

		lock (_gate)
		{
			if (Volatile.Read(ref _ended) != 0)
			{
				item.Fail(new InvalidOperationException("The device is disconnected."));
				return done.Task;
			}

			_queue.Add(item);
		}

		Signal();
		return done.Task;
	}

	private async Task<string> EnqueueTextAsync(WorkItem item, CancellationToken cancellationToken) =>
		await EnqueueAsync(item, cancellationToken).ConfigureAwait(false) ?? string.Empty;

	private void EnqueueKeepAlive()
	{
		lock (_gate)
		{
			if (Volatile.Read(ref _ended) != 0 || _queue.Any(queued => queued.Kind == WorkKind.KeepAlive))
			{
				return;
			}

			_queue.Add(new WorkItem(WorkKind.KeepAlive, -1));
		}

		Signal();
	}

	private void Signal()
	{
		try
		{
			_signal.Release();
		}
		catch (ObjectDisposedException)
		{
			// Disposal won the race, nothing is left to wake.
		}
	}

	private WorkItem? TakeNext()
	{
		lock (_gate)
		{
			if (_queue.Count == 0)
			{
				return null;
			}

			var item = _queue[0];
			_queue.RemoveAt(0);
			return item;
		}
	}

	// The first caller wins: it closes the connection, releases waiters and, for a failure, raises Disconnected.
	private void End(Exception? failure, string operation = "")
	{
		if (Interlocked.Exchange(ref _ended, 1) != 0)
		{
			return;
		}

		_keepAliveTimer?.Dispose();
		if (failure is not null)
		{
			Counters.SetLastError($"{operation} failure: {failure.Message}");
			_logger.Warning("Stream Deck {Model:l} ended after a {Operation:l} failure: {Error:l}", Model.Name, operation, failure.Message);
		}

		_stop.Cancel();
		_connection.Dispose();

		List<WorkItem> abandoned;
		lock (_gate)
		{
			abandoned = [.. _queue];
			_queue.Clear();
		}

		var reason = failure ?? new InvalidOperationException("The device was disposed.");
		foreach (var item in abandoned)
		{
			item.Fail(reason);
		}

		if (failure is not null)
		{
			try
			{
				Disconnected?.Invoke(failure);
			}
			catch (Exception)
			{
				// A throwing subscriber must not break teardown.
			}
		}
	}

	private delegate string TextParser(ReadOnlySpan<byte> report);

	private enum WorkKind
	{
		KeyImage,
		KeyClear,
		Brightness,
		ShowLogo,
		KeepAlive,
		SerialNumber,
		FirmwareVersion,
	}

	private sealed class WorkItem(WorkKind kind, int key)
	{
		public WorkKind Kind { get; } = kind;

		public int Key { get; } = key;

		public byte[]? Data { get; init; }

		public int Percent { get; init; }

		public TaskCompletionSource<string?>? Done { get; set; }

		public CancellationTokenRegistration CancelRegistration { get; set; }

		public void Complete(string? text)
		{
			CancelRegistration.Dispose();
			Done?.TrySetResult(text);
		}

		public void Fail(Exception exception)
		{
			CancelRegistration.Dispose();
			Done?.TrySetException(exception);
		}
	}
}
