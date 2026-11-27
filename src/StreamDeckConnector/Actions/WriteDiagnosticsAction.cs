using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using StreamDeckConnector.Devices;

namespace StreamDeckConnector.Actions;

/// <summary>Lets a user of untested hardware collect what an issue report needs without any tooling.</summary>
public sealed class WriteDiagnosticsAction : IActionDefinition
{
	private const string TraceParameter = "trace-raw-reports";

	private readonly PluginIntegration _integration;
	private readonly StreamDeckDiagnosticsOptions _options;

	public WriteDiagnosticsAction(PluginIntegration integration, StreamDeckDiagnosticsOptions options)
	{
		_integration = integration;
		_options = options;
	}

	public string Id => "write-diagnostics";

	public LocalizedText Name => Strings.Actions.WriteDiagnostics.Name();

	public LocalizedText Description => Strings.Actions.WriteDiagnostics.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		ActionParameter.Toggle(
			TraceParameter,
			label: Strings.Actions.WriteDiagnostics.Trace.Label(),
			description: Strings.Actions.WriteDiagnostics.Trace.Description(),
			literalOnly: true),
	];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.All;

	public IActionExecutor CreateExecutor() => new Executor(_integration, _options);

	private sealed class Executor : IActionExecutor
	{
		private readonly PluginIntegration _integration;
		private readonly StreamDeckDiagnosticsOptions _options;

		public Executor(PluginIntegration integration, StreamDeckDiagnosticsOptions options)
		{
			_integration = integration;
			_options = options;
		}

		public Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			var trace = context.Parameters.TryGetValue(TraceParameter, out var raw)
				&& bool.TryParse(Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture), out var parsed)
				&& parsed;

			_options.TraceRawReports = trace;
			_integration.WriteDiagnosticsToLog();
			return ActionResult.SucceededTask;
		}
	}
}
