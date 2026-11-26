using System.Globalization;
using Serilog.Events;
using StreamDeckConnector.Devices;
using StreamDeckConnector.Hid;
using NUnit.Framework;

namespace StreamDeckConnector.Tests.Devices;

[TestFixture]
public sealed class DiagnosticsTests
{
	private const string FullSerial = "ABCDEFGH1234";

	private static HidDeviceInfo Mk2(string path = "p1", string? serial = FullSerial) =>
		new(path, 0x0FD9, 0x0080, serial, "Stream Deck MK.2", 512, 1024, 32);

	private static HidDeviceInfo Studio(string path = "studio-1") =>
		new(path, 0x0FD9, 0x00AA, "STUDIOSERIAL", "Stream Deck Studio", 43, 1024, 32);

	private static byte[] FirmwareResponse(byte[] request)
	{
		if (request[0] != 0x05)
		{
			throw new IOException("unsupported");
		}

		var report = new byte[request.Length];
		report[0] = 0x05;
		report[1] = 0x0C;
		"9.8.7654"u8.CopyTo(report.AsSpan(6));
		return report;
	}

	private static (StreamDeckDeviceManager Manager, FakeHidDeviceSource Source, CollectingSink Sink, StreamDeckDiagnosticsOptions Options) Create()
	{
		var (logger, sink) = CollectingSink.Create();
		var source = new FakeHidDeviceSource { FeatureResponder = FirmwareResponse };
		var options = new StreamDeckDiagnosticsOptions();
		return (new StreamDeckDeviceManager(source, new FakeTimeProvider(), logger, options), source, sink, options);
	}

	[Test]
	public void Only_the_last_four_characters_of_a_serial_survive_masking()
	{
		Assert.That(SerialMask.Tail(FullSerial), Is.EqualTo("1234"));
		Assert.That(SerialMask.Tail("ABCD"), Is.EqualTo("****"));
		Assert.That(SerialMask.Tail("AB"), Is.EqualTo("****"));
		Assert.That(SerialMask.Tail(null), Is.EqualTo("none reported"));
		Assert.That(SerialMask.Tail(string.Empty), Is.EqualTo("none reported"));
	}

	[Test]
	public async Task Connecting_logs_model_ids_firmware_verification_and_report_lengths_with_a_masked_serial()
	{
		var (manager, source, sink, _) = Create();
		await using var _ = manager;
		source.Plug(Mk2());

		await manager.StartAsync();
		var logged = await sink.WaitForAsync(e => e.Level == LogEventLevel.Information);
		var message = logged.RenderMessage(CultureInfo.InvariantCulture);

		Assert.That(message, Does.Contain("Stream Deck MK.2"));
		Assert.That(message, Does.Contain("vendor 0x0FD9"));
		Assert.That(message, Does.Contain("product 0x0080"));
		Assert.That(message, Does.Contain("firmware 9.8.7654"));
		Assert.That(message, Does.Contain("verified on hardware True"));
		Assert.That(message, Does.Contain("input 512 output 1024 feature 32"));
		Assert.That(message, Does.Contain("serial ending 1234"));
		foreach (var rendered in sink.Events.Select(e => e.RenderMessage(CultureInfo.InvariantCulture)))
		{
			Assert.That(rendered, Does.Not.Contain(FullSerial));
			Assert.That(rendered, Does.Not.Contain("ABCDEFGH"));
		}
	}

	[Test]
	public async Task Only_the_mk2_is_verified_on_hardware()
	{
		Assert.That(StreamDeckModels.All.Where(m => m.VerifiedOnHardware).Select(m => m.ProductId), Is.EqualTo((int[])[0x0080]));
		var (manager, source, _, _) = Create();
		await using var _ = manager;
		source.Plug(Mk2());
		await manager.StartAsync();

		Assert.That(manager.Devices[0].IsHardwareVerified, Is.True);
	}

