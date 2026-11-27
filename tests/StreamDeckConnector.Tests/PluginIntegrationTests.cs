using MacroDeck.Plugin.Hosting;
using MacroDeck.Plugin.Testing;
using MacroDeck.Plugin.Testing.Fakes;
using MacroDeck.Sdk.Devices;
using MacroDeck.Sdk.Issues;
using MacroDeck.Sdk.Layouts;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Serilog.Events;
using StreamDeckConnector.Devices;
using StreamDeckConnector.Hid;
using StreamDeckConnector.Tests.Devices;

namespace StreamDeckConnector.Tests;

[TestFixture]
public sealed class PluginHarnessTests
{
	private static PluginTestHarness CreateHarness(FakeHidDeviceSource source) =>
		PluginTestHarness.Create(builder =>
		{
			builder.Services.AddSingleton<IHidDeviceSource>(source);
			builder.Services.AddStreamDeckTransport();
			builder
				.UseLocalization(Strings.LocalizationCatalog)
				.RegisterIntegration<PluginIntegration>();
		});

	[Test]
	public async Task The_plugin_builds_and_initializes_without_any_device()
	{
		await using var harness = CreateHarness(new FakeHidDeviceSource());

		Assert.DoesNotThrowAsync(harness.InitializeIntegrationsAsync);
	}

	[Test]
	public async Task Setting_the_brightness_without_a_device_fails_truthfully()
	{
		await using var harness = CreateHarness(new FakeHidDeviceSource());
		await harness.InitializeIntegrationsAsync();

		var outcome = await harness.Actions.ExecuteAsync("set-brightness", new Dictionary<string, object?> { ["brightness"] = 50 });

		Assert.That(outcome.Succeeded, Is.False);
	}

	[Test]
	public async Task The_diagnostics_action_writes_the_snapshot_to_the_log()
	{
		await using var harness = CreateHarness(new FakeHidDeviceSource());
		await harness.InitializeIntegrationsAsync();

		var outcome = await harness.Actions.ExecuteAsync("write-diagnostics", new Dictionary<string, object?> { ["trace-raw-reports"] = false });

		Assert.That(outcome.Succeeded, Is.True);
		Assert.That(harness.Logs.Events.Any(e => e.Message.Contains("Stream Deck diagnostics")), Is.True);
	}
}

[TestFixture]
public sealed class LayoutTests
{
	[Test]
	public async Task Every_model_registers_one_layout_under_a_unique_id()
	{
		var context = new FakeLayoutProviderContext();
		await using var fixture = await Fixture.CreateAsync();

		await fixture.Integration.InitializeAsync(context);

		Assert.That(context.Layouts, Has.Count.EqualTo(StreamDeckModels.All.Count));
	}

	[Test]
	public void The_mk2_layout_is_a_fixed_3x5_grid_of_single_key_widgets_with_rotated_jpeg_pictures()
	{
		StreamDeckModels.TryGet(0x0FD9, 0x0080, out var mk2);

		var layout = StreamDeckLayouts.For(mk2);
		var grid = layout.PrimaryGrid!.Grid!;
		var visuals = layout.Capabilities!.Visuals!;

		Assert.Multiple(() =>
		{
			Assert.That((grid.Rows, grid.Columns), Is.EqualTo((3, 5)));
			Assert.That(grid.RowsLocked && grid.ColumnsLocked, Is.True);
			Assert.That(grid.KeySize, Is.EqualTo(new LayoutKeySize(72, 72)));
			Assert.That(grid.KeyImages, Is.EqualTo(new LayoutKeyImageFormat { Encoding = KeyImageEncodings.Jpeg, Rotation = 180 }));
			Assert.That((grid.MaxWidgetColumns, grid.MaxWidgetRows), Is.EqualTo(((int?)1, (int?)1)));
			Assert.That(visuals.AnimatedIcons && visuals.Borders, Is.True);
			Assert.That(visuals.ScreenSavers, Is.False);
			Assert.That(visuals.CustomFolderViews, Is.False);
			Assert.That(visuals.WidgetSpacing || visuals.CornerRadius, Is.False);
		});
	}

