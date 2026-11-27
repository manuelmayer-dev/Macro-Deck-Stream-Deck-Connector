using System.Security.Cryptography;
using Serilog;
using StreamDeckConnector.Hid;

namespace StreamDeckConnector.Devices;

/// <summary>
/// One opened Stream Deck. A dedicated reader raises key events straight from input reports and never
/// waits behind writes. One writer owns every output and feature report, so reports of different keys
/// never interleave. Pictures arrive already encoded for the model. Nothing touches the connection
/// until <see cref="Start"/>, so handlers can be attached first without racing the device threads.
/// </summary>
public sealed class StreamDeckDevice : IAsyncDisposable
{
	private const int InputBufferLength = 1024;
	private static readonly TimeSpan _stopTimeout = TimeSpan.FromSeconds(2);
	private static readonly TimeSpan _keepAliveInterval = TimeSpan.FromMilliseconds(500);

	private readonly IStreamDeckProtocol _protocol;
	private readonly IHidConnection _connection;
	private readonly TimeProvider _timeProvider;
	private readonly ByteRateLimiter? _rateLimiter;
	private readonly ILogger _logger;
	private readonly RawTrace _trace;
	private readonly CancellationTokenSource _stop = new();
	private readonly SemaphoreSlim _signal = new(0);
	private readonly object _gate = new();
	private readonly List<WorkItem> _queue = [];
	private readonly byte[]?[] _shownKeys;
	private ITimer? _keepAliveTimer;
	private Task _reader = Task.CompletedTask;
	private Task _writer = Task.CompletedTask;
	private Exception? _endReason;
	private bool _featureFailureLogged;
	private int _started;
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
	}

	public StreamDeckModel Model { get; }

	public string SerialNumber { get; }

	public bool IsHardwareVerified => Model.VerifiedOnHardware;

	public DeviceCounters Counters { get; }

	/// <summary>Raised on the reader thread with the logical key index.</summary>
	public event Action<int>? KeyDown;

	/// <summary>Raised on the reader thread with the logical key index.</summary>
	public event Action<int>? KeyUp;

	/// <summary>
	/// Raised exactly once, on a device thread, when the read loop or an image or reset write fails. Not
	/// raised by disposal, and not by a refused brightness, logo or keep-alive report.
	/// </summary>
	public event Action<Exception>? Disconnected;

	/// <summary>
	/// Reads the serial feature report straight from a connection that no device owns yet. Throws when
	/// every candidate request fails or the answer is empty.
	/// </summary>
	public static string ReadSerialNumber(StreamDeckModel model, IHidConnection connection)
	{
		ArgumentNullException.ThrowIfNull(model);
		ArgumentNullException.ThrowIfNull(connection);
		var protocol = StreamDeckProtocols.Create(model);
		var serial = ReadFeatureText(connection, protocol.SerialRequests, protocol.ParseSerialNumber).Trim();
		return serial.Length > 0 ? serial : throw new IOException("The serial feature report is empty.");
	}

	/// <summary>Starts the reader, the writer and the keep-alive. Work queued before is sent first.</summary>
	public void Start()
	{
		if (Interlocked.Exchange(ref _started, 1) != 0)
		{
			return;
		}

		lock (_gate)
		{
			if (Volatile.Read(ref _ended) != 0)
			{
				return;
			}

			// The reader gets its own thread because a blocking read would otherwise pin a pool thread forever.
			_reader = Task.Factory.StartNew(ReadLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
			_writer = Task.Run(WriteLoopAsync);
			if (Model.SendsKeepAlive && !_protocol.KeepAliveReport.IsEmpty)
			{
				_keepAliveTimer = _timeProvider.CreateTimer(_ => EnqueueKeepAlive(), null, _keepAliveInterval, _keepAliveInterval);
			}
		}
	}

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

	/// <summary>
	/// Completes once the report was sent. Faults with the HID error when the device refused it (the device
	/// stays open) and with <see cref="InvalidOperationException"/> when the device ended first.
	/// </summary>
	public Task SetBrightnessAsync(int percent, CancellationToken cancellationToken = default)
	{
		RequireDisplay();
		ArgumentOutOfRangeException.ThrowIfNegative(percent);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
		return EnqueueAsync(new WorkItem(WorkKind.Brightness, -1) { Percent = percent }, cancellationToken);
	}

	/// <summary>Shows the built-in logo. Afterwards no key is assumed to show anything. Fails like <see cref="SetBrightnessAsync"/>.</summary>
	public Task ShowLogoAsync(CancellationToken cancellationToken = default)
	{
		RequireDisplay();
		return EnqueueAsync(new WorkItem(WorkKind.ShowLogo, -1), cancellationToken);
	}

	public async Task<string> GetFirmwareVersionAsync(CancellationToken cancellationToken = default) =>
		await EnqueueAsync(new WorkItem(WorkKind.FirmwareVersion, -1), cancellationToken).ConfigureAwait(false) ?? string.Empty;

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}

		End(null);
		Task reader;
		Task writer;
		lock (_gate)
		{
			reader = _reader;
			writer = _writer;
		}

		try
		{
			await Task.WhenAll(reader, writer).WaitAsync(_stopTimeout, _timeProvider).ConfigureAwait(false);
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
		catch (Exception) when (token.IsCancellationRequested)
		{
			// Ended or disposed: a read interrupted by closing the connection is not a failure of its own.
		}
		catch (Exception ex) when (!Failures.IsFatal(ex))
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
		WorkItem? current = null;
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
				current = TakeNext();
				if (current is not null)
				{
					await ExecuteAsync(current, outputBuffer, token).ConfigureAwait(false);
					current = null;
				}
			}
		}
		catch (Exception) when (token.IsCancellationRequested)
		{
			// Ended or disposed: whatever the closed connection threw is not a failure of its own.
		}
		catch (Exception ex) when (!Failures.IsFatal(ex))
		{
			Counters.WriteFailed();
			End(ex, "write");
		}
		finally
		{
			// End only fails what is still queued. The item in hand was already taken off the queue.
			current?.Fail(Volatile.Read(ref _endReason) ?? new InvalidOperationException("The device is disconnected."));
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
			case WorkKind.Brightness:
				var brightness = new byte[_protocol.FeatureReportLength];
				_protocol.BuildBrightnessReport(item.Percent, brightness);
				if (!await TrySendFeatureAsync(item, brightness, token).ConfigureAwait(false))
				{
					return;
				}

				break;
			case WorkKind.ShowLogo:
				var logo = new byte[_protocol.FeatureReportLength];
				_protocol.BuildShowLogoReport(logo);
				// Cleared even when refused: the keys may show anything now, so every picture is sent again.
				Array.Clear(_shownKeys);
				if (!await TrySendFeatureAsync(item, logo, token).ConfigureAwait(false))
				{
					return;
				}

				break;
			case WorkKind.KeepAlive:
				await TrySendFeatureAsync(item, _protocol.KeepAliveReport, token).ConfigureAwait(false);
				return;
			case WorkKind.FirmwareVersion:
				try
				{
					text = ReadFeatureText(_connection, _protocol.FirmwareRequests, _protocol.ParseFirmwareVersion);
				}
				catch (Exception ex) when (!token.IsCancellationRequested && !Failures.IsFatal(ex))
				{
					item.Fail(ex);
					return;
				}

				break;
		}

		item.Complete(text);
	}

	// A refused feature report fails its command but leaves the device open. A device that is really gone
	// is noticed by the reader, whose blocking read fails, and by the source's change notification.
	private async Task<bool> TrySendFeatureAsync(WorkItem item, ReadOnlyMemory<byte> report, CancellationToken token)
	{
		await ThrottleAsync(report.Length, token).ConfigureAwait(false);
		_trace.Report("feature", report.Span);
		try
		{
			_connection.SetFeatureReport(report.Span);
		}
		catch (Exception ex) when (!token.IsCancellationRequested && !Failures.IsFatal(ex))
		{
			Counters.WriteFailed();
			Counters.SetLastError($"{item.Kind} feature report refused: {ex.Message}");
			// The keep-alive repeats every 500 ms, so only the first refusal of a run is a warning.
			if (_featureFailureLogged)
			{
				_logger.Debug("Stream Deck {Model:l} refused the {Command} feature report: {Error:l}", Model.Name, item.Kind, ex.Message);
			}
			else
			{
				_featureFailureLogged = true;
				_logger.Warning(
					"Stream Deck {Model:l} refused the {Command} feature report: {Error:l}. The device stays open. If this keeps happening, please open an issue at "
					+ StreamDeckSupport.IssuesUrl + " and attach the diagnostics snapshot.",
					Model.Name,
					item.Kind,
					ex.Message);
			}

			item.Fail(ex);
			return false;
		}

		_featureFailureLogged = false;
		return true;
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
	private static string ReadFeatureText(IHidConnection connection, IReadOnlyList<FeatureRequest> requests, TextParser parse)
	{
		Exception? last = null;
		foreach (var request in requests)
		{
			var buffer = new byte[request.Length];
			buffer[0] = request.ReportId;
			try
			{
				connection.GetFeatureReport(buffer);
				return parse(buffer);
			}
			catch (Exception ex) when (!Failures.IsFatal(ex))
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

			if (item.Kind == WorkKind.KeyImage)
			{
				// Latest wins, and it also takes the latest place so it cannot jump ahead of a later logo or brightness.
				_queue.RemoveAll(queued => queued.Kind == WorkKind.KeyImage && queued.Key == item.Key);
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

	// The first caller wins: it closes the connection, fails queued commands and, for a failure, raises Disconnected.
	private void End(Exception? failure, string operation = "")
	{
		if (Interlocked.Exchange(ref _ended, 1) != 0)
		{
			return;
		}

		var reason = failure ?? new InvalidOperationException("The device was disposed.");
		Volatile.Write(ref _endReason, reason);
		lock (_gate)
		{
			_keepAliveTimer?.Dispose();
		}

		if (failure is not null)
		{
			Counters.SetLastError($"{operation} failure: {failure.Message}");
			_logger.Warning(
				"Stream Deck {Model:l} ended after a {Operation:l} failure: {Error:l}. If it was not unplugged, please open an issue at "
				+ StreamDeckSupport.IssuesUrl + " and attach the diagnostics snapshot.",
				Model.Name,
				operation,
				failure.Message);
		}

		_stop.Cancel();
		_connection.Dispose();

		List<WorkItem> abandoned;
		lock (_gate)
		{
			abandoned = [.. _queue];
			_queue.Clear();
		}

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
		Brightness,
		ShowLogo,
		KeepAlive,
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
