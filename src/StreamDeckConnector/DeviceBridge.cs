using System.Threading.Channels;
using MacroDeck.Sdk.Devices;
using Serilog;
using StreamDeckConnector.Devices;

namespace StreamDeckConnector;

/// <summary>
/// Joins one physical device to its host session. Either side can come and go on its own: the device
/// on a replug, the session on a host reconnect. Whenever both are present every key is painted from
/// the host's current pictures.
/// </summary>
public sealed class DeviceBridge : IAsyncDisposable
{
	private readonly ILogger _logger;
	private readonly object _gate = new();
	private readonly Dictionary<int, string> _pressedWidgets = [];
	private readonly Channel<PendingInteraction> _interactions = Channel.CreateUnbounded<PendingInteraction>(
		new UnboundedChannelOptions { SingleReader = true });
	private readonly Task _sender;
	private StreamDeckDevice? _device;
	private IDeviceSession? _session;
	private long[] _appliedRevisions = [];
	private int _rejectedPictures;

	public DeviceBridge(string serialNumber, StreamDeckModel model, ILogger logger)
	{
		SerialNumber = serialNumber;
		Model = model;
		_logger = logger.ForContext<DeviceBridge>();
		_sender = Task.Run(SendInteractionsAsync);
	}

	public string SerialNumber { get; }

	public StreamDeckModel Model { get; }

	public bool HasSession
	{
		get
		{
			lock (_gate)
			{
				return _session is not null;
			}
		}
	}

	/// <summary>True while a session is open on a host that sends no key pictures for a model with displays.</summary>
	public bool HostSendsNoPictures
	{
		get
		{
			lock (_gate)
			{
				return Model.HasKeyImages && _session is { KeyImageFormat: null };
			}
		}
	}

	public long InteractionsSent { get; private set; }

	public string? LastInteractionProblem { get; private set; }

	public void AttachDevice(StreamDeckDevice device)
	{
		IDeviceSession? session;
		lock (_gate)
		{
			DetachDeviceLocked();
			_device = device;
			_appliedRevisions = new long[Model.KeyCount];
			device.KeyDown += OnKeyDown;
			device.KeyUp += OnKeyUp;
			session = _session;
		}

		if (session is not null)
		{
			_ = PaintAllAsync(session, device);
		}
	}

	public void DetachDevice(StreamDeckDevice device)
	{
		lock (_gate)
		{
			if (ReferenceEquals(_device, device))
			{
				DetachDeviceLocked();
			}
		}
	}

	public Task OpenSessionAsync(IDeviceSession session)
	{
		StreamDeckDevice? device;
		lock (_gate)
		{
			CloseSessionLocked();
			_session = session;
			_appliedRevisions = new long[Model.KeyCount];
			session.KeyImageChanged += OnKeyImageChanged;
			session.Closed += OnSessionClosed;
			device = _device;
		}

		return device is null ? Task.CompletedTask : PaintAllAsync(session, device);
	}

	public async Task ShowLogoAsync(CancellationToken cancellationToken)
	{
		StreamDeckDevice? device;
		lock (_gate)
		{
			device = _device;
			if (device is not null)
			{
				_appliedRevisions = new long[Model.KeyCount];
			}
		}

		if (device is null || !Model.HasKeyImages)
		{
			return;
		}

		try
		{
			await device.ShowLogoAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_logger.Debug("Could not show the logo on {Model:l}: {Error:l}", Model.Name, ex.Message);
		}
	}

	public async ValueTask DisposeAsync()
	{
		lock (_gate)
		{
			DetachDeviceLocked();
			CloseSessionLocked();
		}

		_interactions.Writer.TryComplete();
		await _sender.ConfigureAwait(false);
	}

	private void DetachDeviceLocked()
	{
		if (_device is null)
		{
			return;
		}

		_device.KeyDown -= OnKeyDown;
		_device.KeyUp -= OnKeyUp;
		_device = null;
		_pressedWidgets.Clear();
	}

	private void CloseSessionLocked()
	{
		if (_session is null)
		{
			return;
		}

		_session.KeyImageChanged -= OnKeyImageChanged;
		_session.Closed -= OnSessionClosed;
		_session = null;
		_pressedWidgets.Clear();
	}

	private void OnSessionClosed(object? sender, DeviceSessionClosedEventArgs e)
	{
		lock (_gate)
		{
			if (ReferenceEquals(_session, sender))
			{
				CloseSessionLocked();
			}
		}

		_ = ShowLogoAsync(CancellationToken.None);
	}

	private void OnKeyImageChanged(object? sender, DeviceKeyImageChangedEventArgs e) => Apply(sender as IDeviceSession, e.Image);