	[Test]
	public void Every_layout_forbids_screen_savers_and_widgets_larger_than_one_key()
	{
		foreach (var layout in StreamDeckLayouts.All)
		{
			var grid = layout.PrimaryGrid!.Grid!;
			Assert.That(layout.Capabilities!.Visuals!.ScreenSavers, Is.False, layout.Name);
			Assert.That((grid.MaxWidgetColumns, grid.MaxWidgetRows), Is.EqualTo(((int?)1, (int?)1)), layout.Name);
		}
	}

	[Test]
	public void A_model_without_displays_asks_for_no_key_pictures()
	{
		StreamDeckModels.TryGet(0x0FD9, 0x0086, out var pedal);

		Assert.That(StreamDeckLayouts.For(pedal).PrimaryGrid!.Grid!.KeyImages, Is.Null);
	}

	[Test]
	public void The_bitmap_models_ask_for_bitmap_pictures()
	{
		StreamDeckModels.TryGet(0x0FD9, 0x0063, out var mini);

		var format = StreamDeckLayouts.KeyImageFormat(mini)!;

		Assert.That((format.Encoding, format.Rotation), Is.EqualTo((KeyImageEncodings.Bmp, 90)));
	}
}

[TestFixture]
public sealed class DeviceProviderTests
{
	private static readonly LayoutKeyImageFormat _mk2Format = new() { Encoding = KeyImageEncodings.Jpeg, Rotation = 180 };

	[Test]
	public async Task A_plugged_in_deck_is_registered_by_serial_with_its_layout()
	{
		await using var fixture = await Fixture.CreateAsync(Fixture.Mk2());
		await fixture.StartProvidersAsync();

		var device = fixture.Devices.Devices["SN-1"];
		Assert.Multiple(() =>
		{
			Assert.That(device.Model, Is.EqualTo("Stream Deck MK.2"));
			Assert.That(device.LayoutReference, Is.EqualTo("deck-0fd9-0080"));
			Assert.That(device.Capabilities!.KeyCount, Is.EqualTo(15));
			Assert.That(fixture.Devices.IsOnline("SN-1"), Is.True);
		});
	}

	[Test]
	public async Task Opening_a_session_paints_every_key_from_the_host_pictures()
	{
		await using var fixture = await Fixture.CreateAsync(Fixture.Mk2());
		await fixture.StartProvidersAsync();
		var session = fixture.NewSession(_mk2Format);
		Fixture.SeedAllKeys(session, rows: 3, columns: 5, revision: 1);

		await fixture.Integration.OnSessionOpenedAsync(session);
		await Fixture.EventuallyAsync(() => fixture.PaintedKeys("mk2").Count == 15);

		Assert.That(session.KeyImageRequests, Has.Count.EqualTo(15));
		Assert.That(fixture.PaintedKeys("mk2").Order(), Is.EqualTo(Enumerable.Range(0, 15)));
	}

	[Test]
	public async Task A_changed_key_picture_is_written_and_a_stale_one_is_dropped()
	{
		await using var fixture = await Fixture.CreateAsync(Fixture.Mk2());
		await fixture.StartProvidersAsync();
		var session = fixture.NewSession(_mk2Format);
		await fixture.Integration.OnSessionOpenedAsync(session);

		session.PushKeyImage(Fixture.Picture(0, 2, revision: 5, fill: 0x11));
		await Fixture.EventuallyAsync(() => fixture.PaintedKeys("mk2").Count == 1);
		session.PushKeyImage(Fixture.Picture(0, 2, revision: 4, fill: 0x22));
		session.PushKeyImage(Fixture.Picture(1, 0, revision: 1, fill: 0x33));
		await Fixture.EventuallyAsync(() => fixture.PaintedKeys("mk2").Count == 2);

		Assert.That(fixture.PaintedKeys("mk2"), Is.EqualTo((int[])[2, 5]));
	}

	[Test]
	public async Task A_key_press_reaches_the_widget_at_that_position_as_press_then_release()
	{
		await using var fixture = await Fixture.CreateAsync(Fixture.Mk2());
		await fixture.StartProvidersAsync();
		var session = fixture.NewSession(_mk2Format);
		await fixture.Integration.OnSessionOpenedAsync(session);
		session.PushSurface(Fixture.Surface(revision: 2, Fixture.Widget("w-7", column: 2, row: 1, DeviceInteractionKind.Press, DeviceInteractionKind.Release)));

		fixture.Connection("mk2").EnqueueInput(Fixture.Gen2Keys(15, pressed: 7));
		fixture.Connection("mk2").EnqueueInput(Fixture.Gen2Keys(15));
		await Fixture.EventuallyAsync(() => session.Interactions.Count == 2);

		Assert.Multiple(() =>
		{
			Assert.That(session.Interactions.Select(i => i.Kind), Is.EqualTo((DeviceInteractionKind[])[DeviceInteractionKind.Press, DeviceInteractionKind.Release]));
			Assert.That(session.Interactions.Select(i => i.Target.WidgetId), Is.All.EqualTo("w-7"));
			Assert.That(session.Interactions[0].SurfaceRevision, Is.EqualTo(2));
		});
	}