	[Test]
	public async Task An_unsupported_stream_deck_is_warned_about_once_per_attach_with_the_issue_url()
	{
		var (manager, source, sink, _) = Create();
		await using var _ = manager;
		source.Plug(Studio());

		await manager.StartAsync();
		await manager.RefreshAsync();
		var warnings = sink.Messages(LogEventLevel.Warning);

		Assert.That(warnings, Has.Count.EqualTo(1));
		Assert.That(warnings[0], Does.Contain("0x00AA"));
		Assert.That(warnings[0], Does.Contain("Stream Deck Studio"));
		Assert.That(warnings[0], Does.Contain("input 43 output 1024 feature 32"));
		Assert.That(warnings[0], Does.Contain("not supported"));
		Assert.That(warnings[0], Does.Contain(StreamDeckSupport.IssuesUrl));
		Assert.That(warnings[0], Does.Contain("diagnostics snapshot"));
		Assert.That(source.Connections, Is.Empty, "an unsupported device is never opened");

		source.Unplug("studio-1");
		await manager.RefreshAsync();
		source.Plug(Studio());
		await manager.RefreshAsync();

		Assert.That(sink.Messages(LogEventLevel.Warning), Has.Count.EqualTo(2), "re-attach warns again");
	}

	[Test]
	public async Task Unsupported_devices_are_exposed_with_attach_and_detach_events()
	{
		var (manager, source, _, _) = Create();
		await using var _ = manager;
		var events = new EventRecorder<string>();
		manager.UnsupportedDeviceAttached += d => events.Add($"attached {d.ProductId:X4} {d.Key}");
		manager.UnsupportedDeviceDetached += d => events.Add($"detached {d.ProductId:X4} {d.Key}");
		source.Plug(Studio());

		await manager.StartAsync();
		await manager.RefreshAsync();
		var first = manager.UnsupportedDevices.Single();
		Assert.That(first.VendorId, Is.EqualTo(0x0FD9));
		Assert.That(first.ProductName, Is.EqualTo("Stream Deck Studio"));
		Assert.That(first.InputReportLength, Is.EqualTo(43));
		Assert.That(first.Key, Has.Length.EqualTo(12));
		Assert.That(first.Key, Is.EqualTo(UnsupportedDevice.KeyFor("studio-1")));

		source.Unplug("studio-1");
		await manager.RefreshAsync();
		Assert.That(manager.UnsupportedDevices, Is.Empty);

		source.Plug(Studio());
		await manager.RefreshAsync();

		Assert.That(events.Events, Is.EqualTo((string[])[$"attached 00AA {first.Key}", $"detached 00AA {first.Key}", $"attached 00AA {first.Key}"]));
	}

	[Test]
	public async Task Other_vendors_are_ignored_without_a_warning()
	{
		var (manager, source, sink, _) = Create();
		await using var _ = manager;
		source.Plug(new HidDeviceInfo("kb", 0x1B1C, 0x1B2D, "KB", "Keyboard"));

		await manager.StartAsync();

		Assert.That(manager.UnsupportedDevices, Is.Empty);
		Assert.That(sink.Messages(LogEventLevel.Warning), Is.Empty);
	}

	[Test]
	public async Task Repeated_open_failures_are_logged_once_per_episode()
	{
		var (manager, source, sink, _) = Create();
		await using var _ = manager;
		source.OpenFailure = _ => new IOException("access denied by the OS");
		source.Plug(Mk2());

		await manager.StartAsync();
		await manager.RefreshAsync();
		await manager.RefreshAsync();
		var first = sink.Messages(LogEventLevel.Warning);
		Assert.That(first, Has.Count.EqualTo(1));
		Assert.That(first[0], Does.Contain("Stream Deck MK.2"));
		Assert.That(first[0], Does.Contain("access denied by the OS"));

		source.OpenFailure = null;
		await manager.RefreshAsync();
		await manager.Devices[0].DisposeAsync();
		source.Unplug("p1");
		await manager.RefreshAsync();
		source.OpenFailure = _ => new IOException("again");
		source.Plug(Mk2());
		await manager.RefreshAsync();

		Assert.That(sink.Messages(LogEventLevel.Warning), Has.Count.EqualTo(2), "a new episode logs again");
	}

