using StreamDeckConnector.Devices;
using NUnit.Framework;

namespace StreamDeckConnector.Tests.Devices;

[TestFixture]
public sealed class ProtocolTests
{
	private static StreamDeckModel Model(int productId)
	{
		Assert.That(StreamDeckModels.TryGet(0x0FD9, productId, out var model), Is.True);
		return model;
	}

	private static byte[] Pattern(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i % 251))];

	private static byte[] Padded(int length, params byte[] start)
	{
		var bytes = new byte[length];
		start.CopyTo(bytes, 0);
		return bytes;
	}

	private static byte[] BuildReport(IStreamDeckProtocol protocol, int key, byte[] image, int index)
	{
		var report = new byte[protocol.OutputReportLength];
		protocol.BuildKeyImageReport(key, image, index, report);
		return report;
	}

	[Test]
	public void Gen2_key_image_spanning_two_reports_has_the_documented_header_and_padding()
	{
		var protocol = StreamDeckProtocols.Create(Model(0x0080));
		var image = Pattern(1500);

		Assert.That(protocol.GetKeyImageReportCount(image.Length), Is.EqualTo(2));

		// 02 07 key last length(LE) index(LE) payload, 1016 payload bytes per 1024 byte report.
		var first = Padded(1024, 0x02, 0x07, 0x03, 0x00, 0xF8, 0x03, 0x00, 0x00);
		image.AsSpan(0, 1016).CopyTo(first.AsSpan(8));
		var second = Padded(1024, 0x02, 0x07, 0x03, 0x01, 0xE4, 0x01, 0x01, 0x00);
		image.AsSpan(1016, 484).CopyTo(second.AsSpan(8));

		Assert.That(BuildReport(protocol, 3, image, 0), Is.EqualTo(first));
		Assert.That(BuildReport(protocol, 3, image, 1), Is.EqualTo(second));
	}

	[Test]
	public void Gen2_image_that_fits_one_report_is_marked_last()
	{
		var protocol = StreamDeckProtocols.Create(Model(0x0080));
		var image = Pattern(1016);

		var expected = Padded(1024, 0x02, 0x07, 0x0E, 0x01, 0xF8, 0x03, 0x00, 0x00);
		image.CopyTo(expected.AsSpan(8));

		Assert.That(protocol.GetKeyImageReportCount(image.Length), Is.EqualTo(1));
		Assert.That(BuildReport(protocol, 14, image, 0), Is.EqualTo(expected));
	}

	[Test]
	public void Gen2_rejects_keys_outside_the_grid_including_the_python_off_by_one()
	{
		var protocol = StreamDeckProtocols.Create(Model(0x0080));

		Assert.Throws<ArgumentOutOfRangeException>(() => protocol.ToDeviceKey(15));
		Assert.Throws<ArgumentOutOfRangeException>(() => protocol.ToDeviceKey(-1));
	}

	[Test]
	public void Gen2_feature_reports_are_32_bytes_and_zero_padded()
	{
		var protocol = StreamDeckProtocols.Create(Model(0x0080));
		var report = new byte[32];

		protocol.BuildBrightnessReport(75, report);
		Assert.That(report, Is.EqualTo(Padded(32, 0x03, 0x08, 75)));

		protocol.BuildShowLogoReport(report);
		Assert.That(report, Is.EqualTo(Padded(32, 0x03, 0x02)));
	}

	[Test]
	public void Gen2_sends_a_lone_report_id_to_reset_the_key_stream()
	{
		var protocol = StreamDeckProtocols.Create(Model(0x0080));

		Assert.That(protocol.ResetKeyStreamReport.ToArray(), Is.EqualTo(Padded(1024, 0x02)));
	}

	[Test]
	public void Gen2_parses_serial_and_firmware_from_feature_reports()
	{
		var protocol = StreamDeckProtocols.Create(Model(0x0080));
		var serial = Padded(32, 0x06, 0x0C);
		"CL12A3456789"u8.CopyTo(serial.AsSpan(2));
		var firmware = Padded(32, 0x05, 0x0C, 0xDE, 0xAD, 0xBE, 0xEF);
		"1.02.034"u8.CopyTo(firmware.AsSpan(6));

		Assert.That(protocol.SerialRequests[0], Is.EqualTo(new FeatureRequest(0x06, 32)));
		Assert.That(protocol.ParseSerialNumber(serial), Is.EqualTo("CL12A3456789"));
		Assert.That(protocol.FirmwareRequests[0], Is.EqualTo(new FeatureRequest(0x05, 32)));
		Assert.That(protocol.ParseFirmwareVersion(firmware), Is.EqualTo("1.02.034"));
	}

	[Test]
	public void Gen2_input_report_yields_key_states_from_offset_four()
	{
		var protocol = StreamDeckProtocols.Create(Model(0x0080));
		var report = Padded(512, 0x01, 0x00, 0x0F, 0x00);
		report[4 + 0] = 1;
		report[4 + 5] = 1;
		report[4 + 14] = 1;
		var keys = new bool[15];

		Assert.That(protocol.TryParseInputReport(report, keys), Is.True);

		Assert.That(keys.Select((down, i) => (down, i)).Where(k => k.down).Select(k => k.i), Is.EqualTo((int[])[0, 5, 14]));
	}

	[TestCase((byte)0x02, TestName = "Gen2_touch_report_is_not_a_key_report")]
	[TestCase((byte)0x03, TestName = "Gen2_encoder_report_is_not_a_key_report")]
	[TestCase((byte)0x04, TestName = "Gen2_nfc_report_is_not_a_key_report")]
	public void Gen2_non_key_reports_are_skipped_without_touching_the_states(byte command)
	{
		// Plus: dials and the touch strip share the input report channel with the keys.
		var protocol = StreamDeckProtocols.Create(Model(0x0084));
		var report = Padded(512, 0x01, command, 0x08, 0x00, 1, 1, 1, 1, 1, 1, 1, 1);
		var keys = new bool[8];

		Assert.That(protocol.TryParseInputReport(report, keys), Is.False);
		Assert.That(keys, Has.None.True);
	}

	[Test]
	public void Gen2_ignores_the_neo_touch_keys_and_short_reports()
	{
		var protocol = StreamDeckProtocols.Create(Model(0x009A));
		var report = Padded(64, 0x01, 0x00, 0x0A, 0x00, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1);
		var keys = new bool[8];

		Assert.That(protocol.TryParseInputReport(report, keys), Is.True);
		Assert.That(keys[7], Is.True);
		Assert.That(keys.Count(k => k), Is.EqualTo(1));

		Assert.That(protocol.TryParseInputReport(new byte[] { 0x01, 0x00, 0x08, 0x00, 1 }, keys), Is.False);
	}

	[Test]
	public void OriginalV1_maps_keys_right_to_left_within_each_row_in_both_directions()
	{
		var protocol = StreamDeckProtocols.Create(Model(0x0060));
		(int Logical, int Device)[] expected =
		[
			(0, 4), (1, 3), (2, 2), (3, 1), (4, 0),
			(5, 9), (6, 8), (7, 7), (8, 6), (9, 5),
			(10, 14), (11, 13), (12, 12), (13, 11), (14, 10),
		];

		foreach (var (logical, device) in expected)
		{
			Assert.That(protocol.ToDeviceKey(logical), Is.EqualTo(device), $"logical {logical}");
			Assert.That(protocol.ToLogicalKey(device), Is.EqualTo(logical), $"device {device}");
		}
	}

	[Test]
	public void OriginalV1_splits_the_image_into_two_pages_with_one_based_page_numbers()
	{
		var protocol = StreamDeckProtocols.Create(Model(0x0060));
		var image = Pattern(20);

		Assert.That(protocol.GetKeyImageReportCount(image.Length), Is.EqualTo(2));

		// Logical key 6 is device key 8, sent as 8 + 1.
		var page1 = Padded(8191, 0x02, 0x01, 0x01, 0x00, 0x00, 0x09);
		image.AsSpan(0, 10).CopyTo(page1.AsSpan(16));
		var page2 = Padded(8191, 0x02, 0x01, 0x02, 0x00, 0x01, 0x09);
		image.AsSpan(10, 10).CopyTo(page2.AsSpan(16));

		Assert.That(BuildReport(protocol, 6, image, 0), Is.EqualTo(page1));
		Assert.That(BuildReport(protocol, 6, image, 1), Is.EqualTo(page2));
	}

	[Test]
	public void OriginalV1_rejects_an_image_that_does_not_split_in_two()
	{
		var protocol = StreamDeckProtocols.Create(Model(0x0060));

		Assert.Throws<ArgumentException>(() => protocol.ValidateImageLength(21));
		Assert.Throws<ArgumentException>(() => protocol.ValidateImageLength(0));
	}

	[Test]
	public void OriginalV1_input_report_is_read_from_offset_one_and_mapped_back_to_logical_keys()
	{
		var protocol = StreamDeckProtocols.Create(Model(0x0060));
		var report = Padded(17, 0x01);
		report[1 + 4] = 1;
		report[1 + 10] = 1;
		var keys = new bool[15];

		Assert.That(protocol.TryParseInputReport(report, keys), Is.True);

		// Device 4 is logical 0 and device 10 is logical 14.
		Assert.That(keys.Select((down, i) => (down, i)).Where(k => k.down).Select(k => k.i), Is.EqualTo((int[])[0, 14]));
	}

	[Test]
	public void OriginalV1_feature_reports_are_17_bytes_and_it_cannot_blank_a_key()
	{
		var protocol = StreamDeckProtocols.Create(Model(0x0060));
		var report = new byte[17];

		protocol.BuildBrightnessReport(50, report);
		Assert.That(report, Is.EqualTo(Padded(17, 0x05, 0x55, 0xAA, 0xD1, 0x01, 50)));

		protocol.BuildShowLogoReport(report);
		Assert.That(report, Is.EqualTo(Padded(17, 0x0B, 0x63)));

		Assert.That(protocol.ResetKeyStreamReport.IsEmpty, Is.True);
	}

	[Test]
	public void OriginalV1_reads_the_serial_from_offset_five()
	{
		var protocol = StreamDeckProtocols.Create(Model(0x0060));
		var report = Padded(17, 0x03, 0, 0, 0, 0);
		"AB123"u8.CopyTo(report.AsSpan(5));

		Assert.That(protocol.SerialRequests[0], Is.EqualTo(new FeatureRequest(0x03, 17)));
		Assert.That(protocol.ParseSerialNumber(report), Is.EqualTo("AB123"));
	}

	[Test]
	public void Mini_key_image_header_uses_zero_based_chunks_and_key_plus_one()
	{
		var protocol = StreamDeckProtocols.Create(Model(0x0063));
		var image = Pattern(2000);

		Assert.That(protocol.GetKeyImageReportCount(image.Length), Is.EqualTo(2));

		// 02 01 chunk 00 last key+1, 1008 payload bytes per 1024 byte report.
		var first = Padded(1024, 0x02, 0x01, 0x00, 0x00, 0x00, 0x05);
		image.AsSpan(0, 1008).CopyTo(first.AsSpan(16));
		var second = Padded(1024, 0x02, 0x01, 0x01, 0x00, 0x01, 0x05);
		image.AsSpan(1008, 992).CopyTo(second.AsSpan(16));

		Assert.That(BuildReport(protocol, 4, image, 0), Is.EqualTo(first));
		Assert.That(BuildReport(protocol, 4, image, 1), Is.EqualTo(second));
	}

	[Test]
	public void Mini_keeps_logical_and_device_keys_identical_and_reads_input_from_offset_one()
	{
		var protocol = StreamDeckProtocols.Create(Model(0x0063));
		var report = Padded(65, 0x01);
		report[1 + 0] = 1;
		report[1 + 5] = 1;
		var keys = new bool[6];

		Assert.That(protocol.ToDeviceKey(4), Is.EqualTo(4));
		Assert.That(protocol.TryParseInputReport(report, keys), Is.True);
		Assert.That(keys, Is.EqualTo((bool[])[true, false, false, false, false, true]));
	}

	[Test]
	public void Mini_feature_reports_match_the_original_v1_ones()
	{
		var protocol = StreamDeckProtocols.Create(Model(0x0063));
		var report = new byte[17];

		protocol.BuildBrightnessReport(100, report);
		Assert.That(report, Is.EqualTo(Padded(17, 0x05, 0x55, 0xAA, 0xD1, 0x01, 100)));

		protocol.BuildShowLogoReport(report);
		Assert.That(report, Is.EqualTo(Padded(17, 0x0B, 0x63)));
	}

	[Test]
	public void Every_catalogued_model_has_a_consistent_description()
	{
		foreach (var model in StreamDeckModels.All)
		{
			Assert.That(model.KeyCount, Is.GreaterThan(0), model.Name);
			Assert.That(model.HasKeyImages, Is.EqualTo(model.KeyPixelSize > 0), model.Name);
			Assert.That(model.HasKeyImages, Is.EqualTo(model.BytesPerSecond > 0), model.Name);
		}

		Assert.That(StreamDeckModels.All.Select(m => m.ProductId), Is.Unique);
		Assert.That(StreamDeckModels.TryGet(0x0FD9, 0x00AA, out _), Is.False, "Studio is not supported");
		Assert.That(StreamDeckModels.TryGet(0x0FD9, 0x2B18, out _), Is.False, "the Galleon is keyed by the Corsair vendor id");
	}

	[Test]
	public void The_bitmap_models_ask_for_the_orientation_python_elgato_streamdeck_produces()
	{
		// Host order: mirror left to right, then turn clockwise, then a standard bottom-up BMP.
		Assert.That((Model(0x0060).RotationDegrees, Model(0x0060).MirrorHorizontal), Is.EqualTo((180, false)), "both axes flipped");
		foreach (var mini in new[] { 0x0063, 0x0090, 0x00B3, 0x00B8 })
		{
			Assert.That((Model(mini).RotationDegrees, Model(mini).MirrorHorizontal), Is.EqualTo((270, true)), $"0x{mini:X4} is transposed");
		}
	}

	[Test]
	public void The_minimum_input_report_carries_every_key_state()
	{
		Assert.That(StreamDeckProtocols.Create(Model(0x0080)).MinimumInputReportLength, Is.EqualTo(4 + 15));
		Assert.That(StreamDeckProtocols.Create(Model(0x0060)).MinimumInputReportLength, Is.EqualTo(1 + 15));
		Assert.That(StreamDeckProtocols.Create(Model(0x0063)).MinimumInputReportLength, Is.EqualTo(1 + 6));
	}
}
