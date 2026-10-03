using System.Runtime.InteropServices;
using Glideslope.App;
using Glideslope.Core;

namespace Glideslope.Shell.Specs;

/// <summary>
/// Integration proof for the actual WindowsTrayService receiver window.
/// The shell owner supplies the live service instance; this helper creates no
/// window, icon, desktop, or synthetic production substitute.
/// </summary>
internal static class WindowsTrayReceiverProof
{
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_APPWINDOW = 0x00040000;
    private const uint WS_CHILD = 0x40000000;
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const uint GA_ROOT = 2;

    /// <summary>
    /// Verifies the live service HWND and posts a unique registered message to
    /// that task-owned recipient. A scoped post proves the message can target
    /// the receiver without broadcasting to Explorer or unrelated windows.
    /// The production procedure has no test-only response contract, so this
    /// does not claim to observe an application callback for the arbitrary
    /// message; callback behavior remains covered by the tray callback path.
    /// </summary>
    public static void Run(WindowsTrayService tray, Func<IReadOnlyList<DiagnosticEvent>> recordedEvents)
    {
        if (!OperatingSystem.IsWindows()) return;

        var window = tray.NativeWindowHandle;
        Assert(window != 0, "tray_receiver_handle_missing");
        Assert(GetParent(window) == 0, "tray_receiver_must_be_unowned");
        Assert(GetAncestor(window, GA_ROOT) == window, "tray_receiver_must_be_top_level");
        Assert(!IsWindowVisible(window), "tray_receiver_must_start_hidden");

        var style = unchecked((uint)GetWindowLongPtr(window, GWL_STYLE).ToInt64());
        var exStyle = unchecked((uint)GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64());
        Assert((style & WS_POPUP) != 0, "tray_receiver_must_use_popup_style");
        Assert((style & WS_CHILD) == 0, "tray_receiver_must_not_be_child_window");
        Assert((exStyle & WS_EX_TOOLWINDOW) != 0, "tray_receiver_must_be_tool_window");
        Assert((exStyle & WS_EX_APPWINDOW) == 0, "tray_receiver_must_not_be_app_window");

        Assert(tray.IsUsable, "native_tray_registration_should_be_verified_for_taskbar_re_registration_proof");
        // Successful re-registration restores the icon without reporting a loss of tray viability.
        var lossObserved = false;
        EventHandler<TrayViabilityChangedEventArgs> viabilityChanged = (_, args) =>
        {
            if (!args.IsUsable) lossObserved = true;
        };
        tray.ViabilityChanged += viabilityChanged;
        try
        {
            Assert(tray.TaskbarCreatedMessageForSpecs != 0, "taskbar_created_message_missing");
            var before = recordedEvents().Count;
            _ = SendMessage(window, tray.TaskbarCreatedMessageForSpecs, 0, 0);
            var after = recordedEvents().Skip(before).ToList();
            Assert(after.Any(e => e.Code == "tray_taskbar_recreated"), "taskbar_re_registration_handler_not_reached");
            Assert(after.Any(e => e.Code == "tray_registration_verified"), "taskbar_re_registration_should_add_the_icon_again");
            Assert(!lossObserved, "a_successful_taskbar_re_registration_must_not_report_the_tray_lost");
            Assert(tray.IsUsable, "taskbar_re_registration_should_keep_viability");
        }
        finally
        {
            tray.ViabilityChanged -= viabilityChanged;
        }

        var message = RegisterWindowMessage($"Glideslope.Shell.Specs.Message.{Environment.ProcessId}.{Guid.NewGuid():N}");
        Assert(message != 0, "tray_receiver_proof_message_registration_failed");
        Assert(PostMessage(window, message, 0, 0), "tray_receiver_proof_message_post_failed");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetParent(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindowVisible(nint window);
}
