using HidSharp;

namespace StreamDeckConnector.Hid;

/// <summary>The only place HidSharp types appear.</summary>
public sealed class HidSharpDeviceSource : IHidDeviceSource, IDisposable
{
	private readonly HashSet<int> _vendorIds;
	private readonly object _gate = new();
	private EventHandler? _changed;
	private bool _subscribed;
	private bool _disposed;

	public HidSharpDeviceSource(IEnumerable<int> vendorIds)
	{
		ArgumentNullException.ThrowIfNull(vendorIds);
		_vendorIds = [.. vendorIds];
	}

	// Subscribed lazily so that constructing the source starts no native watcher.
	public event EventHandler? Changed
	{
		add
		{
			lock (_gate)
			{
				ObjectDisposedException.ThrowIf(_disposed, this);
				_changed += value;
				if (!_subscribed)
				{
					DeviceList.Local.Changed += OnDeviceListChanged;
					_subscribed = true;
				}
			}
		}
		remove
		{
			lock (_gate)
			{
				_changed -= value;
			}
		}
	}

	public IReadOnlyList<HidDeviceInfo> Enumerate() =>
		[.. DeviceList.Local.GetHidDevices().Where(d => _vendorIds.Contains(d.VendorID)).Select(ToInfo)];

	public IHidConnection Open(HidDeviceInfo device)
	{
		ArgumentNullException.ThrowIfNull(device);
		var hidDevice = DeviceList.Local.GetHidDevices(device.VendorId, device.ProductId, null, null)
			.FirstOrDefault(d => d.DevicePath == device.Path)
			?? throw new IOException("The HID device is no longer present.");

		var stream = hidDevice.Open();
		try
		{
			// The default read timeout is 3 s and would end an idle reader.
			stream.ReadTimeout = Timeout.Infinite;
			return new HidSharpConnection(stream, hidDevice.GetMaxInputReportLength(), Safe(hidDevice.GetMaxFeatureReportLength, 0));
		}
		catch
		{
			stream.Dispose();
			throw;
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			_changed = null;
			if (_subscribed)
			{
				DeviceList.Local.Changed -= OnDeviceListChanged;
				_subscribed = false;
			}
		}
	}

	private void OnDeviceListChanged(object? sender, DeviceListChangedEventArgs e)
	{
		EventHandler? handler;
		lock (_gate)
		{
			handler = _changed;
		}

		handler?.Invoke(this, EventArgs.Empty);
	}

	private static HidDeviceInfo ToInfo(HidDevice device)
	{
		string? serial;
		try
		{
			serial = device.GetSerialNumber();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			serial = null;
		}

		return new HidDeviceInfo(
			device.DevicePath,
			device.VendorID,
			device.ProductID,
			serial,
			Safe(device.GetProductName, null),
			Safe(device.GetMaxInputReportLength, -1),
			Safe(device.GetMaxOutputReportLength, -1),
			Safe(device.GetMaxFeatureReportLength, -1));
	}

	// Diagnostics only: an unreadable string or descriptor must never hide the device itself.
	private static T Safe<T>(Func<T> read, T fallback)
	{
		try
		{
			return read();
		}
		catch (Exception)
		{
			return fallback;
		}
	}

	private sealed class HidSharpConnection : IHidConnection
	{
		private readonly HidStream _stream;
		private readonly byte[] _readBuffer;
		private readonly int _featureReportLength;
		private byte[] _writeBuffer = [];
		private byte[] _featureBuffer = [];

		public HidSharpConnection(HidStream stream, int maxInputReportLength, int maxFeatureReportLength)
		{
			_stream = stream;
			_readBuffer = new byte[Math.Max(maxInputReportLength, 1)];
			_featureReportLength = Math.Max(maxFeatureReportLength, 0);
		}

		public int ReadInputReport(Span<byte> destination, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			int read;
			try
			{
				read = _stream.Read(_readBuffer);
			}
			catch (Exception) when (cancellationToken.IsCancellationRequested)
			{
				// Cancellation is carried out by disposing the stream, which surfaces as an I/O error here.
				throw new OperationCanceledException(cancellationToken);
			}

			var length = Math.Min(read, destination.Length);
			_readBuffer.AsSpan(0, length).CopyTo(destination);
			return length;
		}

		public void WriteOutputReport(ReadOnlySpan<byte> report)
		{
			// HidSharp pads output reports to the device length on Windows itself.
			if (_writeBuffer.Length != report.Length)
			{
				_writeBuffer = new byte[report.Length];
			}

			report.CopyTo(_writeBuffer);
			_stream.Write(_writeBuffer, 0, report.Length);
		}

		public void SetFeatureReport(ReadOnlySpan<byte> report)
		{
			var length = PrepareFeatureBuffer(report);
			_stream.SetFeature(_featureBuffer, 0, length);
		}

		public void GetFeatureReport(Span<byte> buffer)
		{
			var length = PrepareFeatureBuffer(buffer);
			_stream.GetFeature(_featureBuffer, 0, length);
			_featureBuffer.AsSpan(0, buffer.Length).CopyTo(buffer);
		}

		public void Dispose() => _stream.Dispose();

		// Windows rejects a feature report shorter than the device's feature report length, and HidSharp
		// passes the length to HidD_SetFeature and HidD_GetFeature unchanged, so the report is zero padded here.
		private int PrepareFeatureBuffer(ReadOnlySpan<byte> report)
		{
			var length = Math.Max(report.Length, _featureReportLength);
			if (_featureBuffer.Length != length)
			{
				_featureBuffer = new byte[length];
			}

			report.CopyTo(_featureBuffer);
			_featureBuffer.AsSpan(report.Length).Clear();
			return length;
		}
	}
}
