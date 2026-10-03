using System.Diagnostics;
using Glideslope.App;
using Glideslope.Core;
using Tmds.DBus.Protocol;

namespace Glideslope.Shell.Specs;

/// <summary>
/// Exercises the production tray monitor against a private dbus-daemon and synthetic
/// StatusNotifier services. It does not start Avalonia or touch the user's desktop bus.
/// </summary>
internal static class LinuxTrayLifecycleProof
{
    private const string WatcherName = "org.kde.StatusNotifierWatcher";
    private const string WatcherPath = "/StatusNotifierWatcher";
    private const string WatcherInterface = "org.kde.StatusNotifierWatcher";
    private const string ItemInterface = "org.kde.StatusNotifierItem";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string MenuPath = "/StatusNotifierItem/Menu";

    public static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("linux_tray_lifecycle_proof_requires_linux=yes");
            return 2;
        }

        var previousAddress = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        var diagnostics = new RecordingDiagnosticSink();
        var tempRoot = Path.Combine(Path.GetTempPath(), $"glideslope-tray-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        await using var monitor = new LinuxTrayMonitor(diagnostics, static (_, _) => { });
        PrivateBus? bus = null;
        FakeWatcher? watcher = null;
        FakeItem? item = null;
        var phase = "private_bus_start";
        try
        {
            var address = $"unix:abstract=glideslope_tray_lifecycle_{Guid.NewGuid():N}";
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", address);
            bus = await PrivateBus.StartAsync(address).ConfigureAwait(false);
            phase = "fake_item_register";
            var registration = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-0/StatusNotifierItem";
            watcher = await FakeWatcher.StartAsync(address, [registration]).ConfigureAwait(false);
            item = await FakeItem.StartAsync(address, registration[..registration.IndexOf('/')]).ConfigureAwait(false);
            await AssertWatcherOwnerPresentAsync(address).ConfigureAwait(false);
            await AssertFixtureRegistrationAsync(address, registration).ConfigureAwait(false);
            await AssertFixtureMenuAsync(address, registration[..registration.IndexOf('/')]).ConfigureAwait(false);

            phase = "initial_viability";
            monitor.Start();
            await diagnostics.WaitForAsync(
                static item => item.Code == "tray_viability_verified" && item.Status == "registered_item_and_menu_verified",
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            phase = "watcher_owner_loss";
            var ownerLossEventCount = diagnostics.Count;
            await watcher.ReleaseNameAsync().ConfigureAwait(false);
            await diagnostics.WaitForAfterAsync(ownerLossEventCount,
                static item => item.Code == "tray_viability_lost" && item.Status == "watcher_owner_changed",
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            phase = "watcher_owner_recovery";
            var ownerRecoveryEventCount = diagnostics.Count;
            await watcher.AcquireNameAsync().ConfigureAwait(false);
            await diagnostics.WaitForAfterAsync(ownerRecoveryEventCount,
                static item => item.Code == "tray_viability_verified" && item.Status == "registered_item_and_menu_verified",
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Console.WriteLine("owner_loss_reverified_same_registered_item_pid_menu=yes");

            phase = "pending_read_disposal";
            await watcher.BlockNextPropertyReadAsync().ConfigureAwait(false);
            await watcher.WaitForBlockedReadAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var disposeStarted = Stopwatch.GetTimestamp();
            // The elapsed-time check below verifies the monitor's disposal promptness.
            await monitor.DisposeAsync().AsTask().ConfigureAwait(false);
            var disposeElapsed = Stopwatch.GetElapsedTime(disposeStarted);
            if (disposeElapsed >= TimeSpan.FromSeconds(3)) throw new TimeoutException("pending_read_disposal_exceeded_bound");
            watcher.ReleaseBlockedRead();
            await watcher.WaitForBlockedReadCompletionAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            Console.WriteLine($"pending_read_disposal_bounded={(disposeElapsed < TimeSpan.FromSeconds(3)).ToString().ToLowerInvariant()}");

            await item.DisposeAsync().ConfigureAwait(false);
            item = null;
            await watcher.DisposeAsync().ConfigureAwait(false);
            watcher = null;
            await bus.DisposeAsync().ConfigureAwait(false);
            bus = null;

            // Run a fresh monitor against a daemon that is deliberately made to die and
            // return at the same private address. No real desktop host is involved.
            phase = "connection_recovery_setup";
            var recoveryDiagnostics = new RecordingDiagnosticSink();
            await using var recoveryMonitor = new LinuxTrayMonitor(recoveryDiagnostics, static (_, _) => { });
            bus = await PrivateBus.StartAsync(address).ConfigureAwait(false);
            watcher = await FakeWatcher.StartAsync(address, [registration]).ConfigureAwait(false);
            item = await FakeItem.StartAsync(address, registration[..registration.IndexOf('/')]).ConfigureAwait(false);
            recoveryMonitor.Start();
            await recoveryDiagnostics.WaitForAsync(
                static entry => entry.Code == "tray_viability_verified" && entry.Status == "registered_item_and_menu_verified",
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            phase = "private_bus_disconnect";
            var busLossEventCount = recoveryDiagnostics.Count;
            await bus.StopAsync().ConfigureAwait(false);
            await recoveryDiagnostics.WaitForAfterAsync(busLossEventCount,
                static entry => entry.Code == "tray_viability_lost",
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await watcher.DisposeAsync().ConfigureAwait(false);
            watcher = null;
            await item.DisposeAsync().ConfigureAwait(false);
            item = null;

            phase = "private_bus_reconnect";
            var reconnectEventCount = recoveryDiagnostics.Count;
            bus = await PrivateBus.StartAsync(address).ConfigureAwait(false);
            watcher = await FakeWatcher.StartAsync(address, [registration]).ConfigureAwait(false);
            item = await FakeItem.StartAsync(address, registration[..registration.IndexOf('/')]).ConfigureAwait(false);
            await recoveryDiagnostics.WaitForAfterAsync(reconnectEventCount,
                static entry => entry.Code == "tray_viability_verified" && entry.Status == "registered_item_and_menu_verified",
                TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            Console.WriteLine("private_bus_disconnect_reconnected_same_registered_item_pid_menu=yes");
            Console.WriteLine("linux_tray_lifecycle_proof=passed");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"linux_tray_lifecycle_proof_failed={phase}:{exception.GetType().Name}");
            Console.Error.WriteLine($"diagnostic_summary={diagnostics.SafeSummary}");
            return 1;
        }
        finally
        {
            try { await monitor.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
            if (watcher is not null)
            {
                watcher.ReleaseBlockedRead();
                await watcher.DisposeAsync().ConfigureAwait(false);
            }
            if (item is not null) await item.DisposeAsync().ConfigureAwait(false);
            if (bus is not null) await bus.DisposeAsync().ConfigureAwait(false);
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", previousAddress);
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
            if (Directory.Exists(tempRoot)) throw new IOException("owned_tray_lifecycle_temp_root_survived_cleanup");
        }
    }

    private sealed class PrivateBus : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly DateTime _startedAt;
        private bool _stopped;

        private PrivateBus(Process process)
        {
            _process = process;
            _startedAt = process.StartTime.ToUniversalTime();
        }

        public static async Task<PrivateBus> StartAsync(string address)
        {
            var start = new ProcessStartInfo("dbus-daemon")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            start.ArgumentList.Add("--session");
            start.ArgumentList.Add("--nofork");
            start.ArgumentList.Add($"--address={address}");
            // dbus-daemon prints its bound address once it is listening; use that signal before connecting.
            start.ArgumentList.Add("--print-address");
            var process = Process.Start(start) ?? throw new InvalidOperationException("private_bus_start_failed");
            var bus = new PrivateBus(process);
            try
            {
                var printedAddress = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (process.HasExited) throw new InvalidOperationException("private_bus_exited_during_start");
                if (string.IsNullOrEmpty(printedAddress)) throw new InvalidOperationException("private_bus_did_not_print_address");
                Console.Error.WriteLine($"private dbus-daemon ready: pid={process.Id} printed_address_length={printedAddress.Length}");
                var connection = await ConnectAsync(address).ConfigureAwait(false);
                connection.Dispose();
                return bus;
            }
            catch
            {
                await bus.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async Task StopAsync()
        {
            if (_stopped) return;
            _stopped = true;
            if (!_process.HasExited && _process.StartTime.ToUniversalTime() == _startedAt)
            {
                _process.Kill(entireProcessTree: false);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
            _process.Dispose();
        }

        public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
    }

    private sealed class FakeWatcher : IAsyncDisposable
    {
        private readonly DBusConnection _connection;
        private readonly string[] _registrations;
        private readonly TaskCompletionSource _blockedReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _blockedReadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _blockedReadCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blockNextRead;

        private FakeWatcher(DBusConnection connection, string[] registrations)
        {
            _connection = connection;
            _registrations = registrations;
            connection.AddMethodHandler(new WatcherPropertiesHandler(this));
        }

        public static async Task<FakeWatcher> StartAsync(string address, string[] registrations)
        {
            var connection = await ConnectAsync(address).ConfigureAwait(false);
            var watcher = new FakeWatcher(connection, registrations);
            await connection.RequestNameAsync(WatcherName, RequestNameOptions.None).ConfigureAwait(false);
            return watcher;
        }

        public async Task ReleaseNameAsync() => _ = await _connection.ReleaseNameAsync(WatcherName).ConfigureAwait(false);
        public async Task AcquireNameAsync() => await _connection.RequestNameAsync(WatcherName, RequestNameOptions.None).ConfigureAwait(false);

        public Task BlockNextPropertyReadAsync()
        {
            Interlocked.Exchange(ref _blockNextRead, 1);
            return Task.CompletedTask;
        }

        public Task WaitForBlockedReadAsync(TimeSpan timeout) => _blockedReadStarted.Task.WaitAsync(timeout);
        public Task WaitForBlockedReadCompletionAsync(TimeSpan timeout) => _blockedReadCompleted.Task.WaitAsync(timeout);
        public void ReleaseBlockedRead() => _blockedReadRelease.TrySetResult();

        private async ValueTask HandlePropertiesAsync(MethodContext context)
        {
            var reader = context.Request.GetBodyReader();
            var interfaceName = reader.ReadString();
            var property = reader.ReadString();
            if (interfaceName != WatcherInterface || property is not ("IsStatusNotifierHostRegistered" or "RegisteredStatusNotifierItems"))
            {
                context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", "unknown_property");
                return;
            }

            if (Interlocked.Exchange(ref _blockNextRead, 0) == 1)
            {
                context.DisposesAsynchronously = true;
                try
                {
                    _blockedReadStarted.TrySetResult();
                    await _blockedReadRelease.Task.ConfigureAwait(false);
                }
                finally
                {
                    _blockedReadCompleted.TrySetResult();
                    context.Dispose();
                }
                return;
            }

            using var writer = context.CreateReplyWriter("v");
            if (property == "IsStatusNotifierHostRegistered") writer.WriteVariantBool(true);
            else writer.WriteVariant(VariantValue.Array(_registrations));
            context.Reply(writer.CreateMessage());
        }

        public async ValueTask DisposeAsync()
        {
            _blockedReadRelease.TrySetResult();
            _connection.Dispose();
            try { await _connection.DisconnectedAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }

        private sealed class WatcherPropertiesHandler(FakeWatcher watcher) : IPathMethodHandler
        {
            public string Path => WatcherPath;
            public bool HandlesChildPaths => false;
            public async ValueTask HandleMethodAsync(MethodContext context)
            {
                if (context.IsPropertiesInterfaceRequest && context.Request.MemberAsString == "Get")
                {
                    await watcher.HandlePropertiesAsync(context).ConfigureAwait(false);
                    return;
                }
                context.ReplyUnknownMethodError();
            }
        }
    }

    private sealed class FakeItem : IAsyncDisposable
    {
        private readonly DBusConnection _connection;

        private FakeItem(DBusConnection connection, string serviceName)
        {
            _connection = connection;
            connection.AddMethodHandler(new ItemPropertiesHandler());
            connection.AddMethodHandler(new MenuHandler());
        }

        public static async Task<FakeItem> StartAsync(string address, string serviceName)
        {
            var connection = await ConnectAsync(address).ConfigureAwait(false);
            var item = new FakeItem(connection, serviceName);
            await connection.RequestNameAsync(serviceName, RequestNameOptions.None).ConfigureAwait(false);
            return item;
        }

        public async ValueTask DisposeAsync()
        {
            _connection.Dispose();
            try { await _connection.DisconnectedAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }

        private sealed class ItemPropertiesHandler : IPathMethodHandler
        {
            public string Path => "/StatusNotifierItem";
            public bool HandlesChildPaths => false;
            public ValueTask HandleMethodAsync(MethodContext context)
            {
                if (!context.IsPropertiesInterfaceRequest || context.Request.MemberAsString != "Get")
                {
                    context.ReplyUnknownMethodError();
                    return ValueTask.CompletedTask;
                }
                var reader = context.Request.GetBodyReader();
                if (reader.ReadString() != ItemInterface || reader.ReadString() != "Menu")
                {
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", "unknown_property");
                    return ValueTask.CompletedTask;
                }
                using var writer = context.CreateReplyWriter("v");
                writer.WriteVariantObjectPath(MenuPath);
                context.Reply(writer.CreateMessage());
                return ValueTask.CompletedTask;
            }
        }

        private sealed class MenuHandler : IPathMethodHandler
        {
            public string Path => MenuPath;
            public bool HandlesChildPaths => false;
            public ValueTask HandleMethodAsync(MethodContext context)
            {
                if (context.Request.InterfaceAsString != "com.canonical.dbusmenu" || context.Request.MemberAsString != "GetLayout")
                {
                    context.ReplyUnknownMethodError();
                    return ValueTask.CompletedTask;
                }

                using var writer = context.CreateReplyWriter("u(ia{sv}av)");
                writer.WriteUInt32(1);
                writer.WriteStructureStart();
                writer.WriteInt32(0);
                writer.WriteDictionary(Array.Empty<KeyValuePair<string, VariantValue>>());
                writer.WriteArray((IEnumerable<VariantValue>)
                [
                    CreateMenuItem(1, LocalizedText.TrayShow),
                    CreateMenuItem(2, LocalizedText.TrayExit)
                ]);
                context.Reply(writer.CreateMessage());
                return ValueTask.CompletedTask;
            }

            private static VariantValue CreateMenuItem(int id, string label)
            {
                var properties = new Dict<string, VariantValue>
                {
                    ["label"] = VariantValue.String(label)
                };
                return VariantValue.Struct(
                    VariantValue.Int32(id),
                    properties,
                    VariantValue.ArrayOfVariant(Array.Empty<VariantValue>()));
            }
        }
    }

    private static async Task<DBusConnection> ConnectAsync(string address)
    {
        var connection = new DBusConnection(new DBusConnectionOptions(address) { AutoConnect = false });
        try
        {
            await connection.ConnectAsync().AsTask().ConfigureAwait(false);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static async Task AssertWatcherOwnerPresentAsync(string address)
    {
        var connection = await ConnectAsync(address).ConfigureAwait(false);
        try
        {
            var watcher = await connection.WatchNameOwnerAsync(WatcherName).ConfigureAwait(false);
            _ = await watcher.WaitForOwnerAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            connection.Dispose();
        }
    }

    private static async Task AssertFixtureRegistrationAsync(string address, string registration)
    {
        if (!LinuxTrayMonitor.TryParseOwnedRegistration(registration, Environment.ProcessId, out var service, out _))
            throw new InvalidOperationException("fixture_registration_did_not_match_current_process");
        var connection = await ConnectAsync(address).ConfigureAwait(false);
        try
        {
            var writer = connection.GetMessageWriter();
            MessageBuffer message;
            try
            {
                writer.WriteMethodCallHeader(WatcherName, WatcherPath, PropertiesInterface, "Get", "ss", MessageFlags.None);
                writer.WriteString(WatcherInterface);
                writer.WriteString("RegisteredStatusNotifierItems");
                message = writer.CreateMessage();
            }
            finally { writer.Dispose(); }
            var value = await connection.CallMethodAsync(
                message,
                static (reply, _) => reply.GetBodyReader().ReadVariantValue(),
                null).ConfigureAwait(false);
            var items = value.GetArray<string>();
            if (!items.Contains(registration, StringComparer.Ordinal))
                throw new InvalidOperationException("fixture_registration_not_returned_by_watcher");

            writer = connection.GetMessageWriter();
            try
            {
                writer.WriteMethodCallHeader(
                    "org.freedesktop.DBus",
                    "/org/freedesktop/DBus",
                    "org.freedesktop.DBus",
                    "GetConnectionUnixProcessID",
                    "s",
                    MessageFlags.None);
                writer.WriteString(service);
                message = writer.CreateMessage();
            }
            finally { writer.Dispose(); }
            var processId = await connection.CallMethodAsync(
                message,
                static (reply, _) => reply.GetBodyReader().ReadUInt32(),
                null).ConfigureAwait(false);
            if (processId != Environment.ProcessId)
                throw new InvalidOperationException("fixture_item_pid_did_not_match_test_process");
        }
        finally { connection.Dispose(); }
    }

    private static async Task AssertFixtureMenuAsync(string address, string serviceName)
    {
        var connection = await ConnectAsync(address).ConfigureAwait(false);
        try
        {
            var message = LinuxTrayMonitor.CreateCallMessage(
                connection,
                serviceName,
                MenuPath,
                "com.canonical.dbusmenu",
                "GetLayout",
                "iias",
                (ref MessageWriter writer) =>
                {
                    writer.WriteInt32(0);
                    writer.WriteInt32(-1);
                    writer.WriteArray(Array.Empty<string>());
                });
            var shape = await connection.CallMethodAsync(
                message,
                static (reply, _) => ReadMenuLayoutShape(reply),
                null).ConfigureAwait(false);
            message = LinuxTrayMonitor.CreateCallMessage(
                connection,
                serviceName,
                MenuPath,
                "com.canonical.dbusmenu",
                "GetLayout",
                "iias",
                (ref MessageWriter writer) =>
                {
                    writer.WriteInt32(0);
                    writer.WriteInt32(-1);
                    writer.WriteArray(Array.Empty<string>());
                });
            var menuVerified = await connection.CallMethodAsync(
                message,
                static (reply, _) => LinuxTrayMonitor.ReadMenuLayout(reply),
                null).ConfigureAwait(false);
            if (!menuVerified) throw new InvalidOperationException("fixture_menu_layout_not_accepted_by_production_parser");
        }
        finally { connection.Dispose(); }
    }

    private static string ReadMenuLayoutShape(Message message)
    {
        var reader = message.GetBodyReader();
        _ = reader.ReadUInt32();
        reader.AlignStruct();
        _ = reader.ReadInt32();
        var rootProperties = reader.ReadDictionaryOfStringToVariantValue();
        var children = reader.ReadArrayStart(DBusType.Variant);
        var count = 0;
        var first = "none";
        while (reader.HasNext(children))
        {
            var item = reader.ReadVariantValue();
            if (count == 0 && item.Type == VariantValueType.Struct && item.Count >= 3)
            {
                var properties = item.GetItem(1).GetDictionary<string, VariantValue>();
                var labelType = properties.TryGetValue("label", out var label) ? label.Type.ToString() : "missing";
                var unwrappedType = label.Type == VariantValueType.Variant ? label.GetVariantValue().Type.ToString() : labelType;
                first = $"{item.Type}:properties={properties.Count}:label={labelType}:inner={unwrappedType}:children={item.GetItem(2).Type}";
            }
            count++;
        }
        reader.SkipTo(children);
        return $"root_properties={rootProperties.Count};children={count};first={first}";
    }

    /// <summary>Record signals waiters as soon as a matching diagnostic arrives. WaitForAsync and
    /// WaitForAfterAsync use caller-supplied bounds around those signals.</summary>
    private sealed class RecordingDiagnosticSink : IDiagnosticSink
    {
        private readonly object _gate = new();
        private readonly List<DiagnosticEvent> _events = [];
        private readonly List<(Func<DiagnosticEvent, bool> Predicate, TaskCompletionSource<DiagnosticEvent> Signal)> _waiters = [];

        public void Record(DiagnosticEvent diagnostic)
        {
            List<TaskCompletionSource<DiagnosticEvent>> fire = [];
            lock (_gate)
            {
                _events.Add(diagnostic);
                for (var index = _waiters.Count - 1; index >= 0; index--)
                {
                    if (!_waiters[index].Predicate(diagnostic)) continue;
                    fire.Add(_waiters[index].Signal);
                    _waiters.RemoveAt(index);
                }
            }

            foreach (var signal in fire) signal.TrySetResult(diagnostic);
        }

        public int Count { get { lock (_gate) return _events.Count; } }
        public string SafeSummary { get { lock (_gate) return string.Join(',', _events.Select(item => $"{item.Code}:{item.Status ?? "none"}")); } }

        public Task<DiagnosticEvent> WaitForAsync(Func<DiagnosticEvent, bool> predicate, TimeSpan timeout)
            => WaitForAfterAsync(0, predicate, timeout);

        public Task<DiagnosticEvent> WaitForAfterAsync(int startIndex, Func<DiagnosticEvent, bool> predicate, TimeSpan timeout)
        {
            Task<DiagnosticEvent> pending;
            lock (_gate)
            {
                for (var index = startIndex; index < _events.Count; index++)
                    if (predicate(_events[index])) return Task.FromResult(_events[index]);
                var signal = new TaskCompletionSource<DiagnosticEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((predicate, signal));
                pending = signal.Task;
            }

            return AwaitWithBoundAsync(pending, timeout);
        }

        private static async Task<DiagnosticEvent> AwaitWithBoundAsync(Task<DiagnosticEvent> signal, TimeSpan timeout)
        {
            try
            {
                return await signal.WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("expected_sanitized_tray_transition_not_observed");
            }
        }
    }
}
