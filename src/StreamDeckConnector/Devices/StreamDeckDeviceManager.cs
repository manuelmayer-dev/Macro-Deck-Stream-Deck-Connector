using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;
using StreamDeckConnector.Hid;

namespace StreamDeckConnector.Devices;

/// <summary>
/// Keeps one <see cref="StreamDeckDevice"/> open per supported device the source reports. A device that
/// comes back, even with the same serial, is a new object. A device that failed on its own is dropped
/// and only reopened on the next change notification from the source. Devices from the Elgato vendor
/// that are not in the catalogue are never opened, only reported.
/// </summary>
public sealed class StreamDeckDeviceManager : IAsyncDisposable
{
	private readonly IHidDeviceSource _source;
	private readonly TimeProvider _timeProvider;
	private readonly ILogger _logger;
	private readonly StreamDeckDiagnosticsOptions _options;
	private readonly SemaphoreSlim _refreshGate = new(1, 1);
	private readonly object _stateLock = new();
	private readonly Dictionary<string, StreamDeckDevice> _devicesByPath = [];
	private readonly Dictionary<string, DeviceRecord> _records = [];
	private readonly Dictionary<string, UnsupportedDevice> _unsupportedByPath = [];
	private readonly HashSet<string> _openFailing = [];
	private bool _started;
	private bool _disposed;

