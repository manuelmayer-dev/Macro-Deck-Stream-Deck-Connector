namespace StreamDeckConnector.Devices;

public readonly record struct FeatureRequest(byte ReportId, int Length);

/// <summary>
/// The byte level protocol of one device family. Pure functions over spans: no I/O, no encoding of
/// pictures. Report buffers include the report id as their first byte, as HID expects.
/// </summary>
public interface IStreamDeckProtocol
{
	int OutputReportLength { get; }

	int FeatureReportLength { get; }

	/// <summary>Sent once after open to abort a half-sent image, empty when the family has none.</summary>
	ReadOnlyMemory<byte> ResetKeyStreamReport { get; }

	bool SupportsClearKey { get; }

	/// <summary>Number of leading bytes of a key image report that are header, the rest is picture payload.</summary>
	int KeyImageHeaderLength { get; }

	/// <summary>Feature report that keeps a device awake, empty when the family has none.</summary>
	ReadOnlyMemory<byte> KeepAliveReport { get; }

	IReadOnlyList<FeatureRequest> SerialRequests { get; }

	IReadOnlyList<FeatureRequest> FirmwareRequests { get; }

	int ToDeviceKey(int logicalKey);

	int ToLogicalKey(int deviceKey);

	/// <summary>Throws <see cref="ArgumentException"/> when an encoded image of this length cannot be sent.</summary>
	void ValidateImageLength(int imageLength);

	int GetKeyImageReportCount(int imageLength);

	/// <summary>Fills <paramref name="report"/> (<see cref="OutputReportLength"/> bytes) with one chunk of the image, zero padded.</summary>
	void BuildKeyImageReport(int logicalKey, ReadOnlySpan<byte> image, int reportIndex, Span<byte> report);

	void BuildClearKeyReport(int logicalKey, Span<byte> featureReport);

	void BuildBrightnessReport(int percent, Span<byte> featureReport);

	void BuildShowLogoReport(Span<byte> featureReport);

	/// <summary>Writes logical key states into <paramref name="keys"/>, false when the report is not a key report.</summary>
	bool TryParseInputReport(ReadOnlySpan<byte> report, Span<bool> keys);

	string ParseSerialNumber(ReadOnlySpan<byte> featureReport);

	string ParseFirmwareVersion(ReadOnlySpan<byte> featureReport);
}
