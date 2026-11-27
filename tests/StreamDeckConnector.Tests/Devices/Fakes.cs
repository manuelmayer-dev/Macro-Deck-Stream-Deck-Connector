using System.Collections.Concurrent;
using System.Threading.Channels;
using StreamDeckConnector.Hid;

namespace StreamDeckConnector.Tests.Devices;

public enum WriteKind
{
	Output,
	SetFeature,
}

public sealed record RecordedWrite(WriteKind Kind, byte[] Bytes);

public sealed class FakeHidConnection : IHidConnection
{
	private readonly BlockingCollection<object> _inputs = [];
	private readonly CancellationTokenSource _disposed = new();
	private readonly SemaphoreSlim _writeSignal = new(0);
	private readonly SemaphoreSlim _featureAttempt = new(0);
	private readonly List<RecordedWrite> _writes = [];
	private readonly object _lock = new();

	/// <summary>Runs on the writer thread before a write is recorded, with the index of that write.</summary>
	public Action<int>? BeforeWrite { get; set; }

	public Exception? WriteFailure { get; set; }

	/// <summary>Thrown by set feature requests only, after <see cref="BeforeWrite"/> ran.</summary>
	public Exception? FeatureFailure { get; set; }

	public Func<byte[], byte[]>? FeatureResponder { get; set; }

	public bool IsDisposed => _disposed.IsCancellationRequested;

	public IReadOnlyList<RecordedWrite> Writes
	{
		get
		{
			lock (_lock)
			{
				return [.. _writes];
			}
		}
	}

	public void EnqueueInput(params byte[] report) => _inputs.Add(report);

	public void FailRead(Exception exception) => _inputs.Add(exception);

	/// <summary>Waits until <paramref name="count"/> more writes happened since the last call.</summary>
	public async Task<IReadOnlyList<RecordedWrite>> WaitForWritesAsync(int count)
	{
		for (var i = 0; i < count; i++)
		{
			await _writeSignal.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10));
		}

		return Writes;
	}

	public int ReadInputReport(Span<byte> destination, CancellationToken cancellationToken)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
		object next;
		try
		{
			next = _inputs.Take(linked.Token);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new IOException("The connection was disposed.");
		}

		if (next is Exception failure)
		{
			throw failure;
		}

		var report = (byte[])next;
		report.CopyTo(destination);
		return report.Length;
	}

	public void WriteOutputReport(ReadOnlySpan<byte> report) => Record(WriteKind.Output, report);

	/// <summary>Waits until the writer attempted <paramref name="count"/> more set feature requests, refused ones included.</summary>
	public async Task WaitForFeatureAttemptsAsync(int count)
	{
		for (var i = 0; i < count; i++)
		{
			await _featureAttempt.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10));
		}
	}

	public void SetFeatureReport(ReadOnlySpan<byte> report) => Record(WriteKind.SetFeature, report);

	public void GetFeatureReport(Span<byte> buffer)
	{
		if (FeatureResponder is null)
		{
			throw new IOException("No feature response configured.");
		}

		FeatureResponder(buffer.ToArray()).CopyTo(buffer);
	}

	public void Dispose() => _disposed.Cancel();

	private void Record(WriteKind kind, ReadOnlySpan<byte> report)
	{
		int index;
		lock (_lock)
		{
			index = _writes.Count;
		}

		BeforeWrite?.Invoke(index);
		if (kind == WriteKind.SetFeature)
		{
			_featureAttempt.Release();
			if (FeatureFailure is { } refused)
			{
				throw refused;
			}
		}

		if (WriteFailure is { } failure)
		{
			throw failure;
		}

		lock (_lock)
		{
			_writes.Add(new RecordedWrite(kind, report.ToArray()));
		}

		_writeSignal.Release();
	}
}

public sealed class FakeTimeProvider : TimeProvider
{
	private readonly object _lock = new();
	private readonly List<FakeTimer> _timers = [];
	private long _now;
	private TaskCompletionSource? _timerCreated;

	public override long TimestampFrequency => TimeSpan.TicksPerSecond;

	public override long GetTimestamp()
	{
		lock (_lock)
		{
			return _now;
		}
	}

	public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
	{
		var timer = new FakeTimer(this, callback, state);
		timer.Change(dueTime, period);
		return timer;
	}

	public bool HasActiveTimer
	{
		get
		{
			lock (_lock)
			{
				return _timers.Any(t => t.IsScheduled);
			}
		}
	}

