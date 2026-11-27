using System.Collections.Concurrent;
using StreamDeckConnector.Devices;
using StreamDeckConnector.Hid;
using NUnit.Framework;

namespace StreamDeckConnector.Tests.Devices;

[TestFixture]
public sealed class StreamDeckDeviceManagerTests
{
	private static HidDeviceInfo Mk2(string path, string? serial = "SN-1") => new(path, 0x0FD9, 0x0080, serial);

	private static (StreamDeckDeviceManager Manager, EventRecorder<string> Log, ConcurrentDictionary<string, StreamDeckDevice> Seen) Create(FakeHidDeviceSource source, FakeTimeProvider? time = null, Serilog.ILogger? logger = null)
	{
		var manager = new StreamDeckDeviceManager(source, time ?? new FakeTimeProvider(), logger ?? Serilog.Core.Logger.None, new StreamDeckDiagnosticsOptions());
		var log = new EventRecorder<string>();
		var seen = new ConcurrentDictionary<string, StreamDeckDevice>();
		manager.DeviceConnected += d =>
		{
			seen[$"connected {log.Events.Count}"] = d;
			log.Add($"connected {d.SerialNumber}");
		};
		manager.DeviceDisconnected += d =>
		{
			seen[$"disconnected {log.Events.Count}"] = d;
			log.Add($"disconnected {d.SerialNumber}");
		};
		return (manager, log, seen);
	}

	[Test]
	public async Task A_plugged_in_device_is_opened_with_its_serial_and_model()
	{
		var source = new FakeHidDeviceSource();
		source.Plug(Mk2("path-1"));
		var (manager, log, _) = Create(source);
		await using var _ = manager;

		await manager.StartAsync();

		Assert.That(log.Events, Is.EqualTo((string[])["connected SN-1"]));
		Assert.That(manager.Devices, Has.Count.EqualTo(1));
		Assert.That(manager.Devices[0].SerialNumber, Is.EqualTo("SN-1"));
		Assert.That(manager.Devices[0].Model.Name, Is.EqualTo("Stream Deck MK.2"));
	}

	[Test]
	public async Task Unsupported_devices_are_never_opened()
	{
		var source = new FakeHidDeviceSource();
		source.Plug(new HidDeviceInfo("studio", 0x0FD9, 0x00AA, "ST"));
		source.Plug(new HidDeviceInfo("keyboard", 0x1B1C, 0x1B2D, "KB"));
		source.Plug(new HidDeviceInfo("other-vendor", 0x1234, 0x0080, "XX"));
		var (manager, _, _) = Create(source);
		await using var _ = manager;

		await manager.StartAsync();

		Assert.That(manager.Devices, Is.Empty);
		Assert.That(source.Connections, Is.Empty);
	}

	[Test]
	public async Task A_change_notification_opens_a_newly_plugged_device()
	{
		var source = new FakeHidDeviceSource();
		var (manager, log, _) = Create(source);
		await using var _ = manager;
		await manager.StartAsync();

		source.Plug(Mk2("path-1"));
		source.RaiseChanged();

		Assert.That(await log.WaitForAsync(1), Is.EqualTo((string[])["connected SN-1"]));
	}

	[Test]
	public async Task An_unplugged_device_is_closed_and_reported_once()
	{
		var source = new FakeHidDeviceSource();
		source.Plug(Mk2("path-1"));
		var (manager, log, _) = Create(source);
		await using var _ = manager;
		await manager.StartAsync();

		source.Unplug("path-1");
		await manager.RefreshAsync();
		await manager.RefreshAsync();

		Assert.That(log.Events, Is.EqualTo((string[])["connected SN-1", "disconnected SN-1"]));
		Assert.That(manager.Devices, Is.Empty);
		Assert.That(source.Connections["path-1"].IsDisposed, Is.True);
	}

	[Test]
	public async Task Replugging_the_same_serial_yields_a_fresh_device_object()
	{
		var source = new FakeHidDeviceSource();
		source.Plug(Mk2("path-1"));
		var (manager, log, seen) = Create(source);
		await using var _ = manager;
		await manager.StartAsync();
		var first = manager.Devices[0];

		source.Unplug("path-1");
		await manager.RefreshAsync();
		source.Plug(Mk2("path-2"));
		await manager.RefreshAsync();

		Assert.That(log.Events, Is.EqualTo((string[])["connected SN-1", "disconnected SN-1", "connected SN-1"]));
		Assert.That(manager.Devices, Has.Count.EqualTo(1));
		Assert.That(manager.Devices[0], Is.Not.SameAs(first));
		Assert.That(seen["disconnected 1"], Is.SameAs(first));
	}

