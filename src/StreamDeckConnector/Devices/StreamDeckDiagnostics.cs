using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace StreamDeckConnector.Devices;

public static class StreamDeckSupport
{
	public const string IssuesUrl = "https://github.com/manuelmayer-dev/MacroDeckStreamDeckPlugin/issues";
}

/// <summary>Switched at runtime by the integration. The raw trace is off by default.</summary>
public sealed class StreamDeckDiagnosticsOptions
{
	private volatile bool _traceRawReports;
	private int _maxBytesPerTraceLine = 32;
	private int _maxTraceLinesPerSecond = 20;

	public bool TraceRawReports
	{
		get => _traceRawReports;
		set => _traceRawReports = value;
	}

	public int MaxBytesPerTraceLine
	{
		get => Volatile.Read(ref _maxBytesPerTraceLine);
		set => Volatile.Write(ref _maxBytesPerTraceLine, Math.Max(1, value));
	}

	public int MaxTraceLinesPerSecond
	{
		get => Volatile.Read(ref _maxTraceLinesPerSecond);
		set => Volatile.Write(ref _maxTraceLinesPerSecond, Math.Max(1, value));
	}
}

public static class SerialMask
{
	/// <summary>Only the last four characters of a serial number may reach a log or a snapshot.</summary>
	public static string Tail(string? serial)
	{
		if (string.IsNullOrEmpty(serial))
		{
			return "none reported";
		}

		return serial.Length <= 4 ? "****" : serial[^4..];
	}
}

public readonly record struct DeviceCounterSnapshot(long ReportsRead, long KeyEvents, long ImagesWritten, long ImagesSkipped, long WriteFailures, string? LastError);

/// <summary>Thread safe counters. One instance can outlive a device so that totals survive a reconnect.</summary>
public sealed class DeviceCounters
{
	private long _reportsRead;
	private long _keyEvents;
	private long _imagesWritten;
	private long _imagesSkipped;
	private long _writeFailures;
	private string? _lastError;

	public void ReportRead() => Interlocked.Increment(ref _reportsRead);

	public void KeyEvent() => Interlocked.Increment(ref _keyEvents);

	public void ImageWritten() => Interlocked.Increment(ref _imagesWritten);

	public void ImageSkipped() => Interlocked.Increment(ref _imagesSkipped);

	public void WriteFailed() => Interlocked.Increment(ref _writeFailures);

	public void SetLastError(string error) => Volatile.Write(ref _lastError, error);

	public DeviceCounterSnapshot Snapshot() => new(
		Interlocked.Read(ref _reportsRead),
		Interlocked.Read(ref _keyEvents),
		Interlocked.Read(ref _imagesWritten),
		Interlocked.Read(ref _imagesSkipped),
		Interlocked.Read(ref _writeFailures),
		Volatile.Read(ref _lastError));
}

/// <summary>
/// Opt-in hex trace at Debug. Capped in bytes per line and lines per second so it cannot flood the log.
/// Image payloads are never dumped, only their length and hash.
/// </summary>
public sealed class RawTrace
{
	private readonly ILogger _logger;
	private readonly StreamDeckDiagnosticsOptions _options;
	private readonly TimeProvider _timeProvider;
	private readonly object _lock = new();
	private long _windowStart;
	private int _linesInWindow;
	private int _suppressed;

	public RawTrace(ILogger logger, StreamDeckDiagnosticsOptions options, TimeProvider timeProvider)
	{
		_logger = logger;
		_options = options;
		_timeProvider = timeProvider;
		_windowStart = timeProvider.GetTimestamp();
	}

	public bool Enabled => _options.TraceRawReports;

	public void Report(string direction, ReadOnlySpan<byte> bytes)
	{
		if (!Enabled || !TryTake(out var suppressed))
		{
			return;
		}

		_logger.Debug("Raw trace {Direction:l}, {Length} bytes: {Hex:l}{Suppressed:l}", direction, bytes.Length, Hex(bytes), SuppressedNote(suppressed));
	}

	/// <param name="header">The header bytes of the first report. The picture bytes behind it are not traced.</param>
	public void Image(int key, int length, int reports, ReadOnlySpan<byte> sha256, ReadOnlySpan<byte> header)
	{
		if (!Enabled || !TryTake(out var suppressed))
		{
			return;
		}

		_logger.Debug(
			"Raw trace image for key {Key}, {Length} bytes in {Reports} reports, sha256 {Hash:l}, first header: {Hex:l}{Suppressed:l}",
			key,
			length,
			reports,
			Convert.ToHexString(sha256[..8]),
			Hex(header),
			SuppressedNote(suppressed));
	}

	private static string SuppressedNote(int suppressed) => suppressed > 0 ? $" ({suppressed} earlier trace lines dropped)" : string.Empty;

	private string Hex(ReadOnlySpan<byte> bytes)
	{
		var cap = _options.MaxBytesPerTraceLine;
		var shown = bytes[..Math.Min(bytes.Length, cap)];
		var text = Convert.ToHexString(shown);
		var spaced = string.Join(' ', Enumerable.Range(0, shown.Length).Select(i => text.Substring(i * 2, 2)));
		return shown.Length < bytes.Length ? spaced + " ..." : spaced;
	}

	private bool TryTake(out int suppressed)
	{
		lock (_lock)
		{
			var now = _timeProvider.GetTimestamp();
			if (_timeProvider.GetElapsedTime(_windowStart, now) >= TimeSpan.FromSeconds(1))
			{
				_windowStart = now;
				_linesInWindow = 0;
			}

			if (_linesInWindow >= _options.MaxTraceLinesPerSecond)
			{
				_suppressed++;
				suppressed = 0;
				return false;
			}

			_linesInWindow++;
			suppressed = _suppressed;
			_suppressed = 0;
			return true;
		}
	}
}

/// <param name="Key">Stable for as long as the device stays attached, derived from its path.</param>
public sealed record UnsupportedDevice(string Key, int VendorId, int ProductId, string? ProductName, int InputReportLength, int OutputReportLength, int FeatureReportLength)
{
	public static string KeyFor(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..12];
}
