using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;
using StreamDeckConnector.Devices;
using StreamDeckConnector.Hid;

namespace StreamDeckConnector;

public static class ServiceCollectionExtensions
{
	/// <summary>Registers the USB transport. A test replaces <see cref="IHidDeviceSource"/> before building.</summary>
	public static IServiceCollection AddStreamDeckTransport(this IServiceCollection services)
	{
		services.TryAddSingleton(TimeProvider.System);
		services.TryAddSingleton<IHidDeviceSource>(_ => new HidSharpDeviceSource([StreamDeckModels.VendorId, StreamDeckModels.CorsairVendorId]));
		services.TryAddSingleton<StreamDeckDiagnosticsOptions>();
		services.TryAddSingleton(provider => new StreamDeckDeviceManager(
			provider.GetRequiredService<IHidDeviceSource>(),
			provider.GetRequiredService<TimeProvider>(),
			provider.GetRequiredService<ILogger>(),
			provider.GetRequiredService<StreamDeckDiagnosticsOptions>()));
		return services;
	}
}