	public StreamDeckDeviceManager(IHidDeviceSource source, TimeProvider timeProvider, ILogger logger, StreamDeckDiagnosticsOptions options)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(timeProvider);
		ArgumentNullException.ThrowIfNull(logger);
		ArgumentNullException.ThrowIfNull(options);
		_source = source;
		_timeProvider = timeProvider;
		_logger = logger.ForContext<StreamDeckDeviceManager>();
		_options = options;
	}

	public IReadOnlyList<StreamDeckDevice> Devices
	{
		get
		{
			lock (_stateLock)
			{
				return [.. _devicesByPath.Values];
			}
		}
	}

	/// <summary>Devices currently attached that this plugin does not support.</summary>
	public IReadOnlyList<UnsupportedDevice> UnsupportedDevices
	{
		get
		{
			lock (_stateLock)
			{
				return [.. _unsupportedByPath.Values];
			}
		}
	}

	public event Action<StreamDeckDevice>? DeviceConnected;

	public event Action<StreamDeckDevice>? DeviceDisconnected;

	public event Action<UnsupportedDevice>? UnsupportedDeviceAttached;

	public event Action<UnsupportedDevice>? UnsupportedDeviceDetached;

	/// <summary>Starts watching the source and opens what is already plugged in.</summary>
	public Task StartAsync()
	{
		lock (_stateLock)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_started)
			{
				return Task.CompletedTask;
			}

			_started = true;
		}

		_source.Changed += OnSourceChanged;
		return RefreshAsync();
	}

	/// <summary>Reconciles the open devices with what the source enumerates now.</summary>
	public async Task RefreshAsync()
	{
		await _refreshGate.WaitAsync().ConfigureAwait(false);
		try
		{
			if (_disposed)
			{
				return;
			}

			IReadOnlyList<HidDeviceInfo> all;
			try
			{
				all = _source.Enumerate();
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return;
			}

			var present = all.Where(IsSupported).ToList();
			var unsupported = all.Where(IsUnsupportedStreamDeck).ToList();
			var paths = all.Select(info => info.Path).ToHashSet(StringComparer.Ordinal);

			lock (_stateLock)
			{
				_openFailing.IntersectWith(paths);
			}

			await CloseMissingAsync(present).ConfigureAwait(false);
			UpdateUnsupported(unsupported);
			OpenNew(present);
		}
		finally
		{
			_refreshGate.Release();
		}
	}

	/// <summary>A plain text snapshot a user can paste into an issue.</summary>
	public string DescribeDiagnostics()
	{
		var text = new StringBuilder();
		text.AppendLine("Stream Deck diagnostics");
		text.AppendLine(CultureInfo.InvariantCulture, $"Please attach this snapshot when you open an issue at {StreamDeckSupport.IssuesUrl}");
		text.AppendLine(CultureInfo.InvariantCulture, $"Plugin version: {PluginVersion()}");
		text.AppendLine(CultureInfo.InvariantCulture, $"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
		text.AppendLine(CultureInfo.InvariantCulture, $"Runtime: {RuntimeInformation.FrameworkDescription}");
		text.AppendLine(CultureInfo.InvariantCulture, $"Raw trace: {(_options.TraceRawReports ? "on" : "off")}");

		List<DeviceRecord> records;
		List<UnsupportedDevice> unsupported;
		lock (_stateLock)
		{
			records = [.. _records.Values];
			unsupported = [.. _unsupportedByPath.Values];
		}

		text.AppendLine();
		text.AppendLine(CultureInfo.InvariantCulture, $"Supported devices seen: {records.Count}");
		foreach (var record in records)
		{
			var info = record.Info;
			var counters = record.Counters.Snapshot();
			text.AppendLine(CultureInfo.InvariantCulture, $"- {record.Model.Name} | vendor 0x{info.VendorId:X4} product 0x{info.ProductId:X4} | serial ending {record.SerialTail} | {(record.Connected ? "connected" : "disconnected")} | firmware {record.Firmware}");
			text.AppendLine(CultureInfo.InvariantCulture, $"  hardware verified: {(record.Model.VerifiedOnHardware ? "yes" : "no")} | HID report lengths input {info.InputReportLength} output {info.OutputReportLength} feature {info.FeatureReportLength}");
			text.AppendLine(CultureInfo.InvariantCulture, $"  reports read {counters.ReportsRead}, key events {counters.KeyEvents}, images written {counters.ImagesWritten}, images skipped as identical {counters.ImagesSkipped}, write failures {counters.WriteFailures}, reconnects {record.Reconnects}");
			text.AppendLine(CultureInfo.InvariantCulture, $"  last error: {record.LastError ?? counters.LastError ?? "none"}");
		}

		text.AppendLine();
		text.AppendLine(CultureInfo.InvariantCulture, $"Unsupported devices attached: {unsupported.Count}");
		foreach (var device in unsupported)
		{
			text.AppendLine(CultureInfo.InvariantCulture, $"- vendor 0x{device.VendorId:X4} product 0x{device.ProductId:X4} | product string '{device.ProductName}' | HID report lengths input {device.InputReportLength} output {device.OutputReportLength} feature {device.FeatureReportLength}");
		}

		return text.ToString();
	}

	public async ValueTask DisposeAsync()
	{
		await _refreshGate.WaitAsync().ConfigureAwait(false);
		List<StreamDeckDevice> devices;
		try
		{
			if (_disposed)
			{
				return;
			}

			lock (_stateLock)
			{
				_disposed = true;
				devices = [.. _devicesByPath.Values];
				_devicesByPath.Clear();
			}
		}
		finally
		{
			_refreshGate.Release();
		}

		_source.Changed -= OnSourceChanged;
		foreach (var device in devices)
		{
			await device.DisposeAsync().ConfigureAwait(false);
		}
	}

	private static string PluginVersion() =>
		typeof(StreamDeckDeviceManager).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
		?? typeof(StreamDeckDeviceManager).Assembly.GetName().Version?.ToString()
		?? "unknown";

	private static bool IsSupported(HidDeviceInfo info) => StreamDeckModels.TryGet(info.VendorId, info.ProductId, out _);

	private static bool IsUnsupportedStreamDeck(HidDeviceInfo info) => info.VendorId == StreamDeckModels.VendorId && !IsSupported(info);

	private void OnSourceChanged(object? sender, EventArgs e) => _ = RefreshAsync();

	private async Task CloseMissingAsync(IReadOnlyList<HidDeviceInfo> present)
	{
		var paths = present.Select(info => info.Path).ToHashSet(StringComparer.Ordinal);
		List<StreamDeckDevice> missing;
		lock (_stateLock)
		{
			var gone = _devicesByPath.Where(pair => !paths.Contains(pair.Key)).ToList();
			missing = [.. gone.Select(pair => pair.Value)];
			foreach (var pair in gone)
			{
				_devicesByPath.Remove(pair.Key);
			}

			foreach (var device in missing)
			{
				MarkDisconnected(device);
			}
		}

		foreach (var device in missing)
		{
			Raise(DeviceDisconnected, device);
			await device.DisposeAsync().ConfigureAwait(false);
		}
	}

	private void UpdateUnsupported(IReadOnlyList<HidDeviceInfo> unsupported)
	{
		var paths = unsupported.Select(info => info.Path).ToHashSet(StringComparer.Ordinal);
		List<UnsupportedDevice> detached;
		List<UnsupportedDevice> attached = [];
		lock (_stateLock)
		{
			var gone = _unsupportedByPath.Where(pair => !paths.Contains(pair.Key)).ToList();
			detached = [.. gone.Select(pair => pair.Value)];
			foreach (var pair in gone)
			{
				_unsupportedByPath.Remove(pair.Key);
			}

			foreach (var info in unsupported.Where(info => !_unsupportedByPath.ContainsKey(info.Path)))
			{
				var device = new UnsupportedDevice(UnsupportedDevice.KeyFor(info.Path), info.VendorId, info.ProductId, info.ProductName, info.InputReportLength, info.OutputReportLength, info.FeatureReportLength);
				_unsupportedByPath[info.Path] = device;
				attached.Add(device);
			}
		}

		foreach (var device in detached)
		{
			Raise(UnsupportedDeviceDetached, device);
		}

		foreach (var device in attached)
		{
			_logger.Warning(
				"Unsupported Stream Deck device attached: vendor 0x{VendorId:X4}, product 0x{ProductId:X4}, product string '{ProductName:l}', "
				+ "HID report lengths input {InputLength} output {OutputLength} feature {FeatureLength}. It is not supported and will not be opened. "
				+ "Please open an issue at " + StreamDeckSupport.IssuesUrl + " and attach the diagnostics snapshot.",
				device.VendorId,
				device.ProductId,
				device.ProductName,
				device.InputReportLength,
				device.OutputReportLength,
				device.FeatureReportLength);
			Raise(UnsupportedDeviceAttached, device);
		}
	}

	private void OpenNew(IReadOnlyList<HidDeviceInfo> present)
	{
		foreach (var info in present)
		{
			lock (_stateLock)
			{
				if (_devicesByPath.ContainsKey(info.Path))
				{
					continue;
				}
			}

			StreamDeckModels.TryGet(info.VendorId, info.ProductId, out var model);
			var serial = string.IsNullOrEmpty(info.SerialNumber) ? info.Path : info.SerialNumber;
			DeviceRecord record;
			lock (_stateLock)
			{
				record = GetRecord(serial, model, info);
			}

			StreamDeckDevice device;
			try
			{
				var connection = _source.Open(info);
				device = new StreamDeckDevice(model, serial, connection, _timeProvider, _logger, _options, record.Counters);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Another process may hold it, or it vanished again. The next change notification retries.
				bool firstOfEpisode;
				lock (_stateLock)
				{
					firstOfEpisode = _openFailing.Add(info.Path);
					record.LastError = $"open failure: {ex.Message}";
				}

				if (firstOfEpisode)
				{
					_logger.Warning("Could not open {Model:l} (serial ending {SerialTail:l}): {Error:l}", model.Name, record.SerialTail, ex.Message);
				}

				continue;
			}

			lock (_stateLock)
			{
				_openFailing.Remove(info.Path);
				_devicesByPath[info.Path] = device;
				record.Info = info;
				if (record.EverConnected)
				{
					record.Reconnects++;
				}

				record.EverConnected = true;
				record.Connected = true;
			}

			device.Disconnected += failure => OnDeviceFailed(device, failure);
			Raise(DeviceConnected, device);
			_ = LogConnectedAsync(device, record);
		}
	}

	private async Task LogConnectedAsync(StreamDeckDevice device, DeviceRecord record)
	{
		var firmware = "unknown";
		try
		{
			firmware = await device.GetFirmwareVersionAsync().ConfigureAwait(false);
		}
		catch (Exception)
		{
			// The device may have ended already, or the firmware report is not readable on this model.
		}

		record.Firmware = firmware;
		var info = record.Info;
		_logger.Information(
			"Stream Deck connected: {Model:l} (vendor 0x{VendorId:X4}, product 0x{ProductId:X4}), serial ending {SerialTail:l}, firmware {Firmware:l}, "
			+ "verified on hardware {Verified}, HID report lengths input {InputLength} output {OutputLength} feature {FeatureLength}",
			device.Model.Name,
			info.VendorId,
			info.ProductId,
			record.SerialTail,
			firmware,
			device.IsHardwareVerified,
			info.InputReportLength,
			info.OutputReportLength,
			info.FeatureReportLength);
	}

	private DeviceRecord GetRecord(string serial, StreamDeckModel model, HidDeviceInfo info)
	{
		if (!_records.TryGetValue(serial, out var record))
		{
			record = new DeviceRecord(model, info, SerialMask.Tail(string.IsNullOrEmpty(info.SerialNumber) ? null : info.SerialNumber));
			_records[serial] = record;
		}

		return record;
	}

	private void MarkDisconnected(StreamDeckDevice device)
	{
		if (_records.TryGetValue(device.SerialNumber, out var record))
		{
			record.Connected = false;
		}
	}

	private void OnDeviceFailed(StreamDeckDevice device, Exception failure)
	{
		lock (_stateLock)
		{
			var entry = _devicesByPath.FirstOrDefault(pair => pair.Value == device);
			if (entry.Value is null)
			{
				return;
			}

			_devicesByPath.Remove(entry.Key);
			MarkDisconnected(device);
			if (_records.TryGetValue(device.SerialNumber, out var record))
			{
				record.LastError = failure.Message;
			}
		}

		Raise(DeviceDisconnected, device);
		// Disposal waits for the device threads and this runs on one of them.
		_ = Task.Run(() => device.DisposeAsync().AsTask());
	}

	private static void Raise<T>(Action<T>? handler, T value)
	{
		try
		{
			handler?.Invoke(value);
		}
		catch (Exception)
		{
			// A throwing subscriber must not break the device bookkeeping.
		}
	}

	private sealed class DeviceRecord(StreamDeckModel model, HidDeviceInfo info, string serialTail)
	{
		public StreamDeckModel Model { get; } = model;

		public HidDeviceInfo Info { get; set; } = info;

		public string SerialTail { get; } = serialTail;

		public DeviceCounters Counters { get; } = new();

		public bool Connected { get; set; }

		public bool EverConnected { get; set; }

		public int Reconnects { get; set; }

		public string Firmware { get; set; } = "unknown";

		public string? LastError { get; set; }
	}
}
