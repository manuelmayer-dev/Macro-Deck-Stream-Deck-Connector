using System.Globalization;
using MacroDeck.Sdk.Layouts;
using StreamDeckConnector.Devices;

namespace StreamDeckConnector;

/// <summary>One fixed layout per catalogue model, so a profile claimed by a device matches its keys.</summary>
public static class StreamDeckLayouts
{
	public static IReadOnlyList<LayoutDescriptor> All { get; } =
		[.. StreamDeckModels.All.OrderBy(model => model.Name, StringComparer.Ordinal).Select(For)];

	public static string LocalId(StreamDeckModel model) =>
		string.Create(CultureInfo.InvariantCulture, $"deck-{model.VendorId:x4}-{model.ProductId:x4}");

	public static LayoutKeyImageFormat? KeyImageFormat(StreamDeckModel model) => model.ImageFormat switch
	{
		StreamDeckImageFormat.Jpeg => Format(KeyImageEncodings.Jpeg, model),
		StreamDeckImageFormat.Bmp => Format(KeyImageEncodings.Bmp, model),
		_ => null,
	};

	public static LayoutDescriptor For(StreamDeckModel model)
	{
		var grid = new LayoutGrid
		{
			Rows = model.Rows,
			Columns = model.Columns,
			KeySize = model.HasKeyImages ? new LayoutKeySize(model.KeyPixelSize, model.KeyPixelSize) : null,
			KeyImages = KeyImageFormat(model),
			// Every key is its own display or switch, so a widget cannot span the gap between two.
			MaxWidgetColumns = 1,
			MaxWidgetRows = 1,
		};

		return new LayoutDescriptor(
			LocalId(model),
			model.Name,
			Regions: [new LayoutRegion { Id = "keys", Kind = LayoutRegionKinds.Grid, Grid = grid }],
			Capabilities: new LayoutCapabilities { Visuals = Visuals(model) });
	}

	private static LayoutKeyImageFormat Format(string encoding, StreamDeckModel model) =>
		new() { Encoding = encoding, Rotation = model.RotationDegrees, MirrorHorizontal = model.MirrorHorizontal };

	private static LayoutVisualCapabilities Visuals(StreamDeckModel model) => new()
	{
		StaticIcons = model.HasKeyImages,
		AnimatedIcons = model.HasKeyImages,
		Borders = model.HasKeyImages,
		BackgroundColors = model.HasKeyImages,
		TextLabels = model.HasKeyImages,
		Transparency = model.HasKeyImages,
		WidgetSpacing = false,
		// The profile's corner radius rounds each key picture and its border.
		CornerRadius = model.HasKeyImages,
		CustomFolderViews = false,
		ScreenSavers = false,
	};
}