	[Test]
	public async Task A_device_that_fails_on_its_own_is_dropped_and_reopened_on_the_next_refresh()
	{
		var source = new FakeHidDeviceSource();
		source.Plug(Mk2("path-1"));
		var (manager, log, _) = Create(source);
		await using var _ = manager;
		await manager.StartAsync();
		var first = manager.Devices[0];

		source.Connections["path-1"].FailRead(new IOException("gone"));
		await log.WaitForAsync(2);

		Assert.That(manager.Devices, Is.Empty);
		Assert.That(log.Events, Is.EqualTo((string[])["connected SN-1", "disconnected SN-1"]));

		await manager.RefreshAsync();
		Assert.That(manager.Devices, Has.Count.EqualTo(1));
		Assert.That(manager.Devices[0], Is.Not.SameAs(first));
	}

	[Test]
	public async Task A_device_that_cannot_be_opened_is_skipped_and_retried()
	{
		var source = new FakeHidDeviceSource { OpenFailure = _ => new IOException("busy") };
		source.Plug(Mk2("path-1"));
		var (manager, log, _) = Create(source);
		await using var _ = manager;

		await manager.StartAsync();
		Assert.That(manager.Devices, Is.Empty);

		source.OpenFailure = null;
		await manager.RefreshAsync();

		Assert.That(log.Events, Is.EqualTo((string[])["connected SN-1"]));
	}

	[Test]
	public async Task A_device_without_a_usb_serial_is_identified_by_its_serial_feature_report_before_it_is_announced()
	{
		var source = new FakeHidDeviceSource { FeatureResponder = SerialResponse("AL99K2B04321") };
		source.Plug(Mk2("path-1", serial: null));
		var (manager, _, _) = Create(source);
		await using var _ = manager;
		var announced = new List<string>();
		manager.DeviceConnected += device => announced.Add(device.SerialNumber);

		await manager.StartAsync();

		Assert.That(announced, Is.EqualTo((string[])["AL99K2B04321"]));
		Assert.That(manager.DescribeDiagnostics(), Does.Contain("serial ending 4321"));
	}

	[Test]
	public async Task A_device_without_any_serial_gets_a_port_based_identity_and_a_warning()
	{
		var (logger, sink) = CollectingSink.Create();
		var source = new FakeHidDeviceSource();
		source.Plug(Mk2("path-1", serial: null));
		source.Plug(Mk2("path-2", serial: null));
		var (manager, _, _) = Create(source, logger: logger);
		await using var _ = manager;

		await manager.StartAsync();
		var ids = manager.Devices.Select(d => d.SerialNumber).Order().ToList();
		source.Unplug("path-1");
		await manager.RefreshAsync();
		source.Plug(Mk2("path-1", serial: null));
		await manager.RefreshAsync();

		Assert.That(ids, Has.Count.EqualTo(2));
		Assert.That(ids.Distinct().Count(), Is.EqualTo(2), "two ports, two identities");
		Assert.That(ids, Has.All.StartWith("0fd90080-port-"));
		Assert.That(ids, Has.None.Contains("path"), "the OS path is not the identity");
		Assert.That(manager.Devices.Select(d => d.SerialNumber).Order(), Is.EqualTo(ids), "the same port gives the same identity again");
		var warnings = sink.Messages(Serilog.Events.LogEventLevel.Warning);
		Assert.That(warnings.Any(w => w.Contains("new device after it is plugged into another port", StringComparison.Ordinal)), Is.True);
	}

