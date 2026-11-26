namespace StreamDeckConnector.Devices;

public enum StreamDeckImageFormat
{
	None,
	Jpeg,
	Bmp,
}

public enum StreamDeckProtocolFamily
{
	Gen2,
	OriginalV1,
	Mini,
}

/// <summary>
/// One supported Stream Deck model. Keys are numbered row-major from the top left, and only keys count:
/// dials, touch strips, touch keys and the Neo info screen are not part of the model.
/// </summary>
/// <param name="VerifiedOnHardware">Whether the model was exercised on real hardware by the maintainers. Everything else is built from the protocol sources only.</param>
/// <param name="SendsKeepAlive">Whether the device needs a periodic feature report to stay awake (Galleon K100 SD).</param>
/// <param name="KeyPixelSize">Edge length of the square key image in pixels, 0 when the model has no key displays.</param>
/// <param name="RotationDegrees">Clockwise rotation the hardware expects the picture to have before encoding.</param>
/// <param name="MirrorHorizontal">Whether the picture must be mirrored left to right before encoding.</param>
/// <param name="BytesPerSecond">Recommended sustained output rate, 0 when nothing is ever sent.</param>
public sealed record StreamDeckModel(
	string Name,
	int VendorId,
	int ProductId,
	int Rows,
	int Columns,
	int KeyPixelSize,
	StreamDeckImageFormat ImageFormat,
	int RotationDegrees,
	bool MirrorHorizontal,
	StreamDeckProtocolFamily Family,
	int BytesPerSecond,
	bool VerifiedOnHardware = false,
	bool SendsKeepAlive = false)
{
	public int KeyCount => Rows * Columns;

	public bool HasKeyImages => ImageFormat != StreamDeckImageFormat.None;
}
