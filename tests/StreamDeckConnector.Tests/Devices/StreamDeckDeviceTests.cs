using StreamDeckConnector.Devices;
using NUnit.Framework;

namespace StreamDeckConnector.Tests.Devices;

[TestFixture]
public sealed class StreamDeckDeviceTests
{
	private const int Mk2 = 0x0080;

	private static StreamDeckModel Model(int productId) =>
		StreamDeckModels.TryGet(0x0FD9, productId, out var model) ? model : throw new AssertionException($"No model {productId:X4}");

	private static byte[] KeyReport(params int[] heldKeys)
	{
		var report = new byte[512];
		report[0] = 0x01;
		report[2] = 15;
		foreach (var key in heldKeys)
		{
			report[4 + key] = 1;
		}

		return report;
	}

	private static byte[] Image(byte fill, int length) => [.. Enumerable.Repeat(fill, length)];

	private static StreamDeckDevice Open(FakeHidConnection connection, FakeTimeProvider? time = null, int productId = Mk2) =>
		Started(Create(connection, Model(productId), time));

	private static StreamDeckDevice Create(FakeHidConnection connection, StreamDeckModel model, FakeTimeProvider? time = null, Serilog.ILogger? logger = null) =>
		new(model, "SERIAL", connection, time ?? new FakeTimeProvider(), logger ?? Serilog.Core.Logger.None, new StreamDeckDiagnosticsOptions());

	private static StreamDeckDevice Started(StreamDeckDevice device)
	{
		device.Start();
		return device;
	}

	private static IEnumerable<RecordedWrite> ImageWrites(IEnumerable<RecordedWrite> writes) =>
		writes.Where(w => w.Kind == WriteKind.Output && w.Bytes[1] == 0x07);

	[Test]
	public async Task Key_events_follow_the_difference_between_reports_with_several_keys_held()
	{
		var connection = new FakeHidConnection();
		await using var device = Open(connection);
		var events = new EventRecorder<string>();
		device.KeyDown += key => events.Add($"down {key}");
		device.KeyUp += key => events.Add($"up {key}");

		connection.EnqueueInput(KeyReport(1, 3));
		connection.EnqueueInput(KeyReport(1, 3));
		connection.EnqueueInput([0x01, 0x02, 0x00, 0x00, 1, 1, 1]);
		connection.EnqueueInput(KeyReport(1, 2, 3));
		connection.EnqueueInput(KeyReport(2, 3));
		connection.EnqueueInput(KeyReport());

		var seen = await events.WaitForAsync(6);

		Assert.That(seen, Is.EqualTo((string[])["down 1", "down 3", "down 2", "up 1", "up 2", "up 3"]));
	}

	[Test]
	public async Task Key_events_are_raised_while_the_writer_is_blocked()
	{
		var connection = new FakeHidConnection();
		using var release = new ManualResetEventSlim();
		using var writerBlocked = new ManualResetEventSlim();
		connection.BeforeWrite = _ =>
		{
			writerBlocked.Set();
			release.Wait(TimeSpan.FromSeconds(10));
		};
		await using var device = Open(connection);
		var events = new EventRecorder<int>();
		device.KeyDown += events.Add;
		Assert.That(writerBlocked.Wait(TimeSpan.FromSeconds(10)), Is.True);

		connection.EnqueueInput(KeyReport(4));
		var seen = await events.WaitForAsync(1);

		Assert.That(seen, Is.EqualTo((int[])[4]));
		release.Set();
	}

	[Test]
	public async Task Only_the_latest_picture_of_a_key_is_sent_when_the_writer_is_behind()
	{
		var connection = new FakeHidConnection();
		using var release = new ManualResetEventSlim();
		connection.BeforeWrite = index =>
		{
			if (index == 0)
			{
				release.Wait(TimeSpan.FromSeconds(10));
			}
		};
		await using var device = Open(connection);

		device.SetKeyImage(2, Image(0xA1, 100));
		device.SetKeyImage(2, Image(0xB2, 100));
		device.SetKeyImage(2, Image(0xC3, 100));
		release.Set();
		await device.SetBrightnessAsync(50);

		var writes = await connection.WaitForWritesAsync(3);
		var images = ImageWrites(writes).ToList();
		Assert.That(images, Has.Count.EqualTo(1));
		Assert.That(images[0].Bytes[8], Is.EqualTo(0xC3));
		Assert.That(writes[0].Bytes, Is.EqualTo(new byte[1024].Select((_, i) => i == 0 ? (byte)0x02 : (byte)0).ToArray()), "reset key stream comes first");
	}