	[Test]
	public async Task A_widget_that_only_takes_whole_presses_gets_a_short_press()
	{
		await using var fixture = await Fixture.CreateAsync(Fixture.Mk2());
		await fixture.StartProvidersAsync();
		var session = fixture.NewSession(_mk2Format);
		await fixture.Integration.OnSessionOpenedAsync(session);
		session.PushSurface(Fixture.Surface(revision: 2, Fixture.Widget("w-0", column: 0, row: 0, DeviceInteractionKind.ShortPress)));

		fixture.Connection("mk2").EnqueueInput(Fixture.Gen2Keys(15, pressed: 0));
		fixture.Connection("mk2").EnqueueInput(Fixture.Gen2Keys(15));
		await Fixture.EventuallyAsync(() => session.Interactions.Count == 1);
		fixture.Connection("mk2").EnqueueInput(Fixture.Gen2Keys(15, pressed: 3));
		fixture.Connection("mk2").EnqueueInput(Fixture.Gen2Keys(15));
		await Fixture.EventuallyAsync(() => fixture.Integration.DescribeDiagnostics().Contains("key events 4", StringComparison.Ordinal));

		Assert.That(session.Interactions.Select(i => i.Kind), Is.EqualTo((DeviceInteractionKind[])[DeviceInteractionKind.ShortPress]));
	}

	[Test]
	public async Task A_key_without_a_widget_or_without_press_support_sends_nothing()
	{
		await using var fixture = await Fixture.CreateAsync(Fixture.Mk2());
		await fixture.StartProvidersAsync();
		var session = fixture.NewSession(_mk2Format);
		await fixture.Integration.OnSessionOpenedAsync(session);
		session.PushSurface(Fixture.Surface(revision: 2, Fixture.Widget("label-only", column: 1, row: 0)));

		fixture.Connection("mk2").EnqueueInput(Fixture.Gen2Keys(15, pressed: 1));
		fixture.Connection("mk2").EnqueueInput(Fixture.Gen2Keys(15, pressed: 9));
		fixture.Connection("mk2").EnqueueInput(Fixture.Gen2Keys(15));
		await Fixture.EventuallyAsync(() => fixture.Integration.DescribeDiagnostics().Contains("key events 4", StringComparison.Ordinal));

		Assert.That(session.Interactions, Is.Empty);
	}

	[Test]
	public async Task An_unplugged_deck_goes_offline_and_a_replugged_one_is_painted_again()
	{
		await using var fixture = await Fixture.CreateAsync(Fixture.Mk2());
		await fixture.StartProvidersAsync();
		var session = fixture.NewSession(_mk2Format);
		Fixture.SeedAllKeys(session, rows: 3, columns: 5, revision: 3);
		await fixture.Integration.OnSessionOpenedAsync(session);
		await Fixture.EventuallyAsync(() => fixture.PaintedKeys("mk2").Count == 15);
		var first = fixture.Connection("mk2");

		fixture.Source.Unplug("mk2");
		fixture.Source.RaiseChanged();
		await Fixture.EventuallyAsync(() => !fixture.Devices.IsOnline("SN-1"));
		fixture.Source.Plug(Fixture.Mk2());
		fixture.Source.RaiseChanged();
		await Fixture.EventuallyAsync(() => !ReferenceEquals(fixture.Connection("mk2"), first));
		await Fixture.EventuallyAsync(() => fixture.PaintedKeys("mk2").Count == 15);

		Assert.That(fixture.Devices.IsOnline("SN-1"), Is.True);
	}

	[Test]
	public async Task The_provider_shutdown_shows_the_logo()
	{
		await using var fixture = await Fixture.CreateAsync(Fixture.Mk2());
		await fixture.StartProvidersAsync();

		await ((IDeviceProvider)fixture.Integration).ShutdownAsync();

		Assert.That(fixture.Connection("mk2").Writes.Any(w => w.Kind == WriteKind.SetFeature && w.Bytes[0] == 0x03 && w.Bytes[1] == 0x02), Is.True);
	}
}

