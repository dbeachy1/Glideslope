using Tmds.DBus.Protocol;
using Glideslope.Core;

namespace Glideslope.App;

/// <summary>
/// Observes the StatusNotifier item that Avalonia already owns for this process.
/// </summary>
internal sealed class LinuxTrayMonitor : IAsyncDisposable
{
    private const string WatcherName = "org.kde.StatusNotifierWatcher";
    private const string WatcherPath = "/StatusNotifierWatcher";
    private const string WatcherInterface = "org.kde.StatusNotifierWatcher";
    private const string ItemInterface = "org.kde.StatusNotifierItem";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string IntrospectionInterface = "org.freedesktop.DBus.Introspectable";
    private const string BusName = "org.freedesktop.DBus";
    private const string BusPath = "/org/freedesktop/DBus";
    private const string BusInterface = "org.freedesktop.DBus";

    private readonly IDiagnosticSink _diagnostics;
    private readonly Action<bool, string> _viabilityChanged;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _itemPrefix = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-";
    private readonly TrayViabilityState _state = new();
    private DBusConnection? _connection;
    private IDisposable? _registered;
    private IDisposable? _unregistered;
    private Task? _ownerLoop;
    private bool _disposed;

    public LinuxTrayMonitor(IDiagnosticSink diagnostics, Action<bool, string> viabilityChanged)
    {
        _diagnostics = diagnostics;
        _viabilityChanged = viabilityChanged;
    }

    public bool IsUsable => _state.IsUsable;

    public void Start()
    {
        _ownerLoop = MonitorAsync();
    }

