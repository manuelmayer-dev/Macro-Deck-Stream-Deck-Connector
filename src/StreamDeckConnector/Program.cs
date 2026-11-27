using MacroDeck.Plugin.Hosting;
using MacroDeck.Plugin.Serilog;
using StreamDeckConnector;

// Identity, description and icon are not set here: they come from manifest.json at the content root.
var builder = MacroDeckPlugin.CreatePlugin(args);
builder.Services.AddStreamDeckTransport();

var plugin = builder
	.UseMacroDeckLogging()
	.UseLocalization(Strings.LocalizationCatalog)
	.RegisterIntegration<PluginIntegration>()
	.Build();

await plugin.RunAsync();