	[Test]
	public async Task Only_the_hid_interface_whose_reports_fit_the_protocol_is_opened()
	{
		var source = new FakeHidDeviceSource();
		source.Plug(new HidDeviceInfo("galleon-keys", 0x1B1C, 0x2B18, "GK", "Galleon", 64, 8, 64));
		source.Plug(new HidDeviceInfo("galleon-deck", 0x1B1C, 0x2B18, "GK", "Galleon", 512, 1024, 32));
		source.Plug(new HidDeviceInfo("mk2-second", 0x0FD9, 0x0080, "SN-2", "Stream Deck MK.2", 5, 0, 1));
		source.Plug(new HidDeviceInfo("mk2-unknown", 0x0FD9, 0x0080, "SN-3"));
		var (manager, _, _) = Create(source);
		await using var _ = manager;

		await manager.StartAsync();

		Assert.That(source.Connections.Keys.Order(), Is.EqualTo((string[])["galleon-deck", "mk2-unknown"]), "unknown lengths still open");
		Assert.That(manager.UnsupportedDevices, Is.Empty);
		Assert.That(manager.OpenFailures, Is.Empty);
	}

	[Test]
	public async Task A_device_that_throws_while_opening_does_not_stop_the_others()
	{
		var source = new FakeHidDeviceSource { OpenFailure = info => info.Path == "path-1" ? new InvalidOperationException("driver bug") : null };
		source.Plug(Mk2("path-1", "SN-1"));
		source.Plug(Mk2("path-2", "SN-2"));
		var (manager, log, _) = Create(source);
		await using var _ = manager;

		await manager.StartAsync();

		Assert.That(log.Events, Is.EqualTo((string[])["connected SN-2"]));
		Assert.That(manager.OpenFailures.Single().Error, Is.EqualTo("driver bug"));
	}

	[Test]
	public async Task A_device_that_cannot_be_opened_is_retried_on_a_timer_until_it_opens()
	{
		var time = new FakeTimeProvider();
		var source = new FakeHidDeviceSource { OpenFailure = _ => new IOException("held by another program") };
		source.Plug(Mk2("path-1"));
		var (manager, log, _) = Create(source, time);
		await using var _ = manager;

		await manager.StartAsync();
		Assert.That(time.HasActiveTimer, Is.True);
		time.Advance(TimeSpan.FromSeconds(5));
		await WaitUntilAsync(() => time.HasActiveTimer);
		Assert.That(log.Events, Is.Empty, "still held");

		source.OpenFailure = null;
		time.Advance(TimeSpan.FromSeconds(5));

		Assert.That(await log.WaitForAsync(1), Is.EqualTo((string[])["connected SN-1"]));
		await WaitUntilAsync(() => !time.HasActiveTimer);
		Assert.That(manager.OpenFailures, Is.Empty);
	}

	[Test]
	public async Task The_retry_timer_stops_when_the_failing_device_is_unplugged()
	{
		var time = new FakeTimeProvider();
		var source = new FakeHidDeviceSource { OpenFailure = _ => new IOException("busy") };
		source.Plug(Mk2("path-1"));
		var (manager, _, _) = Create(source, time);
		await using var _ = manager;
		await manager.StartAsync();

		source.Unplug("path-1");
		await manager.RefreshAsync();

		Assert.That(time.HasActiveTimer, Is.False);
		Assert.That(manager.OpenFailures, Is.Empty);
	}

	[Test]
	public async Task An_enumeration_failure_is_logged_once_per_episode()
	{
		var (logger, sink) = CollectingSink.Create();
		var source = new FakeHidDeviceSource { EnumerateFailure = new UnauthorizedAccessException("no access to the device list") };
		var (manager, _, _) = Create(source, logger: logger);
		await using var _ = manager;

		await manager.StartAsync();
		await manager.RefreshAsync();
		Assert.That(sink.Messages(Serilog.Events.LogEventLevel.Warning), Has.Count.EqualTo(1));
		Assert.That(sink.Messages(Serilog.Events.LogEventLevel.Warning)[0], Does.Contain("no access to the device list"));

		source.EnumerateFailure = null;
		await manager.RefreshAsync();
		source.EnumerateFailure = new IOException("again");
		await manager.RefreshAsync();

		Assert.That(sink.Messages(Serilog.Events.LogEventLevel.Warning), Has.Count.EqualTo(2));
	}

