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
/// and only reopened on the next change notification from the source. A device that could not be
/// opened is retried on a timer while it stays attached. Elgato devices that are not in the catalogue
/// are never opened, and only reported when they look like a Stream Deck.
/// </summary>
public sealed class StreamDeckDeviceManager : IAsyncDisposable
{
	private static readonly TimeSpan _openRetryInterval = TimeSpan.FromSeconds(5);

	// Elgato Stream Decks whose product string might not say so, and that this plugin cannot drive.
	private static readonly HashSet<int> _knownUnsupportedProductIds = [0x00AA];

	private readonly IHidDeviceSource _source;
	private readonly TimeProvider _timeProvider;
	private readonly ILogger _logger;
	private readonly StreamDeckDiagnosticsOptions _options;
	private readonly SemaphoreSlim _refreshGate = new(1, 1);
	private readonly object _stateLock = new();
	private readonly Dictionary<string, StreamDeckDevice> _devicesByPath = [];
	private readonly Dictionary<string, DeviceRecord> _records = [];
	private readonly Dictionary<string, UnsupportedDevice> _unsupportedByPath = [];
	private readonly Dictionary<string, OpenFailure> _openFailing = [];
	private readonly HashSet<string> _ignoredPaths = [];
	private ITimer? _retryTimer;
	private object? _retryTimerToken;
	private int? _brightness;
	private bool _enumerationFailing;
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

	/// <summary>Supported devices that are attached but could not be opened.</summary>
	public IReadOnlyList<OpenFailure> OpenFailures
	{
		get
		{
			lock (_stateLock)
			{
				return [.. _openFailing.Values];
			}
		}
	}

	public event Action<StreamDeckDevice>? DeviceConnected;

	public event Action<StreamDeckDevice>? DeviceDisconnected;

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

			if (!TryEnumerate(out var all))
			{
				return;
			}

			List<HidDeviceInfo> present = [];
			List<HidDeviceInfo> unsupported = [];
			List<(HidDeviceInfo Info, string Reason)> ignored = [];
			foreach (var info in all)
			{
				if (StreamDeckModels.TryGet(info.VendorId, info.ProductId, out var model))
				{
					if (FitsProtocol(info, model))
					{
						present.Add(info);
					}
					else
					{
						ignored.Add((info, $"another HID interface of {model.Name}, its report lengths do not fit the protocol"));
					}
				}
				else if (info.VendorId == StreamDeckModels.VendorId)
				{
					if (LooksLikeStreamDeck(info))
					{
						unsupported.Add(info);
					}
					else
					{
						ignored.Add((info, "an Elgato device that is not a Stream Deck"));
					}
				}
			}

			var paths = all.Select(info => info.Path).ToHashSet(StringComparer.Ordinal);
			lock (_stateLock)
			{
				foreach (var gone in _openFailing.Keys.Where(path => !paths.Contains(path)).ToList())
				{
					_openFailing.Remove(gone);
				}
			}

