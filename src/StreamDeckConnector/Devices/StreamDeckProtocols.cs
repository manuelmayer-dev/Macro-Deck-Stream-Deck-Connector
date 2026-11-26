using System.Buffers.Binary;
using System.Text;

namespace StreamDeckConnector.Devices;

public static class StreamDeckProtocols
{
	public static IStreamDeckProtocol Create(StreamDeckModel model) => model.Family switch
	{
		StreamDeckProtocolFamily.Gen2 => new Gen2Protocol(model),
		StreamDeckProtocolFamily.OriginalV1 => new OriginalV1Protocol(model),
		StreamDeckProtocolFamily.Mini => new MiniProtocol(model),
		_ => throw new ArgumentOutOfRangeException(nameof(model)),
	};

	internal static void CheckKey(int key, int keyCount)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(key);
		ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(key, keyCount);
	}

	internal static void CheckReportLength(Span<byte> report, int expected)
	{
		if (report.Length != expected)
		{
			throw new ArgumentException($"The report must be {expected} bytes long.", nameof(report));
		}
	}

	internal static void CheckPercent(int percent)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(percent);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
	}

	internal static string AsciiUntilNul(ReadOnlySpan<byte> bytes)
	{
		var end = bytes.IndexOf((byte)0);
		return Encoding.ASCII.GetString(end < 0 ? bytes : bytes[..end]);
	}
}

/// <summary>Original v2, MK.2, XL, Plus, Plus XL, Neo, Pedal and the modules (brief, part A "Gen 2").</summary>
public sealed class Gen2Protocol : IStreamDeckProtocol
{
	private const int HeaderLength = 8;
	private const int ReportLength = 1024;
	private const int PayloadLength = ReportLength - HeaderLength;
	private const int FeatureLength = 32;
	private const int KeyStatesOffset = 4;

	private static readonly byte[] _resetKeyStream = CreateResetKeyStream();
	private static readonly byte[] _keepAlive = [0x03, 0x27];

	private readonly int _keyCount;

	public Gen2Protocol(StreamDeckModel model)
	{
		_keyCount = model.KeyCount;
	}

	public int OutputReportLength => ReportLength;

	public int FeatureReportLength => FeatureLength;

	public ReadOnlyMemory<byte> ResetKeyStreamReport => _resetKeyStream;

	public bool SupportsClearKey => true;

	public int KeyImageHeaderLength => HeaderLength;

	// Sent as two bytes, exactly as node does for the Galleon.
	public ReadOnlyMemory<byte> KeepAliveReport => _keepAlive;

	public IReadOnlyList<FeatureRequest> SerialRequests { get; } = [new(0x06, FeatureLength)];

	public IReadOnlyList<FeatureRequest> FirmwareRequests { get; } = [new(0x05, FeatureLength)];

	public int ToDeviceKey(int logicalKey)
	{
		StreamDeckProtocols.CheckKey(logicalKey, _keyCount);
		return logicalKey;
	}

	public int ToLogicalKey(int deviceKey)
	{
		StreamDeckProtocols.CheckKey(deviceKey, _keyCount);
		return deviceKey;
	}

	public int GetKeyImageReportCount(int imageLength) => (imageLength + PayloadLength - 1) / PayloadLength;

	public void ValidateImageLength(int imageLength)
	{
		// The chunk index field is a UINT16.
		if (imageLength <= 0 || GetKeyImageReportCount(imageLength) > ushort.MaxValue + 1)
		{
			throw new ArgumentException("The image is empty or too large.", nameof(imageLength));
		}
	}

	public void BuildKeyImageReport(int logicalKey, ReadOnlySpan<byte> image, int reportIndex, Span<byte> report)
	{
		StreamDeckProtocols.CheckReportLength(report, ReportLength);
		ValidateImageLength(image.Length);
		var count = GetKeyImageReportCount(image.Length);
		ArgumentOutOfRangeException.ThrowIfNegative(reportIndex);
		ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(reportIndex, count);

		var offset = reportIndex * PayloadLength;
		var chunk = image.Slice(offset, Math.Min(PayloadLength, image.Length - offset));

		report.Clear();
		report[0] = 0x02;
		report[1] = 0x07;
		report[2] = (byte)ToDeviceKey(logicalKey);
		report[3] = reportIndex == count - 1 ? (byte)1 : (byte)0;
		BinaryPrimitives.WriteUInt16LittleEndian(report[4..], (ushort)chunk.Length);
		BinaryPrimitives.WriteUInt16LittleEndian(report[6..], (ushort)reportIndex);
		chunk.CopyTo(report[HeaderLength..]);
	}

