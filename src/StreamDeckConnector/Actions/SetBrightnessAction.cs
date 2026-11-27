using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using StreamDeckConnector.Devices;

namespace StreamDeckConnector.Actions;

public sealed class SetBrightnessAction : IActionDefinition
{
	private const string BrightnessParameter = "brightness";
	private const double DefaultBrightness = 70;

	private readonly StreamDeckDeviceManager _manager;

	public SetBrightnessAction(StreamDeckDeviceManager manager) => _manager = manager;

	public string Id => "set-brightness";

	public LocalizedText Name => Strings.Actions.SetBrightness.Name();

	public LocalizedText Description => Strings.Actions.SetBrightness.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		ActionParameter.Slider(
			BrightnessParameter,
			min: 0,
			max: 100,
			label: Strings.Actions.SetBrightness.Brightness.Label(),
			description: Strings.Actions.SetBrightness.Brightness.Description(),
			step: 1,
			defaultValue: DefaultBrightness),
	];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.All;

	public IActionExecutor CreateExecutor() => new Executor(_manager);

	private sealed class Executor : IActionExecutor
	{
		private readonly StreamDeckDeviceManager _manager;

		public Executor(StreamDeckDeviceManager manager) => _manager = manager;

		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			if (!TryReadPercent(context, out var percent))
			{
				return ActionResult.Failed(
					ActionErrorCodes.InvalidParameter,
					Strings.Actions.SetBrightness.InvalidBrightness());
			}

			int applied;
			try
			{
				applied = await _manager.SetBrightnessAsync(percent, context.CancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
			{
				return ActionResult.Failed(ActionErrorCodes.ProviderError, Strings.Actions.SetBrightness.Failed());
			}

			if (applied == 0)
			{
				return ActionResult.Failed(ActionErrorCodes.NotConnected, Strings.Actions.SetBrightness.NoDevice());
			}

			return ActionResult.Success();
		}

		private static bool TryReadPercent(ActionExecutionContext context, out int percent)
		{
			percent = (int)DefaultBrightness;
			if (!context.Parameters.TryGetValue(BrightnessParameter, out var raw) || raw is null)
			{
				return true;
			}

			var text = Convert.ToString(raw, CultureInfo.InvariantCulture);
			if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value is < 0 or > 100)
			{
				return false;
			}

			percent = (int)Math.Round(value);
			return true;
		}
	}
}