	[Test]
	public async Task A_read_failure_is_logged_once_with_the_model_and_the_os_error()
	{
		var (manager, source, sink, _) = Create();
		await using var _ = manager;
		source.Plug(Mk2());
		await manager.StartAsync();

		source.Connections["p1"].FailRead(new IOException("device not configured"));
		var warning = await sink.WaitForAsync(e => e.Level == LogEventLevel.Warning);
		var message = warning.RenderMessage(CultureInfo.InvariantCulture);

		Assert.That(message, Does.Contain("Stream Deck MK.2"));
		Assert.That(message, Does.Contain("read"));
		Assert.That(message, Does.Contain("device not configured"));
		Assert.That(sink.Messages(LogEventLevel.Warning), Has.Count.EqualTo(1));
	}

	[Test]
	public async Task The_snapshot_describes_the_environment_devices_counters_and_the_last_error()
	{
		var (manager, source, sink, _) = Create();
		await using var _ = manager;
		source.Plug(Mk2());
		source.Plug(Studio());
		await manager.StartAsync();
		await sink.WaitForAsync(e => e.Level == LogEventLevel.Information);
		var device = manager.Devices[0];
		var keys = new EventRecorder<int>();
		device.KeyDown += keys.Add;

		var image = new byte[200];
		device.SetKeyImage(0, image);
		await device.SetBrightnessAsync(10);
		device.SetKeyImage(0, image.ToArray());
		await device.SetBrightnessAsync(20);
		device.SetKeyImage(0, image.ToArray());
		await device.SetBrightnessAsync(30);
		var press = new byte[512];
		press[0] = 0x01;
		press[4] = 1;
		source.Connections["p1"].EnqueueInput(press);
		await keys.WaitForAsync(1);

		var dropped = new EventRecorder<StreamDeckDevice>();
		manager.DeviceDisconnected += dropped.Add;
		source.Connections["p1"].FailRead(new IOException("cable pulled"));
		await dropped.WaitForAsync(1);
		await manager.RefreshAsync();

		var text = manager.DescribeDiagnostics();

		Assert.That(text, Does.Contain(StreamDeckSupport.IssuesUrl));
		Assert.That(text.Split('\n')[1], Does.Contain(StreamDeckSupport.IssuesUrl), "the hint is at the top");
		Assert.That(text, Does.Contain("Plugin version:"));
		Assert.That(text, Does.Contain($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}"));
		Assert.That(text, Does.Contain("Runtime: .NET"));
		Assert.That(text, Does.Contain("Stream Deck MK.2 | vendor 0x0FD9 product 0x0080 | serial ending 1234"));
		Assert.That(text, Does.Contain("firmware 9.8.7654"));
		Assert.That(text, Does.Contain("hardware verified: yes"));
		Assert.That(text, Does.Contain("input 512 output 1024 feature 32"));
		Assert.That(text, Does.Contain("reports read 1, key events 1, images written 1, images skipped as identical 2, write failures 0, reconnects 1"));
		Assert.That(text, Does.Contain("last error: cable pulled"));
		Assert.That(text, Does.Contain("vendor 0x0FD9 product 0x00AA | product string 'Stream Deck Studio'"));
		Assert.That(text, Does.Not.Contain(FullSerial));
		Assert.That(text, Does.Not.Contain("STUDIOSERIAL"));
	}

	[Test]
	public void The_raw_trace_is_off_by_default_and_capped_in_bytes_and_lines()
	{
		var (logger, sink) = CollectingSink.Create();
		var time = new FakeTimeProvider();
		var options = new StreamDeckDiagnosticsOptions { MaxBytesPerTraceLine = 4, MaxTraceLinesPerSecond = 2 };
		var trace = new RawTrace(logger, options, time);
		byte[] report = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06];

		trace.Report("input", report);
		Assert.That(sink.Events, Is.Empty, "off by default");

		options.TraceRawReports = true;
		for (var i = 0; i < 5; i++)
		{
			trace.Report("input", report);
		}

		var lines = sink.Messages(LogEventLevel.Debug);
		Assert.That(lines, Has.Count.EqualTo(2));
		Assert.That(lines[0], Does.Contain("input, 6 bytes: 01 02 03 04 ..."));
		Assert.That(lines[0], Does.Not.Contain("05"));

		time.Advance(TimeSpan.FromSeconds(1));
		trace.Report("input", report);

		lines = sink.Messages(LogEventLevel.Debug);
		Assert.That(lines, Has.Count.EqualTo(3));
		Assert.That(lines[2], Does.Contain("3 earlier trace lines dropped"));
	}