	public void BuildClearKeyReport(int logicalKey, Span<byte> featureReport)
	{
		StreamDeckProtocols.CheckReportLength(featureReport, FeatureLength);
		featureReport.Clear();
		featureReport[0] = 0x03;
		featureReport[1] = 0x06;
		featureReport[2] = (byte)ToDeviceKey(logicalKey);
	}

	public void BuildBrightnessReport(int percent, Span<byte> featureReport)
	{
		StreamDeckProtocols.CheckReportLength(featureReport, FeatureLength);
		StreamDeckProtocols.CheckPercent(percent);
		featureReport.Clear();
		featureReport[0] = 0x03;
		featureReport[1] = 0x08;
		featureReport[2] = (byte)percent;
	}

	public void BuildShowLogoReport(Span<byte> featureReport)
	{
		StreamDeckProtocols.CheckReportLength(featureReport, FeatureLength);
		featureReport.Clear();
		featureReport[0] = 0x03;
		featureReport[1] = 0x02;
	}

	public bool TryParseInputReport(ReadOnlySpan<byte> report, Span<bool> keys)
	{
		// Command 0x00 is key state. Touch (0x02), encoder (0x03) and NFC (0x04) reports are skipped.
		if (report.Length < KeyStatesOffset + _keyCount || report[0] != 0x01 || report[1] != 0x00)
		{
			return false;
		}

		for (var i = 0; i < _keyCount; i++)
		{
			keys[i] = report[KeyStatesOffset + i] != 0;
		}

		return true;
	}

	public string ParseSerialNumber(ReadOnlySpan<byte> featureReport) =>
		StreamDeckProtocols.AsciiUntilNul(Field(featureReport, 2, featureReport[1]));

	// Bytes 2..5 are a checksum, the version string follows at offset 6.
	public string ParseFirmwareVersion(ReadOnlySpan<byte> featureReport) =>
		StreamDeckProtocols.AsciiUntilNul(Field(featureReport, 6, featureReport[1] - 4));

	private static ReadOnlySpan<byte> Field(ReadOnlySpan<byte> report, int offset, int length) =>
		report.Slice(offset, Math.Clamp(length, 0, report.Length - offset));

	private static byte[] CreateResetKeyStream()
	{
		// Only python sends this (brief, part A), and it is harmless on a device that is idle.
		var report = new byte[ReportLength];
		report[0] = 0x02;
		return report;
	}
}

/// <summary>Original v1 (0x0060): BMP images in two pages, keys numbered right to left within a row.</summary>
public sealed class OriginalV1Protocol : IStreamDeckProtocol
{
	private const int HeaderLength = 16;
	private const int ReportLength = 8191;
	private const int FeatureLength = 17;
	private const int KeyStatesOffset = 1;

	private readonly int _keyCount;
	private readonly int _columns;

	public OriginalV1Protocol(StreamDeckModel model)
	{
		_keyCount = model.KeyCount;
		_columns = model.Columns;
	}

	public int OutputReportLength => ReportLength;

	public int FeatureReportLength => FeatureLength;

	public ReadOnlyMemory<byte> ResetKeyStreamReport => ReadOnlyMemory<byte>.Empty;

	public bool SupportsClearKey => false;

	public int KeyImageHeaderLength => HeaderLength;

	public ReadOnlyMemory<byte> KeepAliveReport => ReadOnlyMemory<byte>.Empty;

	public IReadOnlyList<FeatureRequest> SerialRequests { get; } = [new(0x03, FeatureLength)];

	public IReadOnlyList<FeatureRequest> FirmwareRequests { get; } = [new(0x04, FeatureLength)];

