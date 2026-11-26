namespace StreamDeckConnector.Devices;

/// <summary>
/// Token bucket in bytes per second. A caller reserves bytes and gets the time it has to wait, so a
/// burst larger than the bucket still goes through, only later. Not thread safe: one writer owns it.
/// </summary>
public sealed class ByteRateLimiter
{
	private readonly TimeProvider _timeProvider;
	private readonly double _bytesPerSecond;
	private readonly double _capacity;
	private double _tokens;
	private long _lastTimestamp;

	public ByteRateLimiter(TimeProvider timeProvider, int bytesPerSecond, int burstBytes)
	{
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bytesPerSecond, 0);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(burstBytes, 0);
		_timeProvider = timeProvider;
		_bytesPerSecond = bytesPerSecond;
		_capacity = burstBytes;
		_tokens = burstBytes;
		_lastTimestamp = timeProvider.GetTimestamp();
	}

	public TimeSpan Reserve(int bytes)
	{
		var now = _timeProvider.GetTimestamp();
		var elapsed = _timeProvider.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
		_lastTimestamp = now;
		_tokens = Math.Min(_capacity, _tokens + (elapsed * _bytesPerSecond));
		_tokens -= bytes;
		return _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / _bytesPerSecond);
	}
}
