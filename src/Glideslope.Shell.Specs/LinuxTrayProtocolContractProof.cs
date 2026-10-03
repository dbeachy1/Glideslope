using Glideslope.App;
using System.Buffers;
using System.Reflection;
using System.Text;
using Tmds.DBus.Protocol;

namespace Glideslope.Shell.Specs;

/// <summary>
/// Protocol-level fixture for the StatusNotifier property and menu contract.
/// It rejects the old method-shaped watcher request instead of teaching a fake
/// watcher to accept the wrong API.
/// </summary>
internal static class LinuxTrayProtocolContractProof
{
    public static void Run()
    {
        var fixture = new StatusNotifierFixture();
        AssertThrows(() => fixture.CallWatcherMethod("GetRegisteredStatusNotifierItems"), "method_shaped_registered_items_request_must_fail");
        Assert(fixture.GetWatcherProperty("RegisteredStatusNotifierItems").Length == 2, "registered_items_property_should_be_readable");
        Assert(LinuxTrayMonitor.IsHostAndMenuVerifiedForSpecs(true, true), "host_and_menu_should_verify");
        Assert(!LinuxTrayMonitor.IsHostAndMenuVerifiedForSpecs(false, true), "host_loss_must_drop_viability");
        Assert(!LinuxTrayMonitor.IsHostAndMenuVerifiedForSpecs(true, false), "menu_loss_must_drop_viability");
        AssertCallBodyIsSerialized();
    }

    private static void AssertCallBodyIsSerialized()
    {
        using var connection = new DBusConnection(new DBusConnectionOptions("unix:path=/unused"));
        const string marker = "GlideslopeTrayCallBodySerializationProbe";
        var message = LinuxTrayMonitor.CreateCallMessage(
            connection,
            "org.example.TrayProbe",
            "/TrayProbe",
            "org.example.TrayProbe",
            "Probe",
            "s",
            (ref MessageWriter writer) => writer.WriteString(marker));

        // Tmds.DBus.Protocol 0.94.1 exposes no public byte view for MessageBuffer.
        // Inspect only its backing sequence so this test verifies actual body bytes,
        // rather than merely asserting that the callback delegate has a ref parameter.
        var data = typeof(MessageBuffer)
            .GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(message)!;
        var serialized = (ReadOnlySequence<byte>)data.GetType()
            .GetProperty("AsReadOnlySequence")!
            .GetValue(data)!;
        var bytes = serialized.ToArray();
        var bodyMarker = Encoding.UTF8.GetBytes(marker);
        Assert(bytes.AsSpan().IndexOf(bodyMarker) >= 0, "call_body_callback_must_write_into_serialized_message");
    }

    private static void AssertThrows(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException($"FAIL: {message}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {message}");
    }

    private sealed class StatusNotifierFixture
    {
        private readonly string[] _registeredItems =
        [
            "org.kde.StatusNotifierItem-42-0/StatusNotifierItem",
            "org.kde.StatusNotifierItem-77-0/StatusNotifierItem",
        ];

        public string[] GetWatcherProperty(string property)
        {
            if (!string.Equals(property, "RegisteredStatusNotifierItems", StringComparison.Ordinal))
                throw new InvalidOperationException("unknown_watcher_property");

            return _registeredItems.ToArray();
        }

        public void CallWatcherMethod(string member)
        {
            if (string.Equals(member, "GetRegisteredStatusNotifierItems", StringComparison.Ordinal))
                throw new InvalidOperationException("watcher_registered_items_is_property");

            throw new InvalidOperationException("unknown_watcher_method");
        }
    }
}
