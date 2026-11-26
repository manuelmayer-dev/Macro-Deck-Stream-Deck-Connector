namespace StreamDeckConnector.Hid;

/// <summary>
/// One open HID device. Report buffers include the report id as their first byte. Each member is
/// called from one thread at a time: reads from the reader, everything else from the writer.
/// </summary>
public interface IHidConnection : IDisposable
{
	/// <summary>
	/// Blocks until an input report arrives and returns its length, truncated to the buffer. Throws
	/// <see cref="OperationCanceledException"/> when cancelled and <see cref="IOException"/> when the device is gone.
	/// Disposing the connection also unblocks a pending read.
	/// </summary>
	int ReadInputReport(Span<byte> destination, CancellationToken cancellationToken);

	void WriteOutputReport(ReadOnlySpan<byte> report);

	void SetFeatureReport(ReadOnlySpan<byte> report);

	/// <summary>Sends a get feature request: byte 0 of <paramref name="buffer"/> is the report id, the rest is filled in.</summary>
	void GetFeatureReport(Span<byte> buffer);
}
