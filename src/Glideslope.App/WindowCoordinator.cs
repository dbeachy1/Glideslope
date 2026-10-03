using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Threading;
using Glideslope.Core;
using Glideslope.Domain;
using Glideslope.Monitoring;

namespace Glideslope.App;

/// <summary>
/// Owns every card window, the layout, gestures and settings commits.
///
/// Coordinates card windows, layout, gestures, settings commits, and activation.
///
/// The live layout is authoritative across asynchronous settings saves: commits update only owned fields, then
/// normalize the live layout. Gestures may run while a store write is in flight, so callers must preserve changes
/// made during awaits. Layout-changing operations finish active gestures first. Restore paths recover saved
/// placements against current displays; external window-manager geometry changes are tracked as pending gestures.
/// - WindowCoordinator.Settings.cs: the Settings window, Settings saves and disabling a provider.
/// - WindowCoordinator.Persistence.cs: capturing the layout and the coordinator's own store commits.
/// - WindowCoordinator.Restore.cs: startup restore, Reset window positions, reset size and display changes.
/// - WindowCoordinator.Gestures.cs: layout gestures, from the press through commit, cancel and interrupt.
/// - WindowCoordinator.Docking.cs: the magnet candidate, docked sides and Undock.
/// - WindowCoordinator.Raise.cs: raising the cards together and keeping Settings above them.
/// - WindowCoordinator.Modes.cs: the full/mini toggle, mode changes on dock and the mixed-group check.
/// - WindowCoordinator.CardScale.cs: card-size changes and their settle.
/// - WindowCoordinator.Peek.cs: the Ctrl peek wiring to CardPeekController.
/// </summary>
internal sealed partial class WindowCoordinator : IAsyncDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly SettingsStore _settingsStore;
    private readonly IStartupRegistration _startup;
    private readonly ITrayService _tray;
    private readonly IInstanceBroker _broker;
    private readonly string? _launcherPath;
    private readonly string? _installedLauncherPath;
    private readonly IDiagnosticSink _diagnostics;
    private readonly Func<AppSettings, Task<ProviderMonitoringSession>> _monitoringFactory;
    private readonly ICardLayoutPlatform _layoutPlatform;
    private readonly Dictionary<string, ProviderUsageCardWindow> _windows = new(StringComparer.Ordinal);
    private readonly CoordinatorIntentGate _intentGate = new();
    private AppSettings _settings = AppSettings.CreateDefault();
    private SettingsWindow? _settingsWindow;
    private bool _settingsOpening;
    private bool _closing;
    private bool _startupChoicePending;
    private bool _startupRegistered;
    private bool _runningHidden;
    private bool _applyingWindowState;
    private int _disposeStarted;
    private ProviderMonitoringSession? _monitoring;
    private CardLayoutGeometrySnapshot? _layoutGeometry;
    private bool _applyingLayout;
    private readonly TimeProvider _time;
    // Last applied position for each card. X11 reports moves asynchronously, after _applyingLayout is false, so
    // matching reports are recognized as echoes rather than new gestures.
    private readonly Dictionary<string, PixelPoint> _appliedPositions = new(StringComparer.Ordinal);
    // Completes when StartAsync has loaded settings and restored the layout, including hidden startup. Earlier
    // activations wait for this signal.
    private readonly TaskCompletionSource _startupReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Shown in Settings; null in builds that write no file log.
    private readonly string? _logFilePath;
    private readonly Action<AppSettings>? _applyLocale;
    // Live mode per provider id; mode is selected at launch and is not persisted.
    private readonly CardModes _modes = new();

    public WindowCoordinator(
        IClassicDesktopStyleApplicationLifetime desktop,
        SettingsStore settingsStore,
        IStartupRegistration startup,
        ITrayService tray,
        IInstanceBroker broker,
        string? launcherPath,
        string? installedLauncherPath,
        IDiagnosticSink diagnostics,
        Func<AppSettings, Task<ProviderMonitoringSession>> monitoringFactory,
        ICardLayoutPlatform? layoutPlatform = null,
        TimeProvider? timeProvider = null,
        Func<bool>? primaryButtonHeld = null,
        IWindowRaiser? windowRaiser = null,
        string? logFilePath = null,
        Action<AppSettings>? applyLocale = null)
    {
        _logFilePath = logFilePath;
        _applyLocale = applyLocale;
        _desktop = desktop;
        _settingsStore = settingsStore;
        _startup = startup;
        _tray = tray;
        _broker = broker;
        _launcherPath = launcherPath;
        _installedLauncherPath = installedLauncherPath;
        _diagnostics = diagnostics;
        _monitoringFactory = monitoringFactory;
        // Both the peek hold delay and pending placement expiry use this clock.
        _time = timeProvider ?? TimeProvider.System;
        _layoutPlatform = layoutPlatform ?? new CardLayoutPlatform(
            new AvaloniaCardWindowGeometryBackend(!OperatingSystem.IsWindows(), _time));
        // Peek polls Windows key state or the app's X11 connection. Without X11 on Linux, card events drive
        // peek and UpdatePeekProbeTimer creates no polling timer. The controller reads _modes.Of and
        // IsLayoutBusy only after construction, when _gestures, _scaleSettle and _closing are initialized;
        // those decisions use current state rather than a constructor-time snapshot.
        _x11 = OperatingSystem.IsWindows() ? null : X11Connection.TryOpen();
        _peekProbe = OperatingSystem.IsWindows() ? new WindowsPeekInputProbe()
            : _x11 is not null ? new X11PeekInputProbe(_x11)
            : null;
        if (!OperatingSystem.IsWindows() && _x11 is null)
            _diagnostics.Record(new DiagnosticEvent("card_peek_probe_unavailable", Status: "x11_connection_failed"));
        // The peek controller uses this raiser.
        _raiser = windowRaiser ?? WindowRaiser.Create(_x11);
        // A nonactivating raiser brings the peeking card to the front of its band. An activating raiser
        // cannot be handed to the controller: its Activated echo would start a group raise instead.
        _peekController = new CardPeekController(_layoutPlatform, _peekProbe, _diagnostics, PeekContextFor,
            _modes.Of, IsLayoutBusy, _time,
            _raiser.ActivatesWindow ? null : window => _raiser.RaiseWithoutActivating((Window)window));
        _primaryButtonHeld = primaryButtonHeld ?? PrimaryPointerButtonProbe.IsHeld;
        _gestures = new LayoutGestureTracker(_time, GestureQuietPeriod, _primaryButtonHeld);
        // Cancelled always fires synchronously from a call this coordinator itself makes on the UI
        // thread (Begin's supersede, or our own Cancel calls in WindowCoordinator.Gestures.cs), so handling it
        // inline keeps the restore-then-continue ordering callers like DisableProviderAsync depend on. Settled can
        // also arrive from the quiet-period timer on a thread-pool thread, so it is always marshaled.
        _gestures.Cancelled += OnGestureCancelled;
        _gestures.Settled += OnGestureSettled;
        _displayChanges = new QuietPeriodDebouncer(_time, DisplayChangeQuietPeriod,
            (source, signals) => Dispatcher.UIThread.Post(() => RevalidateLayoutForDisplays(source, signals)));
        _scaleSettle = new QuietPeriodDebouncer(_time, CardScaleSettlePeriod,
            (source, steps) => Dispatcher.UIThread.Post(() => SettleCardScale(source, steps)));
        _desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _desktop.ShutdownRequested += OnDesktopShutdownRequested;
        _desktop.Exit += (_, _) => _ = ObserveDisposeAsync();
        _tray.ShowRequested += (_, _) => ShowAll();
        _tray.ExitRequested += (_, _) => _ = ObserveExitAsync();
        _tray.ViabilityChanged += OnTrayViabilityChanged;
    }

    /// <summary>Runs startup and then opens the gate that activations queued before
    /// or during startup wait on (see HandleActivationAsync). A failed startup fails the gate too, so a waiting
    /// activation is dropped and logged instead of showing default-settings cards.</summary>
    public async Task StartAsync(ActivationIntent intent)
    {
        try
        {
            await StartCoreAsync(intent).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _startupReady.TrySetException(ex);
            throw;
        }
        _startupReady.TrySetResult();
        _diagnostics.Record(new DiagnosticEvent("startup_ready", Status: $"hidden={_runningHidden},cards={_windows.Count}"));
    }

    private async Task StartCoreAsync(ActivationIntent intent)
    {
        var load = await _settingsStore.LoadAsync().ConfigureAwait(true);
        _settings = load.Settings;
        _applyLocale?.Invoke(_settings);
        _tray.RefreshLocalizedText();
        // Preserve the store state separately from subsequent in-memory layout changes.
        _persisted = load.Settings.Clone();
        _lastSettingsFieldsRevision = load.Settings.Revision;
        // The saved scale applies before any card exists, so the cards open at it and the
        // layout restore takes the saved sizes (which are the real scaled window sizes) as they are.
        SetAppliedScale(CardMode.Full, CardScale.Normalize(CardScale.PercentFor(_settings, CardMode.Full)));
        CardScale.SetPercent(_settings, CardMode.Full, AppliedScale(CardMode.Full));
        SetAppliedScale(CardMode.Mini, CardScale.Normalize(CardScale.PercentFor(_settings, CardMode.Mini)));
        CardScale.SetPercent(_settings, CardMode.Mini, AppliedScale(CardMode.Mini));
        UpdatePeekSetting(_settings.CtrlPeekFullCard);
        // Snapping off means no snapped groups, also for a layout saved before that rule existed; the
        // startup layout restore below persists the detached layout.
        var startupLayout = LayoutForSnapSetting(_settings.Layout, _settings.SnapToScreenEdge);
        if (!ReferenceEquals(startupLayout, _settings.Layout))
        {
            _diagnostics.Record(new DiagnosticEvent("layout_unsnapped_by_setting", Status: $"startup,edges={_settings.Layout.Edges.Count}"));
            _settings.Layout = startupLayout;
        }
        // Select the startup mode before creating cards. Count groups restored to full size for diagnostics because
        // PrepareForMode returns only the resulting layout.
        var startMode = _settings.StartInMiniMode ? CardMode.Mini : CardMode.Full;
        var beforePrepare = _settings.Layout;
        _settings.Layout = LayoutPolicy.PrepareForMode(beforePrepare, _settings.EnabledProviderIds, startMode,
            CardScale.DefaultSize(CardMode.Mini, AppliedScale(CardMode.Mini)));
        var restoredFullSizes = startMode == CardMode.Full
            ? CountGroupsRestoredToFullSize(beforePrepare, _settings.EnabledProviderIds)
            : 0;
        _diagnostics.Record(new DiagnosticEvent("card_mode_startup", Status:
            $"mode={startMode},cards={_settings.EnabledProviderIds.Count},restoredFullSizes={restoredFullSizes}"));
        ApplyApplicationTheme();
        if (load.IssueCode is not null)
            _diagnostics.Record(new DiagnosticEvent(load.IssueCode, Status: "settings_recovered"));
        _monitoring = await _monitoringFactory(_settings).ConfigureAwait(true);
        _monitoring.Scheduler.StateChanged += OnProviderStateChanged;
        await _monitoring.StartAsync(_settings).ConfigureAwait(true);
        // Runs once per launch, after the single-instance owner check that already happened before
        // this coordinator was constructed, so a concurrent second process cannot race this reconciliation.
        // Reconcile only against the installed launcher. A development build without one leaves any legacy
        // sign-in entry untouched instead of migrating it to a temporary executable.
        var registration = await _startup.ReconcileLegacyArtifactAsync(_installedLauncherPath).ConfigureAwait(true);
        if (registration.IssueCode is null && _launcherPath is null)
        {
            registration = new StartupRegistrationState(false, "startup_launcher_missing");
        }
        _startupRegistered = registration.IsRegistered;
        _diagnostics.Record(new DiagnosticEvent("startup_registration_observed", Status: registration.IsRegistered ? "registered" : "not_registered"));
        _startupChoicePending = OperatingSystem.IsLinux() && !_settings.StartupChoiceCompleted && intent == ActivationIntent.Manual;
        _tray.SetVisible(true);
        if (intent == ActivationIntent.Autostart && _tray.IsUsable)
        {
            _runningHidden = true;
            _diagnostics.Record(new DiagnosticEvent("autostart_hidden", Status: "tray_verified"));
            return;
        }
        if (intent == ActivationIntent.Autostart)
            _diagnostics.Record(new DiagnosticEvent("autostart_visible_fallback", Status: "tray_viability_unproven"));
        ShowAll();
        if (_startupChoicePending) _ = OpenSettingsAsync();
    }

    /// <summary>
    /// Waits for settings and layout restoration before showing cards in response to an activation. The wait runs
    /// detached so slow startup does not block the broker's delivery deadline.
    /// </summary>
    public Task HandleActivationAsync(ActivationIntent intent)
    {
        if (_startupReady.Task.IsCompletedSuccessfully)
        {
            Dispatcher.UIThread.Post(() => ShowForActivation(intent));
            return Task.CompletedTask;
        }
        _diagnostics.Record(new DiagnosticEvent("activation_deferred_until_started", Status: intent.ToString()));
        _ = ShowAfterStartupAsync(intent);
        return Task.CompletedTask;
    }

    private async Task ShowAfterStartupAsync(ActivationIntent intent)
    {
        try
        {
            await _startupReady.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _diagnostics.Record(new DiagnosticEvent("activation_dropped", Status: $"startup_not_ready,intent={intent},{ex.GetType().Name}"));
            return;
        }
        Dispatcher.UIThread.Post(() => ShowForActivation(intent));
    }

    private void ShowForActivation(ActivationIntent intent)
    {
        if (intent == ActivationIntent.Manual || !_tray.IsUsable)
        {
            ShowAll();
            return;
        }
        _diagnostics.Record(new DiagnosticEvent("activation_ignored", Status: $"intent={intent},tray_usable"));
    }

    private void ShowAll()
    {
        if (_closing) return;
        // Commit a gesture whose button is already up; canceling would revert a completed move.
        InterruptActiveGesture("window_set_changed");
        // New cards use the configured startup mode, independent of the modes of cards already open.
        var newCardMode = _settings.StartInMiniMode ? CardMode.Mini : CardMode.Full;
        var added = new List<(ProviderUsageCardWindow Window, int Index)>();
        foreach (var providerId in _settings.EnabledProviderIds)
        {
            if (_windows.ContainsKey(providerId))
            {
                if (_windows[providerId].WindowState == WindowState.Minimized)
                    _windows[providerId].WindowState = WindowState.Normal;
                _windows[providerId].Show();
                _windows[providerId].Activate();
                continue;
            }
            var window = new ProviderUsageCardWindow(providerId, showMark: true, providerId => _ = RetryProviderAsync(providerId), _diagnostics);
            // The current scale, minimum, and default size apply before the window is
            // shown, placed or observed, so the first frame is already the scaled card (window design §18).
            // Set content mode before Show and register it before any geometry operation needs the mode.
            CardModeApplier.Apply([((ICardModeWindow)window, newCardMode, AppliedScale(newCardMode))]);
            _modes.Set([providerId], newCardMode);
            var defaultSize = CardScale.DefaultSize(newCardMode, AppliedScale(newCardMode));
            window.Width = defaultSize.Width;
            window.Height = defaultSize.Height;
            // Set startup placement before Show so the window manager applies it.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Topmost = _settings.AlwaysOnTop;
            window.UserCloseRequested += OnUserCloseRequested;
            window.SettingsRequested += (_, _) => _ = OpenSettingsAsync();
            window.ResetSizeRequested += (_, _) => _ = ResetCardSizeAsync(window.ProviderId);
            window.MinimizeRequested += OnMinimizeRequested;
            window.PropertyChanged += OnWindowPropertyChanged;
            // Position is not an AvaloniaProperty, so PropertyChanged does not report moves. Subscribe separately
            // to track move gestures and settle them when movement ends.
            window.PositionChanged += OnWindowPositionChanged;
            window.MoveDragStarted += OnMoveDragStarted;
            window.DetachDragStarted += OnDetachDragStarted;
            window.Activated += OnCardActivated;
            window.UndockRequested += OnUndockRequested;
            window.HistoryWindowStepRequested += OnHistoryWindowStepRequested;
            window.ResizeDragStarted += OnResizeDragStarted;
            window.LayoutGestureCancelled += OnLayoutGestureCancelled;
            window.CardScaleRequested += OnCardScaleRequested;
            window.CardScaleChangeEnded += OnCardScaleChangeEnded;
            window.ModeToggleRequested += OnModeToggleRequested;
            window.PeekHoverChanged += OnCardPeekHoverChanged;
            window.PeekInputObserved += OnCardPeekInputObserved;
            window.Closed += OnCardClosed;
            GestureEndSources.Attach(window, providerId, () => ReferenceEquals(_leadWindow, window),
                () => BeginLayoutGesture(window, LayoutGestureKind.Pending, null, origin: "sizemove"),
                source => _gestures.End(source), _diagnostics);
            _windows.Add(providerId, window);
            var states = _monitoring?.Scheduler.GetStates();
            window.UpdateState(states is not null && states.TryGetValue(providerId, out var state) ? state : null);
            window.Show();
            // Display-change handling is window-independent and debounced, so reports from multiple cards coalesce
            // into one revalidation. A report from a closed card is harmless.
            if (window.Screens is { } screens) screens.Changed += OnScreensChanged;
            window.ScalingChanged += OnCardScalingChanged;
            if (states is not null && states.TryGetValue(providerId, out var currentState))
                LoadHistoryForWindow(window, currentState);
            added.Add((window, _windows.Count - 1));
        }
        PlaceNewWindows(added);
        if (_windows.Count > 0 && !_layoutInitialized)
            RestoreCardLayout();
        else if (added.Count > 0)
        {
            if (TryCaptureGeometry(out var geometry))
            {
                _layoutGeometry = geometry;
                RefreshCardGroupState(geometry);
                PersistCapturedLayout(geometry);
            }
        }
        _runningHidden = false;
        UpdateTaskbarRepresentative();
    }

    private async Task RetryProviderAsync(string providerId)
    {
        if (_closing || _monitoring is null) return;
        try { await _monitoring.Scheduler.RefreshAsync(providerId).ConfigureAwait(true); }
        catch (ObjectDisposedException) { }
        catch (ArgumentException) { _diagnostics.Record(new DiagnosticEvent("provider_retry_rejected", providerId, "provider_unavailable")); }
    }

    private void OnProviderStateChanged(object? sender, ProviderStateChangedEventArgs args)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_closing || !_windows.TryGetValue(args.State.ProviderId, out var window)) return;
            window.UpdateState(args.State);
            if (!args.State.IsReading && args.State.Status == ProviderStatus.Ready && args.State.Snapshot is not null)
                LoadHistoryForWindow(window, args.State);
        });
    }

    /// <summary>
    /// Places new cards on the primary work area in logical units, applying the cascade through the layout platform
    /// with the primary monitor's transform.
    /// </summary>
    private void PlaceNewWindows(IReadOnlyList<(ProviderUsageCardWindow Window, int Index)> added)
    {
        if (added.Count == 0 || !TryCaptureGeometry(out var geometry)) return;
        var primary = geometry.Monitors[geometry.PrimaryMonitorId];
        var placement = new LayoutSettings();
        var rects = new Dictionary<string, LogicalRect>(StringComparer.Ordinal);
        foreach (var (window, index) in added)
        {
            // 2.0 mini mode: every card added in one ShowAll batch shares the same new-card mode (set on it
            // already, in ShowAll, before this runs), so its own default size at its own mode's scale is used.
            var mode = _modes.Of(window.ProviderId);
            var size = CardScale.DefaultSize(mode, AppliedScale(mode));
            var rect = CascadeRect(primary.WorkArea.Bounds, size, index);
            rects[window.ProviderId] = rect;
            placement.Cards.Add(new CardLayoutSettings
            {
                ProviderId = window.ProviderId,
                Width = size.Width,
                Height = size.Height,
                MonitorId = primary.WorkArea.MonitorId,
            });
            _diagnostics.Record(new DiagnosticEvent("layout_default_placement", window.ProviderId,
                $"index={index},monitor={primary.WorkArea.MonitorId},x={rect.X:0.#},y={rect.Y:0.#},width={size.Width:0.#},height={size.Height:0.#},mode={mode}"));
        }
        _applyingLayout = true;
        try { ApplyCardGeometry(placement, rects, geometry); }
        finally { _applyingLayout = false; }
    }

    /// <summary>The n-th new card's rectangle on a work area: centered, then 48 px right
    /// and down per earlier card, kept fully inside the work area where the card fits, and at the work area's
    /// start on an axis where it does not.</summary>
    internal static LogicalRect CascadeRect(LogicalRect area, LogicalSize size, int index)
    {
        const double spacing = 48;
        var x = CascadeAxis(area.X, area.Width, size.Width, index * spacing);
        var y = CascadeAxis(area.Y, area.Height, size.Height, index * spacing);
        return new LogicalRect(x, y, size.Width, size.Height);
    }

    private static double CascadeAxis(double start, double extent, double length, double offset)
    {
        var free = extent - length;
        return free <= 0 ? start : start + Math.Clamp(free / 2 + offset, 0, free);
    }

    private void OnUserCloseRequested(object? sender, EventArgs e)
    {
        if (sender is not ProviderUsageCardWindow window || _closing) return;
        if (UserClosePolicy.Decide(_windows.Count) == UserCloseDecision.ExitKeepSelection)
        {
            _diagnostics.Record(new DiagnosticEvent("user_closed_final_card", Status: "exit_keep_selection"));
            _ = ExitAsync();
            return;
        }
        _ = DisableProviderAsync(window.ProviderId);
    }

    private async Task ExitAsync()
    {
        await using (var intentLease = await _intentGate.EnterAsync().ConfigureAwait(true))
        {
            if (_closing)
            {
                return;
            }
            // End an active peek before closing or exit changes the layout.
            EndPeekForLayoutBusy();
            // Flush a pending card-scale settle and queued layout save before marking the coordinator closed.
            if (_scaleSettle.CancelPending())
            {
                _scaleSession = null;
                if (!(TryCaptureGeometry(out var geometry) && CaptureLayoutForSave(geometry))) _coordinatorSaveRequested = true;
            }
            // Mark the instance closing before flushing so relaunches wait for teardown instead of being lost.
            _broker.BeginClosing();
            if (_coordinatorSaveRequested) await WriteCoordinatorSaveUnderGateAsync("exit_flush").ConfigureAwait(true);
            _closing = true;
            _diagnostics.Record(new DiagnosticEvent("shell_exit_requested", Status: "flush_and_teardown"));
            foreach (var window in _windows.Values)
            {
                window.CloseProgrammatically();
            }
            _windows.Clear();
        }

        await DisposeAsync().ConfigureAwait(true);
        _desktop.Shutdown(0);
    }

    /// <summary>
    /// Performs UI teardown synchronously before asynchronous disposal. The desktop.Exit dispatcher stops after
    /// Exit returns, so remaining awaits must not require a UI-context continuation. Dispose the broker last so
    /// it holds the single-instance lock until settings and history are closed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        if (!_closing) _closing = true;
        // The desktop.Exit path reaches here without ExitAsync; BeginClosing is idempotent.
        _broker.BeginClosing();
        // 2.1 Ctrl peek (2.1 design §5.4): closing and exit end an active peek first (idempotent with ExitAsync's
        // own call above; DisposeAsync also runs standalone from the desktop.Exit path, without ExitAsync).
        EndPeekForLayoutBusy();
        _peekProbeTimer?.Stop();
        CancelActiveGesture("app_exit");
        _displayChanges.Dispose();
        _scaleSettle.Dispose();
        _startupReady.TrySetCanceled();
        foreach (var window in _windows.Values) window.CloseProgrammatically();
        _windows.Clear();
        // The shared X connection is no longer needed after stopping the probe and closing the cards.
        _x11?.Dispose();
        if (_monitoring is not null) _monitoring.Scheduler.StateChanged -= OnProviderStateChanged;
        _tray.ViabilityChanged -= OnTrayViabilityChanged;
        var trayDisposal = _tray.DisposeAsync();
        await _intentGate.DisposeAsync().ConfigureAwait(false);
        if (_monitoring is not null)
        {
            await _monitoring.DisposeAsync().ConfigureAwait(false);
            _monitoring = null;
        }
        await trayDisposal.ConfigureAwait(false);
        await _broker.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The session is ending or the lifetime was asked to shut down. Avalonia closes windows synchronously before
    /// Exit, so mark the coordinator closed before that teardown. Card closes then trigger no provider actions or
    /// partial layout saves. Pending card-scale settles and coordinator saves cannot be awaited in this query.
    /// </summary>
    private void OnDesktopShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (e.Cancel || _closing) return;
        var pendingScaleSettle = _scaleSettle.CancelPending();
        _diagnostics.Record(new DiagnosticEvent("session_end_requested", Status:
            $"cards={_windows.Count},pendingScaleSettle={pendingScaleSettle},pendingSave={_coordinatorSaveRequested}"));
        // The same order as DisposeAsync: closing first, so neither step below persists anything.
        _closing = true;
        _broker.BeginClosing();
        EndPeekForLayoutBusy();
        CancelActiveGesture("app_exit");
    }

    private async Task ObserveExitAsync()
    {
        try
        {
            await ExitAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _diagnostics.Record(new DiagnosticEvent("shell_exit_failed", Status: $"teardown_aborted,{ex.GetType().Name}"));
            _desktop.TryShutdown(1);
        }
    }

    private async Task ObserveDisposeAsync()
    {
        try
        {
            await DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _diagnostics.Record(new DiagnosticEvent("shell_dispose_failed", Status: $"teardown_incomplete,{ex.GetType().Name}"));
        }
    }

    private void OnTrayViabilityChanged(object? sender, TrayViabilityChangedEventArgs e)
    {
        if (e.IsUsable || _closing || !_runningHidden) return;
        _diagnostics.Record(new DiagnosticEvent("tray_host_lost", Status: e.Reason));
        Dispatcher.UIThread.Post(ShowAll);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_closing) return;
        // Window.Position is a plain CLR property in Avalonia 12.1.3, not an AvaloniaProperty, so it
        // never appears here (design doc §1); OnWindowPositionChanged (WindowCoordinator.Gestures.cs) is what
        // observes a move.
        if (!_applyingLayout && sender is ProviderUsageCardWindow changed &&
            e.Property.Name is nameof(Window.Width) or nameof(Window.Height))
        {
            // Linux window managers report requested sizes asynchronously, outside the apply. Confirm the report
            // as an echo so a peek resize is not treated as a user resize and saved to the layout.
            var answer = _layoutPlatform.ConfirmSize(changed, new LogicalSize(changed.Width, changed.Height));
            OnCardGeometryChanged(changed, isSizeChange: true, isApplyEcho: answer != PlacementConfirmation.None);
        }
        if (_applyingWindowState || !string.Equals(e.Property.Name, nameof(Window.WindowState), StringComparison.Ordinal)) return;
        if (sender is not ProviderUsageCardWindow representative || !ReferenceEquals(representative, TaskbarRepresentative())) return;
        if (representative.WindowState is not (WindowState.Minimized or WindowState.Normal or WindowState.Maximized)) return;
        _applyingWindowState = true;
        try
        {
            foreach (var window in _windows.Values.Where(window => !ReferenceEquals(window, representative)))
                window.WindowState = representative.WindowState;
        }
        finally { _applyingWindowState = false; }
    }

    /// <summary>
    /// Minimizes every card when the header button is clicked. On Windows the representative card's taskbar state
    /// also propagates to the group; tray Show and a new activation restore minimized cards on every platform. Finish
    /// any active gesture or peek before minimizing.
    /// </summary>
    private void OnMinimizeRequested(object? sender, EventArgs e)
    {
        if (_closing || sender is not ProviderUsageCardWindow clicked) return;
        InterruptActiveGesture("minimize");
        EndPeekForLayoutBusy();
        var cards = _windows.Values.Where(window => window.IsVisible && window.WindowState != WindowState.Minimized).ToList();
        _applyingWindowState = true;
        try
        {
            foreach (var window in cards) window.WindowState = WindowState.Minimized;
        }
        finally { _applyingWindowState = false; }
        _diagnostics.Record(new DiagnosticEvent("cards_minimized", clicked.ProviderId, $"cards={cards.Count}"));
    }

    private void OnCardClosed(object? sender, EventArgs e)
    {
        if (sender is ProviderUsageCardWindow closed)
        {
            // Clear this card's hover so polling cannot retain a closed window.
            InvokePeekController(() => _peekController.OnCardRemoved(closed.ProviderId));
            // End any remaining peek before removing the card's mode; this also covers a peek on another card.
            EndPeekForLayoutBusy();
            _activationOrder.Remove(closed);
            _appliedPositions.Remove(closed.ProviderId);
            // Removing twice is safe when provider disable already removed the mode.
            _modes.Remove(closed.ProviderId);
        }
        if (sender is ProviderUsageCardWindow window && ReferenceEquals(window, _leadWindow))
            CancelActiveGesture("card_closed");
    }

    private void ApplyLayoutResult(LayoutPolicyResult result, CardLayoutGeometrySnapshot geometry)
    {
        if (!result.Succeeded || result.ProposedSettings is null) return;
        _applyingLayout = true;
        try
        {
            ApplyCardGeometry(result.ProposedSettings, result.ProposedRects, geometry);
            _settings.Layout = result.ProposedSettings;
            RefreshCardGroupState(geometry with { Rects = result.ProposedRects });
        }
        finally { _applyingLayout = false; }
    }

    private bool TryCaptureGeometry(out CardLayoutGeometrySnapshot geometry)
    {
        try
        {
            geometry = CaptureCardGeometry();
            return true;
        }
        catch (Exception ex)
        {
            geometry = null!;
            // Include platform layout_* errors as well as the exception type when geometry capture fails.
            var code = ex is InvalidOperationException { Message: var message } && message.StartsWith("layout_", StringComparison.Ordinal)
                ? $",{message}" : string.Empty;
            _diagnostics.Record(new DiagnosticEvent("layout_geometry_unavailable", Status: $"layout_unavailable,{ex.GetType().Name}{code}"));
            return false;
        }
    }

    private void RefreshCardGroupState(CardLayoutGeometrySnapshot geometry)
    {
        foreach (var providerId in _settings.EnabledProviderIds)
        {
            if (_windows.TryGetValue(providerId, out var window))
            {
                window.SetMarkVisible(true);
                // The exact docked sides control which Undock actions are available.
                window.SetDockedSides(DockedSidesOf(_settings.Layout, providerId));
            }
        }
        // A dock assigns every incoming member the destination mode. Report any mixed-mode group without changing it.
        foreach (var group in MixedModeGroups(_settings.Layout, _settings.EnabledProviderIds, _modes.Of))
            _diagnostics.Record(new DiagnosticEvent("card_mode_mixed_group", Status: string.Join('|', group)));
    }

    /// <summary>
    /// Reports the peeking card's saved mini rectangle to coordinator layout operations. The peek controller uses
    /// live geometry for its own applies; other captures and saves must not persist the temporary full-size rect.
    /// </summary>
    private CardLayoutGeometrySnapshot CaptureCardGeometry()
    {
        var geometry = _layoutPlatform.Capture(LayoutWindowMap());
        if (_peekController.PeekingCard is { } peekingCard && _peekController.PeekingPreRect is { } preRect)
            geometry = geometry with { Rects = geometry.Rects.SetItem(peekingCard, preRect) };
        return geometry;
    }

    private void ApplyCardGeometry(LayoutSettings settings, IReadOnlyDictionary<string, LogicalRect> rects,
        CardLayoutGeometrySnapshot geometry)
    {
        _layoutPlatform.Apply(LayoutWindowMap(), settings, rects, geometry);
        // Remember applied positions to identify asynchronous position echoes.
        foreach (var providerId in rects.Keys)
            if (_windows.TryGetValue(providerId, out var window)) _appliedPositions[providerId] = window.Position;
    }

    private IReadOnlyDictionary<string, ICardLayoutWindow> LayoutWindowMap() =>
        _windows.ToDictionary(pair => pair.Key, pair => (ICardLayoutWindow)pair.Value, StringComparer.Ordinal);

    // Match the card window's scaled minimum. One logical pixel of slack absorbs fractional physical-pixel
    // readback at non-integer display scales; each mode and card uses its own minimum.

    /// <summary>One card's usable minimum: its mode-specific minimum at its applied scale.
    /// Every group-scoped policy call (MoveGroup, ResizeGroup, DetachCard,
    /// SnapGroupToScreenEdges) passes the minimum of the card (or, for PreviewDock, the destination) it acts on.</summary>
    private LogicalSize UsableMinimumFor(string providerId)
    {
        var mode = _modes.Of(providerId);
        return CardScale.UsableMinimum(mode, AppliedScale(mode));
    }

    /// <summary>2.0 mini mode (2.0 design §5.1, §3.1): every enabled card's usable minimum, for the whole-layout
    /// calls (RecoverMissingMonitor, RecoverGeminiLayout) that see full and mini groups side by side.</summary>
    private IReadOnlyDictionary<string, LogicalSize> UsableMinimumsByProvider() =>
        _settings.EnabledProviderIds.ToDictionary(id => id, UsableMinimumFor, StringComparer.Ordinal);

    /// <summary>2.0 mini mode: one card's real (not usable-minus-one-pixel) minimum size, for
    /// LayoutPolicy.EnsureMinimumSizes's per-card overload.</summary>
    private LogicalSize MinimumSizeFor(string providerId)
    {
        var mode = _modes.Of(providerId);
        return CardScale.MinimumSize(mode, AppliedScale(mode));
    }

    private IReadOnlyDictionary<string, LogicalSize> MinimumSizesByProvider() =>
        _settings.EnabledProviderIds.ToDictionary(id => id, MinimumSizeFor, StringComparer.Ordinal);

    /// <summary>2.0 mini mode: every enabled card's usable minimum during a scale change of <paramref name="scaledMode"/>
    /// to <paramref name="scaledPercent"/>: that mode's cards use the new percent; the other mode's cards keep
    /// their own current applied scale, since only <paramref name="scaledMode"/>'s windows are being resized.</summary>
    private IReadOnlyDictionary<string, LogicalSize> UsableMinimumsByProviderForScale(CardMode scaledMode, int scaledPercent) =>
        _settings.EnabledProviderIds.ToDictionary(id => id, id =>
        {
            var cardMode = _modes.Of(id);
            return CardScale.UsableMinimum(cardMode, cardMode == scaledMode ? scaledPercent : AppliedScale(cardMode));
        }, StringComparer.Ordinal);

    private static LogicalWorkArea WorkAreaFor(CardLayoutGeometrySnapshot geometry, string providerId) =>
        geometry.Monitors[geometry.MonitorByProvider[providerId]].WorkArea;

    private static Dictionary<string, LogicalWorkArea> WorkAreasByProvider(CardLayoutGeometrySnapshot geometry) =>
        geometry.MonitorByProvider.ToDictionary(pair => pair.Key,
            pair => geometry.Monitors[pair.Value].WorkArea, StringComparer.Ordinal);

    private static Dictionary<string, LogicalWorkArea> WorkAreasById(CardLayoutGeometrySnapshot geometry) =>
        geometry.Monitors.ToDictionary(pair => pair.Key, pair => pair.Value.WorkArea, StringComparer.Ordinal);

    private void UpdateTaskbarRepresentative()
    {
        var representative = TaskbarRepresentative();
        foreach (var window in _windows.Values)
            window.ShowInTaskbar = OperatingSystem.IsWindows() && ReferenceEquals(window, representative);
        // Avalonia can recreate a card's native owner while changing its taskbar role.
        // Reassert the configured z-order after that native change.
        ApplyAlwaysOnTop(_windows.Values, _settings.AlwaysOnTop);
        RaiseSettingsAboveCards("taskbar_representative_updated");
    }

    private ProviderUsageCardWindow? TaskbarRepresentative()
    {
        return _settings.EnabledProviderIds.Select(id => _windows.GetValueOrDefault(id)).FirstOrDefault(window => window is not null);
    }

    private void ApplyApplicationTheme()
    {
        SetApplicationTheme(Application.Current, _settings.ThemeMode);
    }

    internal static void SetApplicationTheme(Application? application, string mode)
    {
        if (application is null) return;
        application.RequestedThemeVariant = mode switch
        {
            "dark" => ThemeVariant.Dark,
            "light" => ThemeVariant.Light,
            _ => ThemeVariant.Default
        };
    }

    internal static void ApplyAlwaysOnTop(IEnumerable<ProviderUsageCardWindow> windows, bool enabled)
    {
        foreach (var window in windows)
        {
            window.Topmost = enabled;
            if (OperatingSystem.IsWindows() && window.IsVisible)
                Win32WindowRaiser.ApplyTopmost(window, enabled);
        }
    }
}
