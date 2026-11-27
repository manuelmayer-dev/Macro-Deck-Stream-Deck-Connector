using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Devices;
using MacroDeck.Sdk.Issues;
using MacroDeck.Sdk.Layouts;
using Serilog;
using StreamDeckConnector.Actions;
using StreamDeckConnector.Devices;

namespace StreamDeckConnector;

/// <summary>
/// Offers every attached Stream Deck to Macro Deck as a device. Macro Deck draws the keys, this plugin
/// only moves the finished pictures to the hardware and the key presses back.
/// </summary>
public sealed class PluginIntegration : IPluginIntegration, ILayoutProvider, IDeviceProvider, IIntegrationIssueProvider
{
	private const string Manufacturer = "Elgato";
	private const string HidUnavailableIssue = "hid-unavailable";
	private const string HostTooOldIssue = "host-renders-no-key-images";
	private const string OpenFailedIssue = "device-open-failed";
	private const string UnsupportedIssuePrefix = "unsupported-";
	private const string UntestedIssuePrefix = "untested-";
	private static readonly Uri _newIssueUrl = new(StreamDeckSupport.IssuesUrl + "/new");
	private static readonly TimeSpan _logoTimeout = TimeSpan.FromSeconds(1);

	private readonly ILogger _logger;
	private readonly StreamDeckDeviceManager _manager;
	private readonly StreamDeckDiagnosticsOptions _diagnostics;
	private readonly ConcurrentDictionary<string, DeviceBridge> _bridges = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, string> _layoutIds = new(StringComparer.Ordinal);
	private readonly object _startGate = new();
	private Task? _started;
	private volatile IDeviceProviderContext? _devices;
	private volatile string? _hidFailure;

	public PluginIntegration(ILogger logger, StreamDeckDeviceManager manager, StreamDeckDiagnosticsOptions diagnostics)
	{
		_logger = logger.ForContext<PluginIntegration>();
		_manager = manager;
		_diagnostics = diagnostics;
		Actions =
		[
			new SetBrightnessAction(manager),
			new WriteDiagnosticsAction(this, diagnostics),
		];
	}

	public IReadOnlyList<IActionDefinition> Actions { get; }

	public string ProviderName => "Stream Deck";

	public Task InitializeAsync(IIntegrationContext context) => EnsureStartedAsync();

	public Task ShutdownAsync() => Task.CompletedTask;

	public async Task InitializeAsync(ILayoutProviderContext context, CancellationToken cancellationToken = default)
	{
		foreach (var layout in StreamDeckLayouts.All)
		{
			var registration = await context.RegisterLayoutAsync(layout, cancellationToken).ConfigureAwait(false);
			_layoutIds[layout.Id] = registration.LayoutId;
		}
	}

	public IReadOnlyList<LayoutDescriptor> GetLayouts() => StreamDeckLayouts.All;

	public async Task InitializeAsync(IDeviceProviderContext context, CancellationToken cancellationToken = default)
	{
		_devices = context;
		await EnsureStartedAsync().ConfigureAwait(false);
		foreach (var device in _manager.Devices)
		{
			await RegisterAsync(device, cancellationToken).ConfigureAwait(false);
		}
	}

	public async Task ShutdownAsync(CancellationToken cancellationToken = default)
	{
		_devices = null;
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(_logoTimeout);
		await Task.WhenAll(_bridges.Values.Select(bridge => bridge.ShowLogoAsync(timeout.Token))).ConfigureAwait(false);
	}

	public IReadOnlyList<DeviceDescriptor> GetDevices() => [.. _manager.Devices.Select(Describe)];

	public Task OnSessionOpenedAsync(IDeviceSession session, CancellationToken cancellationToken = default)
	{
		if (!_bridges.TryGetValue(session.ProviderDeviceId, out var bridge))
		{
			_logger.Warning("Macro Deck opened a session for a device this plugin does not know.");
			return Task.CompletedTask;
		}

		if (bridge.Model.HasKeyImages && session.KeyImageFormat is null)
		{
			_logger.Warning(
				"Macro Deck sends no key pictures for {Model:l}. This plugin needs a Macro Deck version that renders key images, so the keys stay dark.",
				bridge.Model.Name);
		}

		return bridge.OpenSessionAsync(session);
	}