			await CloseMissingAsync(present).ConfigureAwait(false);
			UpdateUnsupported(unsupported);
			UpdateIgnored(ignored);
			OpenNew(present);
			UpdateRetryTimer();
		}
		finally
		{
			_refreshGate.Release();
		}
	}

	/// <summary>
	/// Sets the brightness of every connected device with key displays, and remembers it for every device
	/// that connects later. Returns how many devices took it, 0 when none with displays is connected (the
	/// value is still remembered). Every device is tried, then the first failure is thrown: the HID error of
	/// a device that refused the report, <see cref="InvalidOperationException"/> for one that ended
	/// meanwhile, <see cref="OperationCanceledException"/> when cancelled.
	/// </summary>
	public async Task<int> SetBrightnessAsync(int percent, CancellationToken cancellationToken = default)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(percent);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
		List<StreamDeckDevice> targets;
		lock (_stateLock)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			// Under the same lock as registering a device, so a device connecting now either is a target or sees the new value.
			_brightness = percent;
			targets = [.. _devicesByPath.Values.Where(device => device.Model.HasKeyImages)];
		}

		await Task.WhenAll(targets.Select(device => device.SetBrightnessAsync(percent, cancellationToken))).ConfigureAwait(false);
		return targets.Count;
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
				StopRetryTimer();
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

	// A device with several HID collections (the Galleon keyboard, or a second collection of any model)
	// shows up once per collection. Only the one whose reports fit the protocol is the deck. Unknown
	// lengths (-1) do not rule a device out.
	private static bool FitsProtocol(HidDeviceInfo info, StreamDeckModel model)
	{
		var protocol = StreamDeckProtocols.Create(model);
		if (model.HasKeyImages && info.OutputReportLength >= 0 && info.OutputReportLength < protocol.OutputReportLength)
		{
			return false;
		}

		return info.InputReportLength < 0 || info.InputReportLength >= protocol.MinimumInputReportLength;
	}

	private static bool LooksLikeStreamDeck(HidDeviceInfo info) =>
		_knownUnsupportedProductIds.Contains(info.ProductId)
		|| (info.ProductName?.Contains("Stream Deck", StringComparison.OrdinalIgnoreCase) ?? false);

	private static string FallbackIdentity(StreamDeckModel model, HidDeviceInfo info) =>
		$"{model.VendorId:x4}{model.ProductId:x4}-port-{UnsupportedDevice.KeyFor(info.Path).ToLowerInvariant()}";

	private void OnSourceChanged(object? sender, EventArgs e) => _ = RefreshObservedAsync();

	// For refreshes nobody awaits: a failure is logged instead of vanishing with the task.
	private async Task RefreshObservedAsync()
	{
		try
		{
			await RefreshAsync().ConfigureAwait(false);
		}
		catch (Exception ex) when (!Failures.IsFatal(ex))
		{
			_logger.Error(ex, "Refreshing the Stream Deck list failed: {Error:l}. Please open an issue at " + StreamDeckSupport.IssuesUrl + " and attach the diagnostics snapshot.", ex.Message);
		}
	}

	private bool TryEnumerate(out IReadOnlyList<HidDeviceInfo> all)
	{
		try
		{
			all = _source.Enumerate();
		}
		catch (Exception ex) when (!Failures.IsFatal(ex))
		{
			all = [];
			if (!_enumerationFailing)
			{
				_enumerationFailing = true;
				_logger.Warning(
					"Could not list the attached HID devices: {Error:l}. Stream Decks are not updated until this works again. If it persists, please open an issue at "
					+ StreamDeckSupport.IssuesUrl + " and attach the diagnostics snapshot.",
					ex.Message);
			}

			return false;
		}

		if (_enumerationFailing)
		{
			_enumerationFailing = false;
			_logger.Information("Listing the attached HID devices works again");
		}

		return true;
	}

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
		List<UnsupportedDevice> attached = [];
		lock (_stateLock)
		{
			foreach (var gone in _unsupportedByPath.Keys.Where(path => !paths.Contains(path)).ToList())
			{
				_unsupportedByPath.Remove(gone);
			}

			foreach (var info in unsupported.Where(info => !_unsupportedByPath.ContainsKey(info.Path)))
			{
				var device = new UnsupportedDevice(UnsupportedDevice.KeyFor(info.Path), info.VendorId, info.ProductId, info.ProductName, info.InputReportLength, info.OutputReportLength, info.FeatureReportLength);
				_unsupportedByPath[info.Path] = device;
				attached.Add(device);
			}
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
		}
	}

	private void UpdateIgnored(IReadOnlyList<(HidDeviceInfo Info, string Reason)> ignored)
	{
		var paths = ignored.Select(entry => entry.Info.Path).ToHashSet(StringComparer.Ordinal);
		_ignoredPaths.RemoveWhere(path => !paths.Contains(path));
		foreach (var (info, reason) in ignored.Where(entry => _ignoredPaths.Add(entry.Info.Path)))
		{
			_logger.Debug(
				"Ignoring HID device vendor 0x{VendorId:X4}, product 0x{ProductId:X4}, product string '{ProductName:l}', report lengths input {InputLength} output {OutputLength} feature {FeatureLength}: {Reason:l}",
				info.VendorId,
				info.ProductId,
				info.ProductName,
				info.InputReportLength,
				info.OutputReportLength,
				info.FeatureReportLength,
				reason);
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
			try
			{
				Open(info, model);
			}
			catch (Exception ex) when (!Failures.IsFatal(ex))
			{
				// Another process may hold it, it vanished again, or it misbehaved. The retry timer tries again.
				bool firstOfEpisode;
				var serialTail = SerialMask.Tail(info.SerialNumber);
				lock (_stateLock)
				{
					firstOfEpisode = _openFailing.TryAdd(info.Path, new OpenFailure(model.Name, serialTail, ex.Message));
					// Without a USB serial the identity is only known once the device is open, so there is no record to note it on.
					if (!string.IsNullOrEmpty(info.SerialNumber))
					{
						GetRecord(info.SerialNumber, serialTail, model, info).LastError = $"open failure: {ex.Message}";
					}
				}

				if (firstOfEpisode)
				{
					_logger.Warning(
						"Could not open {Model:l} (serial ending {SerialTail:l}): {Error:l}. It is retried every {RetrySeconds} s while it stays attached. "
						+ "If another program uses it, close that program. Otherwise please open an issue at " + StreamDeckSupport.IssuesUrl + " and attach the diagnostics snapshot.",
						model.Name,
						serialTail,
						ex.Message,
						(int)_openRetryInterval.TotalSeconds);
				}
			}
		}
	}

	private void Open(HidDeviceInfo info, StreamDeckModel model)
	{
		var connection = _source.Open(info);
		StreamDeckDevice device;
		DeviceRecord record;
		int? brightness;
		try
		{
			var (serial, serialTail) = ResolveIdentity(info, model, connection);
			lock (_stateLock)
			{
				record = GetRecord(serial, serialTail, model, info);
			}

			device = new StreamDeckDevice(model, serial, connection, _timeProvider, _logger, _options, record.Counters);
		}
		catch
		{
			connection.Dispose();
			throw;
		}

		device.Disconnected += failure => OnDeviceFailed(device, failure);
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
			brightness = _brightness;
		}

		// Queued before the device starts, so the brightness goes out ahead of the first pictures.
		if (brightness is { } percent && model.HasKeyImages)
		{
			_ = ApplyBrightnessAsync(device, percent);
		}

		// Started only after DeviceConnected, so a failure on the first write cannot be reported as a
		// disconnect before the connect.
		Raise(DeviceConnected, device);
		device.Start();
		_ = LogConnectedAsync(device, record);
	}

	// The device serial is the identity Macro Deck keeps the user's setup under, so it must be known
	// before DeviceConnected. The USB serial string comes first, then the protocol's serial report.
	private (string Serial, string SerialTail) ResolveIdentity(HidDeviceInfo info, StreamDeckModel model, IHidConnection connection)
	{
		if (!string.IsNullOrEmpty(info.SerialNumber))
		{
			return (info.SerialNumber, SerialMask.Tail(info.SerialNumber));
		}

		try
		{
			var serial = StreamDeckDevice.ReadSerialNumber(model, connection);
			return (serial, SerialMask.Tail(serial));
		}
		catch (Exception ex) when (!Failures.IsFatal(ex))
		{
			var fallback = FallbackIdentity(model, info);
			_logger.Warning(
				"{Model:l} reported no serial number, neither in its USB descriptor nor in its serial feature report ({Error:l}). It is identified by its USB port instead "
				+ "and will appear as a new device after it is plugged into another port. Please open an issue at " + StreamDeckSupport.IssuesUrl + " and attach the diagnostics snapshot.",
				model.Name,
				ex.Message);
			return (fallback, SerialMask.Tail(null));
		}
	}

	private async Task ApplyBrightnessAsync(StreamDeckDevice device, int percent)
	{
		try
		{
			await device.SetBrightnessAsync(percent).ConfigureAwait(false);
		}
		catch (Exception ex) when (!Failures.IsFatal(ex))
		{
			// The device already logged a refused report, and an ended device is reported as disconnected.
			_logger.Debug("Could not restore brightness {Percent} on {Model:l}: {Error:l}", percent, device.Model.Name, ex.Message);
		}
	}

	// One-shot and re-armed after each refresh, so it runs only while an open failure exists.
	private void UpdateRetryTimer()
	{
		lock (_stateLock)
		{
			if (_disposed || _openFailing.Count == 0)
			{
				StopRetryTimer();
				return;
			}

			if (_retryTimer is not null)
			{
				return;
			}

			var token = new object();
			_retryTimerToken = token;
			_retryTimer = _timeProvider.CreateTimer(OnRetryTimer, token, _openRetryInterval, Timeout.InfiniteTimeSpan);
		}
	}

	private void OnRetryTimer(object? token)
	{
		lock (_stateLock)
		{
			// A late callback of a timer that was already replaced or stopped.
			if (!ReferenceEquals(token, _retryTimerToken))
			{
				return;
			}

			StopRetryTimer();
		}

		_ = RefreshObservedAsync();
	}

	private void StopRetryTimer()
	{
		_retryTimer?.Dispose();
		_retryTimer = null;
		_retryTimerToken = null;
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

	private DeviceRecord GetRecord(string serial, string serialTail, StreamDeckModel model, HidDeviceInfo info)
	{
		if (!_records.TryGetValue(serial, out var record))
		{
			record = new DeviceRecord(model, info, serialTail);
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
		_ = Task.Run(async () =>
		{
			try
			{
				await device.DisposeAsync().ConfigureAwait(false);
			}
			catch (Exception ex) when (!Failures.IsFatal(ex))
			{
				_logger.Debug("Closing the failed {Model:l} threw: {Error:l}", device.Model.Name, ex.Message);
			}
		});
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
