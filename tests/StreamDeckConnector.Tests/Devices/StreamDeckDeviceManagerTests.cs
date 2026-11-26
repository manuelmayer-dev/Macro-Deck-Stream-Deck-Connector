using System.Collections.Concurrent;
using StreamDeckConnector.Devices;
using StreamDeckConnector.Hid;
using NUnit.Framework;

namespace StreamDeckConnector.Tests.Devices;

[TestFixture]
public sealed class StreamDeckDeviceManagerTests
{
	private static HidDeviceInfo Mk2(string path, string? serial = "SN-1") => new(path, 0x0FD9, 0x0080, serial);

	private static (StreamDeckDeviceManager Manager, EventRecorder<string> Log, ConcurrentDictionary<string, StreamDeckDevice> Seen) Create(FakeHidDeviceSource source)
	{
		var manager = new StreamDeckDeviceManager(source, new FakeTimeProvider(), Serilog.Core.Logger.None, new StreamDeckDiagnosticsOptions());
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
	public async Task A_device_without_a_serial_is_identified_by_its_path()
	{
		var source = new FakeHidDeviceSource();
		source.Plug(Mk2("path-1", serial: null));
		var (manager, _, _) = Create(source);
		await using var _ = manager;

		await manager.StartAsync();

		Assert.That(manager.Devices[0].SerialNumber, Is.EqualTo("path-1"));
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