	[Test]
	public async Task A_refresh_started_by_a_change_notification_logs_its_failure()
	{
		var (logger, sink) = CollectingSink.Create();
		var source = new FakeHidDeviceSource();
		var (manager, _, _) = Create(source, logger: logger);
		await using var _ = manager;
		await manager.StartAsync();

		// A malformed entry makes the reconciliation itself throw.
		source.Plug(null!);
		source.RaiseChanged();

		var error = await sink.WaitForAsync(e => e.Level == Serilog.Events.LogEventLevel.Error);
		Assert.That(error.RenderMessage(System.Globalization.CultureInfo.InvariantCulture), Does.Contain("Refreshing the Stream Deck list failed"));
	}

	[Test]
	public async Task A_first_write_failure_is_reported_as_a_disconnect_after_the_connect()
	{
		var source = new FakeHidDeviceSource { ConfigureConnection = (_, connection) => connection.WriteFailure = new IOException("stalled") };
		source.Plug(Mk2("path-1"));
		var (manager, log, _) = Create(source);
		await using var _ = manager;

		await manager.StartAsync();

		Assert.That(await log.WaitForAsync(2), Is.EqualTo((string[])["connected SN-1", "disconnected SN-1"]));
		Assert.That(manager.Devices, Is.Empty);
	}

	[Test]
	public async Task Brightness_is_applied_to_connected_decks_and_again_after_a_replug()
	{
		var source = new FakeHidDeviceSource();
		source.Plug(Mk2("path-1"));
		var (manager, _, _) = Create(source);
		await using var _ = manager;
		await manager.StartAsync();

		Assert.That(await manager.SetBrightnessAsync(35), Is.EqualTo(1));
		Assert.That(BrightnessWrites(source.Connections["path-1"]), Is.EqualTo((int[])[35]));

		source.Unplug("path-1");
		await manager.RefreshAsync();
		Assert.That(await manager.SetBrightnessAsync(60), Is.Zero, "nothing connected, the value is still remembered");
		source.Plug(Mk2("path-2"));
		await manager.RefreshAsync();

		var connection = source.Connections["path-2"];
		var writes = await connection.WaitForWritesAsync(2);
		Assert.That(writes[1].Kind, Is.EqualTo(WriteKind.SetFeature), "right after the reset, before any picture");
		Assert.That(BrightnessWrites(connection), Is.EqualTo((int[])[60]));
	}

	[Test]
	public async Task A_deck_that_refuses_the_brightness_fails_the_call_and_stays_connected()
	{
		var source = new FakeHidDeviceSource { ConfigureConnection = (_, connection) => connection.FeatureFailure = new IOException("rejected") };
		source.Plug(Mk2("path-1"));
		var (manager, log, _) = Create(source);
		await using var _ = manager;
		await manager.StartAsync();

		Assert.ThrowsAsync<IOException>(async () => await manager.SetBrightnessAsync(50));
		Assert.That(manager.Devices, Has.Count.EqualTo(1));
		Assert.That(log.Events, Is.EqualTo((string[])["connected SN-1"]));
		Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await manager.SetBrightnessAsync(101));
	}

	private static int[] BrightnessWrites(FakeHidConnection connection) =>
		[.. connection.Writes.Where(w => w.Kind == WriteKind.SetFeature && w.Bytes[1] == 0x08).Select(w => (int)w.Bytes[2])];

	private static Func<byte[], byte[]> SerialResponse(string serial) => request =>
	{
		if (request[0] != 0x06)
		{
			throw new IOException("unsupported");
		}

		var report = new byte[request.Length];
		report[0] = 0x06;
		report[1] = (byte)serial.Length;
		System.Text.Encoding.ASCII.GetBytes(serial).CopyTo(report, 2);
		return report;
	};

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (!condition())
		{
			if (DateTime.UtcNow > deadline)
			{
				throw new TimeoutException("The condition did not hold within 10 s.");
			}

			await Task.Delay(10);
		}
	}

	[Test]
	public async Task Disposing_the_manager_closes_every_device_and_stops_listening()
	{
		var source = new FakeHidDeviceSource();
		source.Plug(Mk2("path-1"));
		var (manager, log, _) = Create(source);
		await manager.StartAsync();

		await manager.DisposeAsync();
		source.Plug(Mk2("path-2"));
		source.RaiseChanged();
		await manager.RefreshAsync();

		Assert.That(source.Connections["path-1"].IsDisposed, Is.True);
		Assert.That(source.Connections.ContainsKey("path-2"), Is.False);
		Assert.That(log.Events, Is.EqualTo((string[])["connected SN-1"]));
	}
}