	/// <summary>Completes when a timer is waiting. A timer that already fired no longer counts.</summary>
	public Task WaitForActiveTimerAsync()
	{
		lock (_lock)
		{
			if (_timers.Any(t => t.IsScheduled))
			{
				return Task.CompletedTask;
			}

			_timerCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			return _timerCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));
		}
	}

	public void Advance(TimeSpan by)
	{
		List<FakeTimer> due;
		lock (_lock)
		{
			_now += by.Ticks;
			due = [.. _timers.Where(t => t.IsScheduled && t.DueAt <= _now)];
			foreach (var timer in due)
			{
				// A periodic timer stays scheduled one period from now, a one-shot timer is done.
				if (timer.Period is { } period)
				{
					timer.DueAt = _now + period.Ticks;
				}
				else
				{
					timer.DueAt = null;
					_timers.Remove(timer);
				}
			}
		}

		foreach (var timer in due)
		{
			timer.Fire();
		}
	}

	private void Schedule(FakeTimer timer, TimeSpan dueTime, TimeSpan period)
	{
		lock (_lock)
		{
			_timers.Remove(timer);
			timer.Period = period > TimeSpan.Zero && period != Timeout.InfiniteTimeSpan ? period : null;
			if (dueTime == Timeout.InfiniteTimeSpan)
			{
				timer.DueAt = null;
				return;
			}

			timer.DueAt = _now + dueTime.Ticks;
			_timers.Add(timer);
			_timerCreated?.TrySetResult();
		}
	}

	private void Remove(FakeTimer timer)
	{
		lock (_lock)
		{
			_timers.Remove(timer);
		}
	}

	private sealed class FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state) : ITimer
	{
		public long? DueAt { get; set; }

		public TimeSpan? Period { get; set; }

		public bool IsScheduled => DueAt is not null;

		public bool Change(TimeSpan dueTime, TimeSpan period)
		{
			owner.Schedule(this, dueTime, period);
			return true;
		}

		public void Fire() => callback(state);

		public void Dispose() => owner.Remove(this);

		public ValueTask DisposeAsync()
		{
			Dispose();
			return ValueTask.CompletedTask;
		}
	}
}

public sealed class FakeHidDeviceSource : IHidDeviceSource
{
	private readonly object _lock = new();
	private readonly List<HidDeviceInfo> _present = [];

	public Dictionary<string, FakeHidConnection> Connections { get; } = [];

	public Func<HidDeviceInfo, Exception?>? OpenFailure { get; set; }

	public Func<byte[], byte[]>? FeatureResponder { get; set; }

	/// <summary>Thrown by <see cref="Enumerate"/> while set.</summary>
	public Exception? EnumerateFailure { get; set; }

	/// <summary>Runs on every new connection before the device gets it.</summary>
	public Action<HidDeviceInfo, FakeHidConnection>? ConfigureConnection { get; set; }

	public event EventHandler? Changed;

	public void Plug(HidDeviceInfo info)
	{
		lock (_lock)
		{
			_present.Add(info);
		}
	}

	public void Unplug(string path)
	{
		lock (_lock)
		{
			_present.RemoveAll(d => d.Path == path);
		}
	}

	public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

	public IReadOnlyList<HidDeviceInfo> Enumerate()
	{
		if (EnumerateFailure is { } failure)
		{
			throw failure;
		}

		lock (_lock)
		{
			return [.. _present];
		}
	}

	public IHidConnection Open(HidDeviceInfo device)
	{
		if (OpenFailure?.Invoke(device) is { } failure)
		{
			throw failure;
		}

		var connection = new FakeHidConnection { FeatureResponder = FeatureResponder };
		ConfigureConnection?.Invoke(device, connection);
		lock (_lock)
		{
			Connections[device.Path] = connection;
		}

		return connection;
	}
}

/// <summary>Collects events from any thread and lets a test wait for the next one without sleeping.</summary>
public sealed class EventRecorder<T>
{
	private readonly object _lock = new();
	private readonly List<T> _events = [];
	private readonly Channel<bool> _signal = Channel.CreateUnbounded<bool>();

	public IReadOnlyList<T> Events
	{
		get
		{
			lock (_lock)
			{
				return [.. _events];
			}
		}
	}

	public void Add(T item)
	{
		lock (_lock)
		{
			_events.Add(item);
		}

		_signal.Writer.TryWrite(true);
	}

	public async Task<IReadOnlyList<T>> WaitForAsync(int count)
	{
		for (var i = 0; i < count; i++)
		{
			await _signal.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
		}

		return Events;
	}
}

public sealed class CollectingSink : Serilog.Core.ILogEventSink
{
	private readonly object _lock = new();
	private readonly List<Serilog.Events.LogEvent> _events = [];
	private readonly List<(Func<Serilog.Events.LogEvent, bool> Match, TaskCompletionSource<Serilog.Events.LogEvent> Done)> _waiters = [];

	public IReadOnlyList<Serilog.Events.LogEvent> Events
	{
		get
		{
			lock (_lock)
			{
				return [.. _events];
			}
		}
	}

	public static (Serilog.ILogger Logger, CollectingSink Sink) Create()
	{
		var sink = new CollectingSink();
		var logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
		return (logger, sink);
	}

	public void Emit(Serilog.Events.LogEvent logEvent)
	{
		lock (_lock)
		{
			_events.Add(logEvent);
			foreach (var waiter in _waiters.Where(w => w.Match(logEvent)).ToList())
			{
				waiter.Done.TrySetResult(logEvent);
			}
		}
	}

	public IReadOnlyList<string> Messages(Serilog.Events.LogEventLevel level) =>
		[.. Events.Where(e => e.Level == level).Select(e => e.RenderMessage(System.Globalization.CultureInfo.InvariantCulture))];

	public Task<Serilog.Events.LogEvent> WaitForAsync(Func<Serilog.Events.LogEvent, bool> match)
	{
		lock (_lock)
		{
			var existing = _events.FirstOrDefault(match);
			if (existing is not null)
			{
				return Task.FromResult(existing);
			}

			var done = new TaskCompletionSource<Serilog.Events.LogEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
			_waiters.Add((match, done));
			return done.Task.WaitAsync(TimeSpan.FromSeconds(10));
		}
	}
}