    private async Task MonitorAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            SetViability(false, "unsupported_platform");
            return;
        }

        var retryDelay = TimeSpan.FromSeconds(1);
        while (!_stop.IsCancellationRequested)
        {
            DBusConnection? connection = null;
            try
            {
                var sessionAddress = DBusAddress.Session;
                if (string.IsNullOrWhiteSpace(sessionAddress))
                {
                    SetViability(false, "session_bus_address_unavailable");
                    await Task.Delay(retryDelay, _stop.Token).ConfigureAwait(false);
                    continue;
                }

                var connectionOptions = new DBusConnectionOptions(sessionAddress)
                {
                    AutoConnect = false,
                    OnException = context => RecordFailure($"dbus_{context.Source}", context.Exception)
                };
                connection = new DBusConnection(connectionOptions);
                _connection = connection;
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await connection.ConnectAsync().AsTask().WaitAsync(connectTimeout.Token).ConfigureAwait(false);

                retryDelay = TimeSpan.FromSeconds(1);
                using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var monitorTask = MonitorConnectedSessionAsync(connection, connectionLifetime.Token);
                var disconnectedTask = connection.DisconnectedAsync();
                var completed = await Task.WhenAny(monitorTask, disconnectedTask).ConfigureAwait(false);
                if (completed == disconnectedTask)
                {
                    var exception = await disconnectedTask.ConfigureAwait(false);
                    if (!_stop.IsCancellationRequested)
                    {
                        RecordFailure("session_bus_disconnected", exception ?? new IOException("Session bus closed."));
                        SetViability(false, "session_bus_disconnected");
                    }

                    connectionLifetime.Cancel();
                    await ObserveMonitorTerminationAsync(monitorTask).ConfigureAwait(false);
                }
                else
                {
                    await monitorTask.ConfigureAwait(false);
                }

            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                RecordFailure("session_bus_monitor", exception);
                SetViability(false, "monitor_unavailable");
            }
            finally
            {
                if (ReferenceEquals(_connection, connection))
                {
                    _connection = null;
                }
                connection?.Dispose();
            }

            if (_stop.IsCancellationRequested)
            {
                break;
            }

            await Task.Delay(retryDelay, _stop.Token).ConfigureAwait(false);
            retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 10));
        }
    }

    private async Task MonitorConnectedSessionAsync(DBusConnection connection, CancellationToken connectionToken)
    {
        var stage = "watch_status_notifier_owner";
        try
        {
            var ownerWatcher = await connection.WatchNameOwnerAsync(WatcherName).ConfigureAwait(false);
            while (!connectionToken.IsCancellationRequested)
            {
                string owner;
                try
                {
                    stage = "wait_for_status_notifier_owner";
                    using var ownerTimeout = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
                    ownerTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                    owner = await ownerWatcher.WaitForOwnerAsync(ownerTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (connectionToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    SetViability(false, "watcher_unavailable");
                    continue;
                }

                stage = "observe_status_notifier_owner";
                await ObserveOwnerAsync(connection, ownerWatcher, owner, connectionToken).ConfigureAwait(false);
                SetViability(false, "watcher_owner_changed");
            }
        }
        catch (OperationCanceledException) when (connectionToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RecordFailure(stage, exception);
            throw;
        }
    }

    private static async Task ObserveMonitorTerminationAsync(Task monitorTask)
    {
        try
        {
            await monitorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // The disconnected task already records the reason; this join only ensures
            // no work remains on the old connection before it is disposed.
        }
    }

    private async Task ObserveOwnerAsync(DBusConnection connection, NameOwnerWatcher ownerWatcher, string owner, CancellationToken connectionToken)
    {
        var ownerBusName = NameOwnerWatcher.GetOwnerBusName(owner);
        var changedToken = ownerWatcher.GetOwnerChangedCancellationToken(owner);
        using var registrationCancellation = CancellationTokenSource.CreateLinkedTokenSource(connectionToken, changedToken);

        var stage = "register_item_registered_signal";
        try
        {
            _registered = await connection.WatchSignalAsync(
                ownerBusName,
                WatcherPath,
                WatcherInterface,
                "StatusNotifierItemRegistered",
                ReadString,
                notification =>
                {
                    if (notification.HasValue)
                    {
                        _ = ReevaluateAsync(connection, ownerBusName, registrationCancellation.Token, notification.Value);
                    }
                },
                ObserverFlags.EmitOnConnectionClosed,
                false,
                null).ConfigureAwait(false);

            stage = "register_item_unregistered_signal";
            _unregistered = await connection.WatchSignalAsync(
                ownerBusName,
                WatcherPath,
                WatcherInterface,
                "StatusNotifierItemUnregistered",
                ReadString,
                notification =>
                {
                    if (notification.HasValue)
                    {
                        _ = ReevaluateAsync(connection, ownerBusName, registrationCancellation.Token, notification.Value);
                    }
                },
                ObserverFlags.EmitOnConnectionClosed,
                false,
                null).ConfigureAwait(false);

            stage = "evaluate_registered_item";
            await ReevaluateAsync(connection, ownerBusName, registrationCancellation.Token, null).ConfigureAwait(false);
            stage = "wait_for_item_signal";
            while (!registrationCancellation.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), registrationCancellation.Token).ConfigureAwait(false);
                await ReevaluateAsync(connection, ownerBusName, registrationCancellation.Token, null).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (registrationCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RecordFailure(stage, exception);
            SetViability(false, "watcher_observe_failed");
        }
        finally
        {
            _registered?.Dispose();
            _registered = null;
            _unregistered?.Dispose();
            _unregistered = null;
        }
    }

    private async Task ReevaluateAsync(DBusConnection connection, string ownerBusName, CancellationToken cancellationToken, string? changedItem)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var stage = "read_status_notifier_host_property";
        try
        {
            var hostRegistered = await ReadBoolPropertyAsync(
                connection,
                ownerBusName,
                WatcherPath,
                WatcherInterface,
                "IsStatusNotifierHostRegistered",
                cancellationToken).ConfigureAwait(false);
            if (!hostRegistered)
            {
                SetViability(false, "status_notifier_host_unregistered");
                return;
            }

            stage = "read_registered_items_property";
            var registeredItems = await ReadStringArrayPropertyAsync(
                connection,
                ownerBusName,
                WatcherPath,
                WatcherInterface,
                "RegisteredStatusNotifierItems",
                cancellationToken).ConfigureAwait(false);

            foreach (var registration in registeredItems)
            {
                if (!TryParseOwnedRegistration(registration, Environment.ProcessId, out var service, out var itemPath))
                {
                    continue;
                }

                stage = "verify_owned_item_process_id";
                var processId = await CallAsync<uint>(
                    connection,
                    BusName,
                    BusPath,
                    BusInterface,
                    "GetConnectionUnixProcessID",
                    "s",
                    (ref MessageWriter writer) => writer.WriteString(service),
                    static (message, _) => message.GetBodyReader().ReadUInt32(),
                    cancellationToken).ConfigureAwait(false);
                if (processId != Environment.ProcessId)
                {
                    continue;
                }

                stage = "read_item_menu_property";
                var menuPath = await ReadObjectPathPropertyAsync(connection, service, itemPath, "Menu", cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(menuPath))
                {
                    continue;
                }

                stage = "read_show_exit_menu_layout";
                var menuVerified = await CallAsync<bool>(
                    connection,
                    service,
                    menuPath,
                    "com.canonical.dbusmenu",
                    "GetLayout",
                    "iias",
                    (ref MessageWriter writer) =>
                    {
                        writer.WriteInt32(0);
                        writer.WriteInt32(-1);
                        writer.WriteArray(Array.Empty<string>());
                    },
                    static (message, _) => ReadMenuLayout(message),
                    cancellationToken).ConfigureAwait(false);
                if (menuVerified)
                {
                    SetViability(true, "registered_item_and_menu_verified");
                    return;
                }
            }

            SetViability(false, changedItem is null ? "registered_item_unverified" : "registered_item_changed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RecordFailure(stage, exception);
            SetViability(false, "item_probe_failed");
        }
    }

    internal static bool TryParseOwnedRegistration(string registration, int processId, out string service, out string itemPath)
    {
        var separator = registration.IndexOf('/');
        service = separator < 0 ? registration : registration[..separator];
        itemPath = separator < 0 ? "/StatusNotifierItem" : registration[separator..];
        return service.StartsWith($"org.kde.StatusNotifierItem-{processId}-", StringComparison.Ordinal) &&
            itemPath.StartsWith("/", StringComparison.Ordinal);
    }

    internal static bool IsMenuVerified(string menuXml)
    {
        return menuXml.Contains("com.canonical.dbusmenu", StringComparison.Ordinal) &&
            menuXml.Contains("GetLayout", StringComparison.Ordinal);
    }

    internal static bool IsHostAndMenuVerifiedForSpecs(bool hostRegistered, bool menuVerified)
    {
        return hostRegistered && menuVerified;
    }

    private static async Task<bool> ReadBoolPropertyAsync(
        DBusConnection connection,
        string service,
        string path,
        string interfaceName,
        string property,
        CancellationToken cancellationToken)
    {
        var value = await ReadPropertyAsync(connection, service, path, interfaceName, property, cancellationToken).ConfigureAwait(false);
        return value.GetBool();
    }

    private static async Task<string[]> ReadStringArrayPropertyAsync(
        DBusConnection connection,
        string service,
        string path,
        string interfaceName,
        string property,
        CancellationToken cancellationToken)
    {
        var value = await ReadPropertyAsync(connection, service, path, interfaceName, property, cancellationToken).ConfigureAwait(false);
        return value.GetArray<string>();
    }

    private static async Task<VariantValue> ReadPropertyAsync(
        DBusConnection connection,
        string service,
        string path,
        string interfaceName,
        string property,
        CancellationToken cancellationToken)
    {
        return await CallAsync<VariantValue>(
            connection,
            service,
            path,
            PropertiesInterface,
            "Get",
            "ss",
            (ref MessageWriter writer) =>
            {
                writer.WriteString(interfaceName);
                writer.WriteString(property);
            },
            static (message, _) => message.GetBodyReader().ReadVariantValue(),
            cancellationToken).ConfigureAwait(false);
    }

    internal static bool ReadMenuLayout(Message message)
    {
        var reader = message.GetBodyReader();
        _ = reader.ReadUInt32();
        reader.AlignStruct();
        _ = reader.ReadInt32();
        _ = reader.ReadDictionaryOfStringToVariantValue();
        var children = reader.ReadArrayStart(DBusType.Variant);
        var labels = new HashSet<string>(StringComparer.Ordinal);
        while (reader.HasNext(children))
        {
            ReadMenuItem(reader.ReadVariantValue(), labels);
        }

        reader.SkipTo(children);
        return labels.Contains(LocalizedText.TrayShow) && labels.Contains(LocalizedText.TrayExit);
    }

    private static void ReadMenuItem(VariantValue item, ISet<string> labels)
    {
        if (item.Type != VariantValueType.Struct || item.Count < 3)
        {
            return;
        }

        var properties = item.GetItem(1).GetDictionary<string, VariantValue>();
        if (properties.TryGetValue("label", out var label) && label.Type == VariantValueType.String)
        {
            labels.Add(label.GetString());
        }

        var children = item.GetItem(2);
        if (children.Type != VariantValueType.Array)
        {
            return;
        }

        for (var index = 0; index < children.Count; index++)
        {
            ReadMenuItem(children.GetItem(index), labels);
        }
    }

    private static async Task<string> ReadObjectPathPropertyAsync(
        DBusConnection connection,
        string service,
        string path,
        string property,
        CancellationToken cancellationToken)
    {
        var value = await CallAsync<VariantValue>(
            connection,
            service,
            path,
            PropertiesInterface,
            "Get",
            "ss",
            (ref MessageWriter writer) =>
            {
                writer.WriteString(ItemInterface);
                writer.WriteString(property);
            },
            static (message, _) => message.GetBodyReader().ReadVariantValue(),
            cancellationToken).ConfigureAwait(false);
        return value.GetObjectPathAsString();
    }

    private static async Task<T> CallAsync<T>(
        DBusConnection connection,
        string destination,
        string path,
        string interfaceName,
        string member,
        string signature,
        MessageBodyWriter? writeBody,
        MessageValueReader<T> readBody,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = CreateCallMessage(connection, destination, path, interfaceName, member, signature, writeBody);
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await connection.CallMethodAsync(message, readBody, null).WaitAsync(requestTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Tmds has no per-call cancellation API. Close this connection so the
            // pending request is released, then let the outer loop reconnect cleanly.
            connection.Dispose();
            throw new TimeoutException("StatusNotifier D-Bus request timed out.");
        }
    }

    internal static MessageBuffer CreateCallMessage(
        DBusConnection connection,
        string destination,
        string path,
        string interfaceName,
        string member,
        string signature,
        MessageBodyWriter? writeBody)
    {
        MessageBuffer message;
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination, path, interfaceName, member, signature, MessageFlags.None);
            writeBody?.Invoke(ref writer);
            message = writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
        return message;
    }

    internal delegate void MessageBodyWriter(ref MessageWriter writer);

    private static string ReadString(Message message, object? state)
    {
        return message.GetBodyReader().ReadString();
    }

    private void SetViability(bool usable, string reason)
    {
        if (_state.Update(usable))
        {
            _diagnostics.Record(new DiagnosticEvent(
                usable ? "tray_viability_verified" : "tray_viability_lost",
                Status: reason));
            _viabilityChanged(usable, reason);
        }
    }

    private void RecordFailure(string stage, Exception exception)
    {
        // D-Bus exception messages can contain addresses or peer details. Record only the
        // fixed operation stage and exception type so a live failure remains diagnosable.
        var safeType = exception.GetType().Name;
        if (exception.InnerException is { } inner)
        {
            safeType += $"_inner_{inner.GetType().Name}";
        }
        _diagnostics.Record(new DiagnosticEvent(
            "tray_monitor_failed",
            Status: $"{stage}:{safeType}"));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        _registered?.Dispose();
        _unregistered?.Dispose();
        // Close the transport before joining the monitor so a pending Tmds request is
        // released promptly instead of making disposal wait for a bus reply timeout.
        _connection?.Dispose();
        if (_ownerLoop is not null)
        {
            try
            {
                await _ownerLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }
}

internal sealed class TrayViabilityState
{
    public bool IsUsable { get; private set; }

    public bool Update(bool isUsable)
    {
        if (IsUsable == isUsable)
        {
            return false;
        }

        IsUsable = isUsable;
        return true;
    }
}
