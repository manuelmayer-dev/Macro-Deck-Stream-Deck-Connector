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
		new(Model(productId), "SERIAL", connection, time ?? new FakeTimeProvider(), Serilog.Core.Logger.None, new StreamDeckDiagnosticsOptions());

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
	public async Task Clearing_a_key_sends_the_black_fill_feature_report_once()
	{
		var connection = new FakeHidConnection();
		await using var device = Open(connection);

		device.ClearKey(5);
		await device.SetBrightnessAsync(1);
		device.ClearKey(5);
		await device.SetBrightnessAsync(2);

		var writes = await connection.WaitForWritesAsync(1 + 1 + 1 + 1);
		var expected = new byte[32];
		expected[0] = 0x03;
		expected[1] = 0x06;
		expected[2] = 5;
		Assert.That(writes.Where(w => w.Kind == WriteKind.SetFeature && w.Bytes[1] == 0x06).Select(w => w.Bytes), Is.EqualTo(new[] { expected }));
	}

	[Test]
	public async Task Clearing_a_key_is_refused_where_the_protocol_cannot_do_it()
	{
		await using var device = Open(new FakeHidConnection(), productId: 0x0063);

		Assert.Throws<NotSupportedException>(() => device.ClearKey(0));
	}

	[Test]
	public async Task A_pedal_has_no_displays_but_still_reports_its_keys()
	{
		var connection = new FakeHidConnection();
		await using var device = Open(connection, productId: 0x0086);
		var events = new EventRecorder<int>();
		device.KeyDown += events.Add;

		Assert.Throws<NotSupportedException>(() => device.SetKeyImage(0, Image(1, 10)));
		Assert.Throws<NotSupportedException>(() => device.ClearKey(0));
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
		var device = Open(connection);
		var disconnects = new EventRecorder<Exception>();
		device.Disconnected += disconnects.Add;
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
		var device = Open(connection);
		var disconnects = new EventRecorder<Exception>();
		device.Disconnected += disconnects.Add;

		await disconnects.WaitForAsync(1);
		await device.DisposeAsync();

		Assert.That(disconnects.Events, Has.Count.EqualTo(1));
		Assert.That(disconnects.Events[0].Message, Is.EqualTo("write failed"));
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
		await using var device = new StreamDeckDevice(slow, "SERIAL", connection, time, Serilog.Core.Logger.None, new StreamDeckDiagnosticsOptions());

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
		Assert.ThrowsAsync<IOException>(async () => await device.GetSerialNumberAsync());

		// A failed query is not a disconnect.
		Assert.That(connection.IsDisposed, Is.False);
	}
}