[TestFixture]
public sealed class IssueTests
{
	[Test]
	public async Task A_verified_deck_on_a_current_host_reports_no_issue()
	{
		await using var fixture = await Fixture.CreateAsync(Fixture.Mk2());
		await fixture.StartProvidersAsync();
		await fixture.Integration.OnSessionOpenedAsync(fixture.NewSession(new LayoutKeyImageFormat { Encoding = KeyImageEncodings.Jpeg, Rotation = 180 }));

		Assert.That(await fixture.Integration.GetIssuesAsync(), Is.Empty);
	}

	[Test]
	public async Task A_host_that_sends_no_key_pictures_is_an_error_issue()
	{
		await using var fixture = await Fixture.CreateAsync(Fixture.Mk2());
		await fixture.StartProvidersAsync();
		await fixture.Integration.OnSessionOpenedAsync(fixture.NewSession(null));

		var issues = await fixture.Integration.GetIssuesAsync();

		Assert.That(issues.Select(i => (i.Id, i.Severity)), Is.EqualTo(((string, IntegrationIssueSeverity)[])[("host-renders-no-key-images", IntegrationIssueSeverity.Error)]));
	}

	[Test]
	public async Task An_unsupported_stream_deck_is_an_issue_that_links_to_a_new_issue_and_writes_diagnostics()
	{
		await using var fixture = await Fixture.CreateAsync(new HidDeviceInfo("studio", 0x0FD9, 0x00AA, "ST-1", "Stream Deck Studio", 64, 1024, 32));
		await fixture.StartProvidersAsync();

		var issue = (await fixture.Integration.GetIssuesAsync()).Single();
		var resolution = await fixture.Integration.ResolveIssueAsync(issue.Id);

		Assert.Multiple(() =>
		{
			Assert.That(issue.Id, Does.StartWith("unsupported-"));
			Assert.That(issue.Severity, Is.EqualTo(IntegrationIssueSeverity.Warning));
			Assert.That(issue.HelpUrl, Is.EqualTo(new Uri("https://github.com/manuelmayer-dev/MacroDeckStreamDeckPlugin/issues/new")));
			Assert.That(resolution.Success, Is.True);
			Assert.That(fixture.Sink.Messages(LogEventLevel.Information).Any(m => m.Contains("product 0x00AA", StringComparison.Ordinal) && m.Contains("Stream Deck diagnostics", StringComparison.Ordinal)), Is.True);
			Assert.That(fixture.Sink.Messages(LogEventLevel.Information).Any(m => m.Contains("ST-1", StringComparison.Ordinal)), Is.False);
		});
	}

	[Test]
	public async Task A_model_never_tested_on_hardware_is_an_informational_issue()
	{
		await using var fixture = await Fixture.CreateAsync(new HidDeviceInfo("xl", 0x0FD9, 0x006C, "XL-1"));
		await fixture.StartProvidersAsync();

		var issue = (await fixture.Integration.GetIssuesAsync()).Single();

		Assert.That((issue.Id, issue.Severity), Is.EqualTo(("untested-deck-0fd9-006c", IntegrationIssueSeverity.Info)));
	}

	[Test]
	public async Task A_deck_that_cannot_be_opened_is_a_warning_issue()
	{
		var source = new FakeHidDeviceSource { OpenFailure = _ => new UnauthorizedAccessException("access denied") };
		source.Plug(Fixture.Mk2());
		await using var fixture = await Fixture.CreateAsync(source);
		await fixture.StartProvidersAsync();

		var issue = (await fixture.Integration.GetIssuesAsync()).Single();

		Assert.That((issue.Id, issue.Severity), Is.EqualTo(("device-open-failed", IntegrationIssueSeverity.Warning)));
	}

	[Test]
	public async Task An_unknown_issue_id_is_not_resolved()
	{
		await using var fixture = await Fixture.CreateAsync();

		Assert.That((await fixture.Integration.ResolveIssueAsync("host-renders-no-key-images")).Success, Is.False);
	}
}