	public Task<IReadOnlyList<IntegrationIssue>> GetIssuesAsync(CancellationToken cancellationToken = default)
	{
		List<IntegrationIssue> issues = [];
		if (_hidFailure is { } hidFailure)
		{
			issues.Add(new IntegrationIssue
			{
				Id = HidUnavailableIssue,
				Title = Strings.Issues.HidUnavailable.Title(),
				Description = Strings.Issues.HidUnavailable.Description(hidFailure),
				Severity = IntegrationIssueSeverity.Error,
				HelpUrl = _newIssueUrl,
			});
		}

		if (_bridges.Values.Any(bridge => bridge.HostSendsNoPictures))
		{
			issues.Add(new IntegrationIssue
			{
				Id = HostTooOldIssue,
				Title = Strings.Issues.HostTooOld.Title(),
				Description = Strings.Issues.HostTooOld.Description(),
				Severity = IntegrationIssueSeverity.Error,
			});
		}

		foreach (var failure in _manager.OpenFailures.Take(1))
		{
			issues.Add(new IntegrationIssue
			{
				Id = OpenFailedIssue,
				Title = Strings.Issues.OpenFailed.Title(failure.ModelName),
				Description = Strings.Issues.OpenFailed.Description(failure.Error),
				Severity = IntegrationIssueSeverity.Warning,
				ActionLabel = Strings.Issues.WriteDiagnostics(),
				HelpUrl = new Uri(StreamDeckSupport.ReadmeUrl),
			});
		}

		foreach (var device in _manager.UnsupportedDevices)
		{
			issues.Add(new IntegrationIssue
			{
				Id = UnsupportedIssuePrefix + device.Key.ToLowerInvariant(),
				Title = Strings.Issues.Unsupported.Title(),
				Description = Strings.Issues.Unsupported.Description(
					ProductLabel(device),
					Hex(device.VendorId),
					Hex(device.ProductId)),
				Severity = IntegrationIssueSeverity.Warning,
				ActionLabel = Strings.Issues.WriteDiagnostics(),
				HelpUrl = _newIssueUrl,
			});
		}

		foreach (var model in _manager.Devices.Select(device => device.Model).Where(model => !model.VerifiedOnHardware).Distinct())
		{
			issues.Add(new IntegrationIssue
			{
				Id = UntestedIssuePrefix + StreamDeckLayouts.LocalId(model),
				Title = Strings.Issues.Untested.Title(model.Name),
				Description = Strings.Issues.Untested.Description(),
				Severity = IntegrationIssueSeverity.Info,
				ActionLabel = Strings.Issues.WriteDiagnostics(),
				HelpUrl = _newIssueUrl,
			});
		}

		return Task.FromResult<IReadOnlyList<IntegrationIssue>>(issues);
	}

	public Task<IssueResolution> ResolveIssueAsync(string issueId, CancellationToken cancellationToken = default)
	{
		var writesDiagnostics = issueId == OpenFailedIssue
			|| issueId.StartsWith(UnsupportedIssuePrefix, StringComparison.Ordinal)
			|| issueId.StartsWith(UntestedIssuePrefix, StringComparison.Ordinal);
		if (!writesDiagnostics)
		{
			return Task.FromResult(IssueResolution.Failed(Strings.Issues.NothingToDo()));
		}

		WriteDiagnosticsToLog();
		return Task.FromResult(IssueResolution.Ok(Strings.Issues.DiagnosticsWritten()));
	}

	public void WriteDiagnosticsToLog() => _logger.Information("{Diagnostics:l}", DescribeDiagnostics());