	public int ToDeviceKey(int logicalKey)
	{
		StreamDeckProtocols.CheckKey(logicalKey, _keyCount);
		return MirrorWithinRow(logicalKey);
	}

	public int ToLogicalKey(int deviceKey)
	{
		StreamDeckProtocols.CheckKey(deviceKey, _keyCount);
		return MirrorWithinRow(deviceKey);
	}

	public int GetKeyImageReportCount(int imageLength) => 2;

	public void ValidateImageLength(int imageLength)
	{
		if (imageLength <= 0 || imageLength % 2 != 0 || imageLength / 2 > ReportLength - HeaderLength)
		{
			throw new ArgumentException("The image must split into two non-empty halves that each fit one report.", nameof(imageLength));
		}
	}

	public void BuildKeyImageReport(int logicalKey, ReadOnlySpan<byte> image, int reportIndex, Span<byte> report)
	{
		StreamDeckProtocols.CheckReportLength(report, ReportLength);
		ValidateImageLength(image.Length);
		ArgumentOutOfRangeException.ThrowIfNegative(reportIndex);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(reportIndex, 1);

		var half = image.Length / 2;
		report.Clear();
		report[0] = 0x02;
		report[1] = 0x01;
		report[2] = (byte)(reportIndex + 1);
		report[4] = reportIndex == 1 ? (byte)1 : (byte)0;
		report[5] = (byte)(ToDeviceKey(logicalKey) + 1);
		image.Slice(reportIndex * half, half).CopyTo(report[HeaderLength..]);
	}

	public void BuildClearKeyReport(int logicalKey, Span<byte> featureReport) =>
		throw new NotSupportedException("The Original v1 has no way to blank a key without an image.");

	public void BuildBrightnessReport(int percent, Span<byte> featureReport)
	{
		StreamDeckProtocols.CheckReportLength(featureReport, FeatureLength);
		StreamDeckProtocols.CheckPercent(percent);
		featureReport.Clear();
		featureReport[0] = 0x05;
		featureReport[1] = 0x55;
		featureReport[2] = 0xAA;
		featureReport[3] = 0xD1;
		featureReport[4] = 0x01;
		featureReport[5] = (byte)percent;
	}

	public void BuildShowLogoReport(Span<byte> featureReport)
	{
		StreamDeckProtocols.CheckReportLength(featureReport, FeatureLength);
		featureReport.Clear();
		featureReport[0] = 0x0B;
		featureReport[1] = 0x63;
	}

	public bool TryParseInputReport(ReadOnlySpan<byte> report, Span<bool> keys)
	{
		if (report.Length < KeyStatesOffset + _keyCount || report[0] != 0x01)
		{
			return false;
		}

		for (var logical = 0; logical < _keyCount; logical++)
		{
			keys[logical] = report[KeyStatesOffset + MirrorWithinRow(logical)] != 0;
		}

		return true;
	}

	public string ParseSerialNumber(ReadOnlySpan<byte> featureReport) => StreamDeckProtocols.AsciiUntilNul(featureReport[5..]);

	public string ParseFirmwareVersion(ReadOnlySpan<byte> featureReport) => StreamDeckProtocols.AsciiUntilNul(featureReport[5..]);

	private int MirrorWithinRow(int key) => (key / _columns * _columns) + (_columns - 1 - (key % _columns));
}

/// <summary>Mini, Mini 2022, Mini Discord and the 6-key module: BMP images in 1024 byte chunks.</summary>
public sealed class MiniProtocol : IStreamDeckProtocol
{
	private const int HeaderLength = 16;
	private const int ReportLength = 1024;
	private const int PayloadLength = ReportLength - HeaderLength;
	private const int KeyStatesOffset = 1;

	private readonly int _keyCount;

	public MiniProtocol(StreamDeckModel model)
	{
		_keyCount = model.KeyCount;
	}

	public int OutputReportLength => ReportLength;

	public int FeatureReportLength => 17;

	public ReadOnlyMemory<byte> ResetKeyStreamReport => ReadOnlyMemory<byte>.Empty;

	public bool SupportsClearKey => false;

