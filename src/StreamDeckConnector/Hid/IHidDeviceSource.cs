namespace StreamDeckConnector.Hid;

/// <param name="Path">Operating system path, stable while the device stays plugged into the same port.</param>
/// <param name="SerialNumber">Null when the device did not report one.</param>
/// <param name="ProductName">The product string, null when unreadable.</param>
/// <param name="InputReportLength">Maximum report lengths as the OS reports them, -1 when unknown.</param>
public sealed record HidDeviceInfo(
	string Path,
	int VendorId,
	int ProductId,
	string? SerialNumber,
	string? ProductName = null,
	int InputReportLength = -1,
	int OutputReportLength = -1,
	int FeatureReportLength = -1);

public interface IHidDeviceSource
{
	/// <summary>Raised on an arbitrary thread when a device appeared or disappeared.</summary>
	event EventHandler? Changed;

	IReadOnlyList<HidDeviceInfo> Enumerate();

	/// <summary>Throws <see cref="IOException"/> when the device is gone or cannot be opened.</summary>
	IHidConnection Open(HidDeviceInfo device);
}