	/// <summary>A plain text snapshot a user can paste into an issue. Serial numbers are masked.</summary>
	public string DescribeDiagnostics()
	{
		var text = new StringBuilder(_manager.DescribeDiagnostics());
		text.AppendLine();
		text.AppendLine(CultureInfo.InvariantCulture, $"HID access: {_hidFailure ?? "working"}");
		text.AppendLine(CultureInfo.InvariantCulture, $"Macro Deck sessions: {_bridges.Count}");
		foreach (var bridge in _bridges.Values)
		{
			var pictures = bridge.HostSendsNoPictures ? "host sends no key pictures" : "key pictures from host";
			text.AppendLine(CultureInfo.InvariantCulture, $"- {bridge.Model.Name} | serial ending {SerialMask.Tail(bridge.SerialNumber)} | session {(bridge.HasSession ? "open" : "closed")} | {pictures} | interactions sent {bridge.InteractionsSent} | last interaction problem: {bridge.LastInteractionProblem ?? "none"}");
		}

		return text.ToString();
	}

	private static string Hex(int value) => string.Create(CultureInfo.InvariantCulture, $"0x{value:X4}");

	private static string ProductLabel(UnsupportedDevice device) =>
		string.IsNullOrWhiteSpace(device.ProductName) ? Hex(device.ProductId) : device.ProductName;

	private Task EnsureStartedAsync()
	{
		lock (_startGate)
		{
			return _started ??= StartAsync();
		}
	}

	private async Task StartAsync()
	{
		_manager.DeviceConnected += OnDeviceConnected;
		_manager.DeviceDisconnected += OnDeviceDisconnected;
		try
		{
			await _manager.StartAsync().ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			// A missing native HID library or a blocked device node must not take the whole plugin down.
			_hidFailure = ex.Message;
			_logger.Error(
				ex,
				"USB HID access is not available, so no Stream Deck can be used. Please open an issue at " + StreamDeckSupport.IssuesUrl + " if this persists.");
		}
	}

	private void OnDeviceConnected(StreamDeckDevice device)
	{
		var bridge = _bridges.GetOrAdd(device.SerialNumber, serial => new DeviceBridge(serial, device.Model, _logger));
		bridge.AttachDevice(device);
		if (!device.IsHardwareVerified)
		{
			_logger.Information(
				"{Model:l} has not been tested on real hardware by the maintainers. If something does not work, please open an issue at "
				+ StreamDeckSupport.IssuesUrl + " and attach the diagnostics from the action 'Write Stream Deck diagnostics to the log'.",
				device.Model.Name);
		}

		_ = RegisterAsync(device, CancellationToken.None);
	}

	private void OnDeviceDisconnected(StreamDeckDevice device)
	{
		if (_bridges.TryGetValue(device.SerialNumber, out var bridge))
		{
			bridge.DetachDevice(device);
		}

		_ = ReportOfflineAsync(device);
	}

	private async Task RegisterAsync(StreamDeckDevice device, CancellationToken cancellationToken)
	{
		if (_devices is not { } context)
		{
			return;
		}

		try
		{
			await context.RegisterDeviceAsync(Describe(device), cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_logger.Warning("Could not register {Model:l} with Macro Deck: {Error:l}", device.Model.Name, ex.Message);
		}
	}

	private async Task ReportOfflineAsync(StreamDeckDevice device)
	{
		if (_devices is not { } context)
		{
			return;
		}

		try
		{
			await context.SetDevicePresenceAsync(device.SerialNumber, DevicePresence.Offline).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_logger.Debug("Could not report {Model:l} as offline: {Error:l}", device.Model.Name, ex.Message);
		}
	}

	private DeviceDescriptor Describe(StreamDeckDevice device)
	{
		var model = device.Model;
		_layoutIds.TryGetValue(StreamDeckLayouts.LocalId(model), out var layoutId);
		return new DeviceDescriptor(
			device.SerialNumber,
			model.Name,
			Model: model.Name,
			Manufacturer: model.VendorId == StreamDeckModels.CorsairVendorId ? "Corsair" : Manufacturer,
			LayoutReference: layoutId,
			Capabilities: new DeviceCapabilities
			{
				KeyCount = model.KeyCount,
				DisplayCount = model.HasKeyImages ? model.KeyCount : 0,
				SupportsImages = model.HasKeyImages,
				SupportsText = model.HasKeyImages,
			});
	}
}