	[Test]
	public void The_trace_of_an_image_carries_length_and_hash_but_no_payload()
	{
		var (logger, sink) = CollectingSink.Create();
		var options = new StreamDeckDiagnosticsOptions { TraceRawReports = true };
		var trace = new RawTrace(logger, options, new FakeTimeProvider());
		byte[] hash = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22, 0x33, 0x44];

		trace.Image(3, 1500, 2, hash, [0x02, 0x07, 0x03, 0x00, 0xF8, 0x03, 0x00, 0x00]);

		var line = sink.Messages(LogEventLevel.Debug).Single();
		Assert.That(line, Does.Contain("key 3, 1500 bytes in 2 reports"));
		Assert.That(line, Does.Contain("AABBCCDDEEFF1122"));
		Assert.That(line, Does.Contain("02 07 03 00 F8 03 00 00"));
	}

	[Test]
	public async Task The_trace_logs_input_and_output_headers_when_switched_on_at_runtime()
	{
		var (logger, sink) = CollectingSink.Create();
		var options = new StreamDeckDiagnosticsOptions();
		var connection = new FakeHidConnection();
		StreamDeckModels.TryGet(0x0FD9, 0x0080, out var model);
		await using var device = new StreamDeckDevice(model, "SERIAL", connection, new FakeTimeProvider(), logger, options);
		await device.SetBrightnessAsync(5);

		Assert.That(sink.Messages(LogEventLevel.Debug), Is.Empty);

		options.TraceRawReports = true;
		device.SetKeyImage(0, new byte[2000]);
		await device.SetBrightnessAsync(6);
		var press = new byte[512];
		press[0] = 0x01;
		var keys = new EventRecorder<int>();
		device.KeyDown += keys.Add;
		press[4] = 1;
		connection.EnqueueInput(press);
		await keys.WaitForAsync(1);

		var lines = sink.Messages(LogEventLevel.Debug);
		Assert.That(lines.Any(l => l.Contains("image for key 0, 2000 bytes", StringComparison.Ordinal)), Is.True);
		Assert.That(lines.Any(l => l.StartsWith("Raw trace feature", StringComparison.Ordinal)), Is.True);
		Assert.That(lines.Any(l => l.StartsWith("Raw trace input, 512 bytes: 01 00 00 00 01", StringComparison.Ordinal)), Is.True);
	}

	[Test]
	public async Task A_galleon_sends_the_keep_alive_feature_report_on_its_interval()
	{
		var time = new FakeTimeProvider();
		var connection = new FakeHidConnection();
		Assert.That(StreamDeckModels.TryGet(0x1B1C, 0x2B18, out var galleon), Is.True);
		Assert.That((galleon.Rows, galleon.Columns, galleon.KeyPixelSize, galleon.VerifiedOnHardware), Is.EqualTo((4, 3, 160, false)));
		await using var device = new StreamDeckDevice(galleon, "SERIAL", connection, time, Serilog.Core.Logger.None, new StreamDeckDiagnosticsOptions());
		await connection.WaitForWritesAsync(1);

		time.Advance(TimeSpan.FromMilliseconds(500));
		var writes = await connection.WaitForWritesAsync(1);

		Assert.That(writes[1].Kind, Is.EqualTo(WriteKind.SetFeature));
		Assert.That(writes[1].Bytes, Is.EqualTo((byte[])[0x03, 0x27]));
	}
}