	public int KeyImageHeaderLength => HeaderLength;

	public ReadOnlyMemory<byte> KeepAliveReport => ReadOnlyMemory<byte>.Empty;

	// Python reads 17 bytes on 0x0063 and 32 elsewhere, node tries 32 then 17, so try both.
	public IReadOnlyList<FeatureRequest> SerialRequests { get; } = [new(0x03, 32), new(0x03, 17)];

	// Elgato documents 0xA1, python uses 0x04, node tries 0xA1 then 0x04. Offset 5 for the string is from python only.
	public IReadOnlyList<FeatureRequest> FirmwareRequests { get; } = [new(0xA1, 32), new(0xA1, 17), new(0x04, 32), new(0x04, 17)];

	public int ToDeviceKey(int logicalKey)
	{
		StreamDeckProtocols.CheckKey(logicalKey, _keyCount);
		return logicalKey;
	}

	public int ToLogicalKey(int deviceKey)
	{
		StreamDeckProtocols.CheckKey(deviceKey, _keyCount);
		return deviceKey;
	}

	public int GetKeyImageReportCount(int imageLength) => (imageLength + PayloadLength - 1) / PayloadLength;

	public void ValidateImageLength(int imageLength)
	{
		// The chunk index is a single byte here.
		if (imageLength <= 0 || GetKeyImageReportCount(imageLength) > byte.MaxValue + 1)
		{
			throw new ArgumentException("The image is empty or too large.", nameof(imageLength));
		}
	}

	public void BuildKeyImageReport(int logicalKey, ReadOnlySpan<byte> image, int reportIndex, Span<byte> report)
	{
		StreamDeckProtocols.CheckReportLength(report, ReportLength);
		ValidateImageLength(image.Length);
		var count = GetKeyImageReportCount(image.Length);
		ArgumentOutOfRangeException.ThrowIfNegative(reportIndex);
		ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(reportIndex, count);

		var offset = reportIndex * PayloadLength;
		var chunk = image.Slice(offset, Math.Min(PayloadLength, image.Length - offset));

		report.Clear();
		report[0] = 0x02;
		report[1] = 0x01;
		report[2] = (byte)reportIndex;
		report[4] = reportIndex == count - 1 ? (byte)1 : (byte)0;
		// Elgato labels this byte "Key Index", both libraries send key + 1.
		report[5] = (byte)(ToDeviceKey(logicalKey) + 1);
		chunk.CopyTo(report[HeaderLength..]);
	}

	public void BuildClearKeyReport(int logicalKey, Span<byte> featureReport) =>
		throw new NotSupportedException("The Mini family has no way to blank a key without an image.");

	public void BuildBrightnessReport(int percent, Span<byte> featureReport)
	{
		StreamDeckProtocols.CheckReportLength(featureReport, FeatureReportLength);
		StreamDeckProtocols.CheckPercent(percent);
		featureReport.Clear();
		featureReport[0] = 0x05;
		featureReport[1] = 0x55;
		featureReport[2] = 0xAA;
		featureReport[3] = 0xD1;
		featureReport[4] = 0x01;
		featureReport[5] = (byte)percent;
	}

	public void BuildShowLogoReport(Span<byte> featureReport)
	{
		StreamDeckProtocols.CheckReportLength(featureReport, FeatureReportLength);
		featureReport.Clear();
		featureReport[0] = 0x0B;
		featureReport[1] = 0x63;
	}

	public bool TryParseInputReport(ReadOnlySpan<byte> report, Span<bool> keys)
	{
		if (report.Length < KeyStatesOffset + _keyCount || report[0] != 0x01)
		{
			return false;
		}

		for (var i = 0; i < _keyCount; i++)
		{
			keys[i] = report[KeyStatesOffset + i] != 0;
		}

		return true;
	}

	public string ParseSerialNumber(ReadOnlySpan<byte> featureReport) => StreamDeckProtocols.AsciiUntilNul(featureReport[5..]);

	public string ParseFirmwareVersion(ReadOnlySpan<byte> featureReport) => StreamDeckProtocols.AsciiUntilNul(featureReport[5..]);
}