[TestFixture]
public sealed class LocalizationTests
{
	[Test]
	public void The_catalog_is_scoped_to_the_plugin_id()
	{
		Assert.That(Strings.LocalizationCatalog.Scope, Is.EqualTo("plugin:com.manuelmayer.streamdeck-connector"));
	}

	[Test]
	public void Every_key_the_default_culture_declares_resolves_to_text()
	{
		foreach (var key in Strings.LocalizationCatalog.KeysOf("en"))
		{
			Assert.That(Strings.LocalizationCatalog.TryGetTemplate("en", key, out var text), Is.True);
			Assert.That(text, Is.Not.Empty);
		}
	}
}

internal sealed class Fixture : IAsyncDisposable
{
	private readonly StreamDeckDeviceManager _manager;

	private Fixture(FakeHidDeviceSource source)
	{
		Source = source;
		var (logger, sink) = CollectingSink.Create();
		Sink = sink;
		var options = new StreamDeckDiagnosticsOptions();
		_manager = new StreamDeckDeviceManager(source, new FakeTimeProvider(), logger, options);
		Integration = new PluginIntegration(logger, _manager, options);
	}

	public FakeHidDeviceSource Source { get; }

	public CollectingSink Sink { get; }

	public PluginIntegration Integration { get; }

	public FakeDeviceProviderContext Devices { get; } = new();

	public FakeLayoutProviderContext Layouts { get; } = new();

	public static HidDeviceInfo Mk2() => new("mk2", 0x0FD9, 0x0080, "SN-1");

	public static Task<Fixture> CreateAsync(params HidDeviceInfo[] plugged)
	{
		var source = new FakeHidDeviceSource();
		foreach (var info in plugged)
		{
			source.Plug(info);
		}

		return CreateAsync(source);
	}

	public static Task<Fixture> CreateAsync(FakeHidDeviceSource source) => Task.FromResult(new Fixture(source));

	public static DeviceKeyImage Picture(int row, int column, long revision, byte fill) => new()
	{
		Row = row,
		Column = column,
		Revision = revision,
		ContentType = KeyImageContentTypes.Jpeg,
		Content = Enumerable.Repeat(fill, 64).ToArray(),
	};

	public static void SeedAllKeys(FakeDeviceSession session, int rows, int columns, long revision)
	{
		for (var row = 0; row < rows; row++)
		{
			for (var column = 0; column < columns; column++)
			{
				session.KeyImages[(row, column)] = Picture(row, column, revision, (byte)((row * columns) + column + 1));
			}
		}
	}

	public static byte[] Gen2Keys(int keyCount, int pressed = -1)
	{
		var report = new byte[4 + keyCount];
		report[0] = 0x01;
		report[2] = (byte)keyCount;
		if (pressed >= 0)
		{
			report[4 + pressed] = 1;
		}

		return report;
	}

	public static DeviceSurface Surface(long revision, params DeviceSurfaceWidget[] widgets) => new()
	{
		Revision = revision,
		Layout = new DeviceSurfaceLayout { Rows = 3, Columns = 5 },
		Widgets = widgets,
	};

	public static DeviceSurfaceWidget Widget(string id, int column, int row, params DeviceInteractionKind[] supported) => new()
	{
		Id = id,
		Type = "action-button",
		PositionX = column,
		PositionY = row,
		Width = 1,
		Height = 1,
		SupportedInteractions = supported,
	};

	public static async Task EventuallyAsync(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (!condition())
		{
			Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "condition was not met in time");
			await Task.Delay(10);
		}
	}

	public async Task StartProvidersAsync()
	{
		await Integration.InitializeAsync(new FakeIntegrationContext());
		await Integration.InitializeAsync(Layouts);
		await Integration.InitializeAsync(Devices);
	}

	public FakeDeviceSession NewSession(LayoutKeyImageFormat? format) =>
		new(Devices.AssignedIdOf("SN-1") ?? "device", "SN-1", Devices) { KeyImageFormat = format };

	public FakeHidConnection Connection(string path) => Source.Connections[path];

	/// <summary>Key indices of the gen 2 image reports written so far, in order.</summary>
	public IReadOnlyList<int> PaintedKeys(string path) =>
		[.. Connection(path).Writes.Where(w => w.Kind == WriteKind.Output && w.Bytes[0] == 0x02 && w.Bytes[1] == 0x07).Select(w => (int)w.Bytes[2])];

	public async ValueTask DisposeAsync() => await _manager.DisposeAsync();
}
