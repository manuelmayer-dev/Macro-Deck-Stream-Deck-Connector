using System.Threading.Channels;
using MacroDeck.Sdk.Devices;
using Serilog;
using StreamDeckConnector.Devices;

namespace StreamDeckConnector;

/// <summary>
/// Joins one physical device to its host session. Either side can come and go on its own: the device
/// on a replug, the session on a host reconnect. Macro Deck pushes every key picture after a session
/// opens and after a device comes back online. Keys that still show nothing a moment later are fetched.
/// </summary>
public sealed class DeviceBridge : IAsyncDisposable
{
	private const int PressAttempts = 6;
	private const int ReleaseAttempts = 50;
	private static readonly TimeSpan _interactionRetryDelay = TimeSpan.FromMilliseconds(100);

	private readonly ILogger _logger;
	private readonly object _gate = new();
	private readonly Dictionary<int, string> _pressedWidgets = [];
	private readonly Channel<PendingInteraction> _interactions = Channel.CreateUnbounded<PendingInteraction>(
		new UnboundedChannelOptions { SingleReader = true });
	private readonly Task _sender;
	private StreamDeckDevice? _device;
	private IDeviceSession? _session;
	private readonly HashSet<string> _lostPresses = new(StringComparer.Ordinal);
	private readonly Func<Exception, bool> _isRetryable;
	private readonly TimeSpan _fetchMissingAfter;
	private long[] _appliedRevisions = [];
	private int _rejectedPictures;
	private long _interactionsSent;
	private long _interactionsLost;
	private volatile string? _lastInteractionProblem;

	public DeviceBridge(string serialNumber, StreamDeckModel model, ILogger logger, TimeSpan? fetchMissingAfter = null, Func<Exception, bool>? isRetryable = null)
	{
		SerialNumber = serialNumber;
		Model = model;
		_fetchMissingAfter = fetchMissingAfter ?? TimeSpan.FromMilliseconds(400);
		_isRetryable = isRetryable ?? HostCalls.IsRetryable;
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

	public long InteractionsSent => Interlocked.Read(ref _interactionsSent);

	public long InteractionsLost => Interlocked.Read(ref _interactionsLost);

	public string? LastInteractionProblem => _lastInteractionProblem;

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
			_ = FetchMissingAsync(session, device);
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

	/// <summary>Returns at once: Macro Deck sends the first surface only after the provider's open handler returned.</summary>
	public void OpenSession(IDeviceSession session)
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

		if (device is not null)
		{
			_ = FetchMissingAsync(session, device);
		}
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

	private async Task FetchMissingAsync(IDeviceSession session, StreamDeckDevice device)
	{
		if (!Model.HasKeyImages || session.KeyImageFormat is null)
		{
			return;
		}

		try
		{
			await Task.Delay(_fetchMissingAfter).ConfigureAwait(false);
			for (var row = 0; row < Model.Rows; row++)
			{
				for (var column = 0; column < Model.Columns; column++)
				{
					if (!IsMissing(session, device, (row * Model.Columns) + column))
					{
						continue;
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
			_logger.Warning("Could not fetch every key picture of {Model:l}: {Error:l}", Model.Name, ex.Message);
		}
	}

	private bool IsMissing(IDeviceSession session, StreamDeckDevice device, int key)
	{
		lock (_gate)
		{
			return ReferenceEquals(_session, session) && ReferenceEquals(_device, device) && _appliedRevisions[key] == 0;
		}
	}

	private void Apply(IDeviceSession? source, DeviceKeyImage image)
	{
		if (image.Row < 0 || image.Row >= Model.Rows || image.Column < 0 || image.Column >= Model.Columns)
		{
			return;
		}

		var key = (image.Row * Model.Columns) + image.Column;
		try
		{
			// Enqueued under the lock so a fetched and a pushed picture for one key cannot swap places.
			lock (_gate)
			{
				if (_device is null || !ReferenceEquals(source, _session) || image.Revision <= _appliedRevisions[key])
				{
					return;
				}

				_appliedRevisions[key] = image.Revision;
				_device.SetKeyImage(key, image.Content);
			}
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
			var kind = pending.Interaction.Kind;
			var widgetId = pending.Interaction.Target.WidgetId ?? string.Empty;
			if (kind == DeviceInteractionKind.Release && _lostPresses.Remove(widgetId))
			{
				continue;
			}

			try
			{
				// A refused release would leave the widget held on the host, so it is repeated until taken.
				// A press that is still refused after a moment is stale and is dropped with its release.
				var attempts = kind == DeviceInteractionKind.Release ? ReleaseAttempts : PressAttempts;
				var result = await HostCalls.RetryAsync(
					() => pending.Session.SendInteractionAsync(pending.Interaction),
					_isRetryable,
					_interactionRetryDelay,
					attempts).ConfigureAwait(false);
				Interlocked.Increment(ref _interactionsSent);
				if (result.Status != DeviceInteractionStatus.Accepted)
				{
					_lastInteractionProblem = $"{kind} answered {result.Status} {result.ReasonCode}";
					_logger.Debug("{Kind} on {Model:l} was answered {Status} {Reason:l}", kind, Model.Name, result.Status, result.ReasonCode);
				}
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				if (kind == DeviceInteractionKind.Press)
				{
					_lostPresses.Add(widgetId);
				}

				_lastInteractionProblem = $"{kind} failed: {ex.Message}";
				if (Interlocked.Increment(ref _interactionsLost) == 1)
				{
					_logger.Warning("A key {Kind} on {Model:l} could not be sent to Macro Deck: {Error:l}. Further losses are counted in the diagnostics.", kind, Model.Name, ex.Message);
				}
			}
		}
	}

	private sealed record PendingInteraction(IDeviceSession Session, DeviceInteraction Interaction);
}