	[Test]
	public async Task A_picture_the_key_already_shows_is_not_sent_again_until_the_logo_was_shown()
	{
		var connection = new FakeHidConnection();
		await using var device = Open(connection);
		var image = Image(0x55, 3000);

		device.SetKeyImage(0, image);
		await device.SetBrightnessAsync(10);
		device.SetKeyImage(0, image.ToArray());
		await device.SetBrightnessAsync(20);
		Assert.That(ImageWrites(connection.Writes).Count(), Is.EqualTo(3), "one picture of three chunks");

		device.SetKeyImage(0, Image(0x66, 10));
		device.SetKeyImage(0, image);
		await device.SetBrightnessAsync(30);
		Assert.That(ImageWrites(connection.Writes).Count(), Is.EqualTo(3), "latest wins and equals what is shown");

		await device.ShowLogoAsync();
		device.SetKeyImage(0, image);
		await device.SetBrightnessAsync(40);
		Assert.That(ImageWrites(connection.Writes).Count(), Is.EqualTo(6));
	}

	[Test]
	public async Task Chunks_of_two_keys_never_interleave_even_when_pictures_arrive_mid_key()
	{
		var connection = new FakeHidConnection();
		StreamDeckDevice? device = null;
		connection.BeforeWrite = index =>
		{
			// Write 0 is the reset, write 1 the first chunk of key 4. Submit more from inside the writer.
			if (index == 1)
			{
				device!.SetKeyImage(7, Image(0x07, 2500));
				device.SetKeyImage(4, Image(0x44, 2500));
			}
		};
		device = Open(connection);
		await using var _ = device;

		device.SetKeyImage(4, Image(0x04, 2500));
		await device.SetBrightnessAsync(1);

		var writes = await connection.WaitForWritesAsync(1 + 3 + 3 + 3 + 1);
		var sequence = ImageWrites(writes).Select(w => (Key: (int)w.Bytes[2], Chunk: w.Bytes[6] | (w.Bytes[7] << 8), Fill: w.Bytes[8])).ToList();
		Assert.That(sequence, Is.EqualTo(new[]
		{
			(4, 0, 0x04), (4, 1, 0x04), (4, 2, 0x04),
			(7, 0, 0x07), (7, 1, 0x07), (7, 2, 0x07),
			(4, 0, 0x44), (4, 1, 0x44), (4, 2, 0x44),
		}));
	}

	[Test]
	public async Task A_picture_submitted_before_the_logo_does_not_overtake_it()
	{
		var connection = new FakeHidConnection();
		using var release = new ManualResetEventSlim();
		connection.BeforeWrite = index =>
		{
			if (index == 0)
			{
				release.Wait(TimeSpan.FromSeconds(10));
			}
		};
		await using var device = Open(connection);

		device.SetKeyImage(1, Image(0x01, 10));
		var logo = device.ShowLogoAsync();
		device.SetKeyImage(1, Image(0x02, 10));
		release.Set();
		await logo;
		await device.SetBrightnessAsync(5);

		var writes = await connection.WaitForWritesAsync(4);
		Assert.That(writes.Skip(1).Select(w => (w.Kind, w.Bytes[1])), Is.EqualTo(new[]
		{
			(WriteKind.SetFeature, (byte)0x02),
			(WriteKind.Output, (byte)0x07),
			(WriteKind.SetFeature, (byte)0x08),
		}));
		Assert.That(writes[2].Bytes[8], Is.EqualTo(0x02));
	}

	[Test]
	public async Task A_pedal_has_no_displays_but_still_reports_its_keys()
	{
		var connection = new FakeHidConnection();
		await using var device = Open(connection, productId: 0x0086);
		var events = new EventRecorder<int>();
		device.KeyDown += events.Add;

		Assert.Throws<NotSupportedException>(() => device.SetKeyImage(0, Image(1, 10)));
		Assert.Throws<NotSupportedException>(() => device.SetBrightnessAsync(10));

		var report = new byte[512];
		report[0] = 0x01;
		report[4 + 2] = 1;
		connection.EnqueueInput(report);
		Assert.That(await events.WaitForAsync(1), Is.EqualTo((int[])[2]));
		Assert.That(connection.Writes, Is.Empty);
	}

	[Test]
	public async Task A_read_failure_raises_disconnected_exactly_once_and_ends_the_device()
	{
		var connection = new FakeHidConnection();
		var device = Create(connection, Model(Mk2));
		var disconnects = new EventRecorder<Exception>();
		device.Disconnected += disconnects.Add;
		device.Start();
		var failure = new IOException("unplugged");

		connection.FailRead(failure);
		var seen = await disconnects.WaitForAsync(1);
		var brightness = device.SetBrightnessAsync(10);

		Assert.That(seen[0], Is.SameAs(failure));
		Assert.That(connection.IsDisposed, Is.True);
		Assert.ThrowsAsync<InvalidOperationException>(async () => await brightness);

		await device.DisposeAsync();
		Assert.That(disconnects.Events, Has.Count.EqualTo(1));
	}