	private async Task PaintAllAsync(IDeviceSession session, StreamDeckDevice device)
	{
		if (!Model.HasKeyImages || session.KeyImageFormat is null)
		{
			return;
		}

		try
		{
			// One cell after the other, each applied as it arrives, so a refused call costs one key and not all.
			for (var row = 0; row < Model.Rows; row++)
			{
				for (var column = 0; column < Model.Columns; column++)
				{
					if (!IsCurrent(session, device))
					{
						return;
					}

					var (cellRow, cellColumn) = (row, column);
					var image = await HostCalls.RetryAsync(() => session.GetKeyImageAsync(cellRow, cellColumn)).ConfigureAwait(false);
					if (image is not null)
					{
						Apply(session, image);
					}
				}
			}
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			// The session or the device may have ended meanwhile. The next open or attach paints again.
			_logger.Warning("Could not paint every key of {Model:l}: {Error:l}", Model.Name, ex.Message);
		}
	}

	private bool IsCurrent(IDeviceSession session, StreamDeckDevice device)
	{
		lock (_gate)
		{
			return ReferenceEquals(_session, session) && ReferenceEquals(_device, device);
		}
	}

	private void Apply(IDeviceSession? source, DeviceKeyImage image)
	{
		StreamDeckDevice? device;
		var key = (image.Row * Model.Columns) + image.Column;
		lock (_gate)
		{
			device = _device;
			if (device is null || !ReferenceEquals(source, _session))
			{
				return;
			}

			if (image.Row < 0 || image.Row >= Model.Rows || image.Column < 0 || image.Column >= Model.Columns)
			{
				return;
			}

			if (image.Revision <= _appliedRevisions[key])
			{
				return;
			}

			_appliedRevisions[key] = image.Revision;
		}

		try
		{
			device.SetKeyImage(key, image.Content);
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or ObjectDisposedException)
		{
			if (Interlocked.Increment(ref _rejectedPictures) == 1)
			{
				_logger.Warning(
					"{Model:l} refused a key picture of {Length} bytes ({ContentType:l}): {Error:l}. Further refusals are not logged.",
					Model.Name,
					image.Content.Length,
					image.ContentType,
					ex.Message);
			}
		}
	}

	private void OnKeyDown(int key)
	{
		IDeviceSession? session;
		lock (_gate)
		{
			session = _session;
		}

		if (session is null)
		{
			return;
		}

		var surface = session.CurrentSurface;
		var row = key / Model.Columns;
		var column = key % Model.Columns;
		var widget = surface.Widgets.FirstOrDefault(candidate =>
			column >= candidate.PositionX && column < candidate.PositionX + Math.Max(candidate.Width, 1)
			&& row >= candidate.PositionY && row < candidate.PositionY + Math.Max(candidate.Height, 1));
		if (widget is null)
		{
			return;
		}

		DeviceInteractionKind kind;
		if (widget.SupportedInteractions.Contains(DeviceInteractionKind.Press))
		{
			kind = DeviceInteractionKind.Press;
			lock (_gate)
			{
				_pressedWidgets[key] = widget.Id;
			}
		}
		else if (widget.SupportedInteractions.Contains(DeviceInteractionKind.ShortPress))
		{
			kind = DeviceInteractionKind.ShortPress;
		}
		else
		{
			return;
		}

		Queue(session, kind, widget.Id, surface.Revision);
	}

	private void OnKeyUp(int key)
	{
		IDeviceSession? session;
		string? widgetId;
		lock (_gate)
		{
			session = _session;
			_pressedWidgets.Remove(key, out widgetId);
		}

		if (session is not null && widgetId is not null)
		{
			// No revision: the press itself often rebuilds the surface, and the release still belongs to it.
			Queue(session, DeviceInteractionKind.Release, widgetId, null);
		}
	}

	private void Queue(IDeviceSession session, DeviceInteractionKind kind, string widgetId, long? surfaceRevision) =>
		_interactions.Writer.TryWrite(new PendingInteraction(session, new DeviceInteraction
		{
			Kind = kind,
			Target = new DeviceInteractionTarget { WidgetId = widgetId },
			SurfaceRevision = surfaceRevision,
		}));

	// One sender keeps a press ahead of its release without blocking the HID reader thread.
	private async Task SendInteractionsAsync()
	{
		await foreach (var pending in _interactions.Reader.ReadAllAsync().ConfigureAwait(false))
		{
			try
			{
				var result = await pending.Session.SendInteractionAsync(pending.Interaction).ConfigureAwait(false);
				InteractionsSent++;
				if (result.Status != DeviceInteractionStatus.Accepted)
				{
					LastInteractionProblem = $"{pending.Interaction.Kind} answered {result.Status} {result.ReasonCode}";
					_logger.Debug(
						"{Kind} on {Model:l} was answered {Status} {Reason:l}",
						pending.Interaction.Kind,
						Model.Name,
						result.Status,
						result.ReasonCode);
				}
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				LastInteractionProblem = $"{pending.Interaction.Kind} failed: {ex.Message}";
				_logger.Debug("{Kind} on {Model:l} could not be sent: {Error:l}", pending.Interaction.Kind, Model.Name, ex.Message);
			}
		}
	}

	private sealed record PendingInteraction(IDeviceSession Session, DeviceInteraction Interaction);
}
