using System.Collections.Frozen;

namespace StreamDeckConnector.Devices;

public static class StreamDeckModels
{
	public const int VendorId = 0x0FD9;
	public const int CorsairVendorId = 0x1B1C;

	// Rates are the ones StreamDeckSharp found glitch free (brief, part D). Models without a measured
	// figure use the lower one.
	private const int SafeRate = 1_500_000;
	private const int FastRate = 3_200_000;

	private static readonly FrozenDictionary<long, StreamDeckModel> _byId = new[]
	{
		Gen2("Stream Deck Original (v2)", 0x006D, 3, 5, 72, 180, FastRate),
		Gen2("Stream Deck MK.2", 0x0080, 3, 5, 72, 180, SafeRate, verified: true),
		Gen2("Stream Deck MK.2 (Scissor)", 0x00A5, 3, 5, 72, 180, SafeRate),
		// The brief notes python names 0x00B9 MK2_MODULE and MK2_V2 in different places. Elgato, node and rust call it the 15-key module.
		Gen2("Stream Deck 15-Key Module", 0x00B9, 3, 5, 72, 180, SafeRate),
		Gen2("Stream Deck 15-Key Module (Scissor)", 0x00E4, 3, 5, 72, 180, SafeRate),
		Gen2("Stream Deck XL", 0x006C, 4, 8, 96, 180, SafeRate),
		Gen2("Stream Deck XL (2022)", 0x008F, 4, 8, 96, 180, SafeRate),
		Gen2("Stream Deck 32-Key Module", 0x00BA, 4, 8, 96, 180, SafeRate),
		// Plus and Plus XL: keys only. Plus XL key size is 112 per Elgato, python and node (rust says 120).
		Gen2("Stream Deck +", 0x0084, 2, 4, 120, 0, SafeRate),
		Gen2("Stream Deck + XL", 0x00C6, 4, 9, 112, 270, SafeRate),
		// Neo: the 8 LCD keys only. The two touch keys (indices 8 and 9) and the info screen are ignored.
		Gen2("Stream Deck Neo", 0x009A, 2, 4, 96, 180, SafeRate),
		new StreamDeckModel("Stream Deck Pedal", VendorId, 0x0086, 1, 3, 0, StreamDeckImageFormat.None, 0, false, StreamDeckProtocolFamily.Gen2, 0),
		// Original v1 image orientation is untested on hardware: python flips both axes, node only X (brief, part A).
		new StreamDeckModel("Stream Deck Original (v1)", VendorId, 0x0060, 3, 5, 72, StreamDeckImageFormat.Bmp, 0, true, StreamDeckProtocolFamily.OriginalV1, FastRate),
		Mini("Stream Deck Mini", 0x0063),
		Mini("Stream Deck Mini (2022)", 0x0090),
		Mini("Stream Deck Mini Discord Edition", 0x00B3),
		Mini("Stream Deck 6-Key Module", 0x00B8),
		// Galleon K100 SD, from the node library only: a 3 column by 4 row grid, gen2 key images and
		// input (key states at the same offset as gen2), no flip or rotation, encoders and the LCD
		// segment ignored. Node pings it with feature report 03 27 every 500 ms, so a keep-alive is sent.
		// Open point: node also filters on HID usage 0x01 and interface 0, which this transport does not.
		new StreamDeckModel("Corsair Galleon K100 SD", CorsairVendorId, 0x2B18, 4, 3, 160, StreamDeckImageFormat.Jpeg, 0, false, StreamDeckProtocolFamily.Gen2, SafeRate, SendsKeepAlive: true),
	}.ToFrozenDictionary(m => Key(m.VendorId, m.ProductId));

	public static IReadOnlyCollection<StreamDeckModel> All => _byId.Values;

	public static bool TryGet(int vendorId, int productId, out StreamDeckModel model)
	{
		if (_byId.TryGetValue(Key(vendorId, productId), out var found))
		{
			model = found;
			return true;
		}

		model = null!;
		return false;
	}

	private static long Key(int vendorId, int productId) => ((long)vendorId << 16) | (uint)productId;

	private static StreamDeckModel Gen2(string name, int productId, int rows, int columns, int keyPixelSize, int rotation, int rate, bool verified = false) =>
		new(name, VendorId, productId, rows, columns, keyPixelSize, StreamDeckImageFormat.Jpeg, rotation, false, StreamDeckProtocolFamily.Gen2, rate, verified);

	private static StreamDeckModel Mini(string name, int productId) =>
		new(name, VendorId, productId, 2, 3, 80, StreamDeckImageFormat.Bmp, 90, false, StreamDeckProtocolFamily.Mini, SafeRate);
}