	[Test]
	public async Task A_write_failure_raises_disconnected_exactly_once_even_though_the_read_fails_afterwards()
	{
		var connection = new FakeHidConnection { WriteFailure = new IOException("write failed") };
		var device = Create(connection, Model(Mk2));
		var disconnects = new EventRecorder<Exception>();
		// Subscribed before Start, so the failing reset write cannot be missed.
		device.Disconnected += disconnects.Add;
		var queued = device.SetBrightnessAsync(10);
		device.Start();

		await disconnects.WaitForAsync(1);
		var thrown = Assert.ThrowsAsync<IOException>(async () => await queued);
		await device.DisposeAsync();

		Assert.That(disconnects.Events, Has.Count.EqualTo(1));
		Assert.That(disconnects.Events[0].Message, Is.EqualTo("write failed"));
		Assert.That(thrown, Is.SameAs(disconnects.Events[0]), "a command queued behind the failing write fails with it");
	}

	[Test]
	public async Task Disposing_a_healthy_device_does_not_raise_disconnected_and_closes_the_connection()
	{
		var connection = new FakeHidConnection();
		var device = Open(connection);
		var raised = 0;
		device.Disconnected += _ => Interlocked.Increment(ref raised);

		await device.DisposeAsync();
		await device.DisposeAsync();

		Assert.That(raised, Is.Zero);
		Assert.That(connection.IsDisposed, Is.True);
	}

	[Test]
	public async Task The_writer_holds_output_to_the_model_rate_using_the_injected_clock()
	{
		var time = new FakeTimeProvider();
		var connection = new FakeHidConnection();
		// 1024 bytes per second with a burst of two reports: the reset and the first chunk go out at once.
		var slow = Model(Mk2) with { BytesPerSecond = 1024 };
		await using var device = Started(Create(connection, slow, time));

		device.SetKeyImage(0, Image(0x09, 2200));

		await time.WaitForActiveTimerAsync();
		Assert.That(connection.Writes, Has.Count.EqualTo(2));

		time.Advance(TimeSpan.FromMilliseconds(999));
		Assert.That(connection.Writes, Has.Count.EqualTo(2), "not yet a second");

		time.Advance(TimeSpan.FromMilliseconds(1));
		await connection.WaitForWritesAsync(3);
		await time.WaitForActiveTimerAsync();
		Assert.That(connection.Writes, Has.Count.EqualTo(3));

		time.Advance(TimeSpan.FromSeconds(1));
		var writes = await connection.WaitForWritesAsync(1);
		Assert.That(writes, Has.Count.EqualTo(4));
	}

	[Test]
	public async Task Firmware_and_serial_are_read_through_the_writer_and_fall_back_between_candidates()
	{
		var connection = new FakeHidConnection
		{
			FeatureResponder = request =>
			{
				if (request[0] == 0x05)
				{
					var firmware = new byte[request.Length];
					firmware[0] = 0x05;
					firmware[1] = 0x0C;
					"3.00.001"u8.CopyTo(firmware.AsSpan(6));
					return firmware;
				}

				throw new IOException("unsupported");
			},
		};
		await using var device = Open(connection);

		Assert.That(await device.GetFirmwareVersionAsync(), Is.EqualTo("3.00.001"));
		Assert.Throws<IOException>(() => StreamDeckDevice.ReadSerialNumber(Model(Mk2), new FakeHidConnection { FeatureResponder = connection.FeatureResponder }));

		// A failed query is not a disconnect.
		Assert.That(connection.IsDisposed, Is.False);
	}

	[Test]
	public void The_serial_is_read_from_the_feature_report_and_an_empty_answer_is_a_failure()
	{
		var connection = new FakeHidConnection
		{
			FeatureResponder = request =>
			{
				var report = new byte[request.Length];
				report[0] = request[0];
				report[1] = 12;
				"AL12K1A01234"u8.CopyTo(report.AsSpan(2));
				return request[0] == 0x06 ? report : throw new IOException("unsupported");
			},
		};

		Assert.That(StreamDeckDevice.ReadSerialNumber(Model(Mk2), connection), Is.EqualTo("AL12K1A01234"));

		connection.FeatureResponder = request => new byte[request.Length];
		Assert.Throws<IOException>(() => StreamDeckDevice.ReadSerialNumber(Model(Mk2), connection));
	}

	[Test]
	public async Task Nothing_is_written_before_start_and_work_queued_before_goes_out_after_the_reset()
	{
		var connection = new FakeHidConnection();
		await using var device = Create(connection, Model(Mk2));
		device.SetKeyImage(0, Image(0x11, 100));
		var brightness = device.SetBrightnessAsync(40);

		Assert.That(connection.Writes, Is.Empty);
		device.Start();
		await brightness;

		var writes = await connection.WaitForWritesAsync(3);
		Assert.That(writes.Select(w => (w.Kind, w.Bytes[1])), Is.EqualTo(new[]
		{
			(WriteKind.Output, (byte)0x00),
			(WriteKind.Output, (byte)0x07),
			(WriteKind.SetFeature, (byte)0x08),
		}));
	}

	[Test]
	public async Task A_refused_brightness_or_logo_report_fails_that_command_and_the_device_stays_open()
	{
		var (logger, sink) = CollectingSink.Create();
		var connection = new FakeHidConnection();
		var device = Create(connection, Model(Mk2), logger: logger);
		var disconnects = 0;
		device.Disconnected += _ => Interlocked.Increment(ref disconnects);
		device.Start();
		await using var _ = device;
		await connection.WaitForWritesAsync(1);

		connection.FeatureFailure = new IOException("report rejected");
		Assert.ThrowsAsync<IOException>(async () => await device.SetBrightnessAsync(10));
		Assert.ThrowsAsync<IOException>(async () => await device.ShowLogoAsync());
		connection.FeatureFailure = null;
		await device.SetBrightnessAsync(20);

		Assert.That(connection.IsDisposed, Is.False);
		Assert.That(disconnects, Is.Zero);
		Assert.That(device.Counters.Snapshot().WriteFailures, Is.EqualTo(2));
		Assert.That(device.Counters.Snapshot().LastError, Does.Contain("report rejected"));
		var warnings = sink.Messages(Serilog.Events.LogEventLevel.Warning);
		Assert.That(warnings, Has.Count.EqualTo(1), "one warning per run of refusals");
		Assert.That(warnings[0], Does.Contain(StreamDeckSupport.IssuesUrl));
		Assert.That(connection.Writes[^1].Bytes[2], Is.EqualTo(20));
	}

	[Test]
	public async Task A_command_the_writer_is_executing_when_the_device_is_disposed_still_completes()
	{
		var time = new FakeTimeProvider();
		var connection = new FakeHidConnection();
		// The burst holds exactly the reset and one image report, so the brightness waits for the clock.
		var slow = Model(Mk2) with { BytesPerSecond = 1024 };
		var device = Create(connection, slow, time);
		device.SetKeyImage(0, Image(0x01, 100));
		var brightness = device.SetBrightnessAsync(10);
		device.Start();
		await connection.WaitForWritesAsync(2);
		await time.WaitForActiveTimerAsync();

		await device.DisposeAsync();

		Assert.ThrowsAsync<InvalidOperationException>(async () => await brightness.WaitAsync(TimeSpan.FromSeconds(10)));
	}

	[Test]
	public async Task A_command_cancelled_by_its_caller_while_it_waits_completes_as_cancelled()
	{
		var time = new FakeTimeProvider();
		var connection = new FakeHidConnection();
		var slow = Model(Mk2) with { BytesPerSecond = 1024 };
		await using var device = Create(connection, slow, time);
		using var cancel = new CancellationTokenSource();
		device.SetKeyImage(0, Image(0x01, 100));
		var brightness = device.SetBrightnessAsync(10, cancel.Token);
		device.Start();
		await time.WaitForActiveTimerAsync();

		await cancel.CancelAsync();

		Assert.CatchAsync<OperationCanceledException>(async () => await brightness.WaitAsync(TimeSpan.FromSeconds(10)));
	}

	[Test]
	public async Task A_refused_keep_alive_is_counted_and_warned_about_once_without_ending_the_device()
	{
		var (logger, sink) = CollectingSink.Create();
		var time = new FakeTimeProvider();
		var connection = new FakeHidConnection { FeatureFailure = new IOException("pipe stalled") };
		StreamDeckModels.TryGet(0x1B1C, 0x2B18, out var galleon);
		await using var device = Started(Create(connection, galleon, time, logger));
		await connection.WaitForWritesAsync(1);

		await time.WaitForActiveTimerAsync();
		time.Advance(TimeSpan.FromMilliseconds(500));
		await connection.WaitForFeatureAttemptsAsync(1);
		await time.WaitForActiveTimerAsync();
		time.Advance(TimeSpan.FromMilliseconds(500));
		await connection.WaitForFeatureAttemptsAsync(1);
		await sink.WaitForAsync(e => e.Level == Serilog.Events.LogEventLevel.Debug && e.RenderMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("KeepAlive", StringComparison.Ordinal));

		Assert.That(connection.IsDisposed, Is.False);
		Assert.That(device.Counters.Snapshot().WriteFailures, Is.EqualTo(2));
		Assert.That(sink.Messages(Serilog.Events.LogEventLevel.Warning), Has.Count.EqualTo(1));
	}
}
