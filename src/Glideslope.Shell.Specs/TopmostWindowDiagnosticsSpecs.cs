using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Glideslope.App;
using Glideslope.Core;

namespace Glideslope.Shell.Specs;

internal static class TopmostWindowDiagnosticsSpecs
{
    public static void Run()
    {
        AppBuilder.Configure<HeadlessTestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        var window = new Window { ShowInTaskbar = true };
        var diagnostics = new RecordingSink();
        var observer = new TopmostWindowDiagnostics(window, "codex", diagnostics);

        observer.ObserveStyle(0x00000008, "message=WM_WINDOWPOSCHANGED");
        Assert(diagnostics.Events.Count == 1 && diagnostics.Events[0].Code == "window_topmost_native_snapshot",
            "first native style observation records one initial snapshot");
        var snapshot = diagnostics.Events[0].Status ?? throw new InvalidOperationException("snapshot status missing");
        Assert(snapshot.Contains("nativeTopmost=True", StringComparison.Ordinal) &&
               snapshot.Contains("taskbarRole=representative", StringComparison.Ordinal) &&
               snapshot.Contains("owner=none", StringComparison.Ordinal) &&
               snapshot.Contains("nativeOwner=unavailable", StringComparison.Ordinal),
            "snapshot includes native state, card role, and owner context when no native handle is available");

        observer.ObserveStyle(0x00000008, "message=WM_WINDOWPOSCHANGED");
        observer.ObserveStyle(0x00000000, "message=WM_WINDOWPOSCHANGED");
        observer.ObserveStyle(0x00000000, "message=WM_WINDOWPOSCHANGED");
        Assert(diagnostics.Events.Count == 2 && diagnostics.Events[1].Code == "window_topmost_native_lost",
            "native topmost loss is recorded once per actual flag transition");
        var lossStatus = diagnostics.Events[1].Status ?? throw new InvalidOperationException("loss status missing");
        Assert(lossStatus.Contains(" stack=", StringComparison.Ordinal) &&
               lossStatus[(lossStatus.IndexOf(" stack=", StringComparison.Ordinal) + 7)..].Split('|').Length <= 12,
            "actual topmost loss carries bounded managed method attribution");

        var handled = false;
        observer.OnWindowMessage(IntPtr.Zero, 0x0005, new IntPtr(1), IntPtr.Zero, ref handled);
        Assert(!handled && diagnostics.Events.Count == 2, "WM_SIZE updates context without being handled or logged");
        observer.ObserveStyle(0x00000008, "message=WM_WINDOWPOSCHANGED");
        observer.ObserveStyleChangeRequest(0x00000008, 0x00000000);
        var clearRequest = diagnostics.Events[^1];
        var clearStatus = clearRequest.Status ?? throw new InvalidOperationException("clear request status missing");
        Assert(clearRequest.Code == "window_topmost_style_clear_requested" &&
               clearStatus.Contains("oldExStyle=0x00000008", StringComparison.Ordinal) &&
               clearStatus.Contains("newExStyle=0x00000000", StringComparison.Ordinal) &&
               clearStatus.Contains("nativeSizeState=minimized", StringComparison.Ordinal),
            "an explicit clear request is logged even between native style samples with current size context");
        var stack = clearStatus[(clearStatus.IndexOf(" stack=", StringComparison.Ordinal) + 7)..];
        Assert(stack.Split('|').Length <= 12 && !stack.Contains('/') && !stack.Contains('\\'),
            "clear-request attribution is bounded to method names and contains no paths");

        handled = false;
        observer.OnWindowMessage(IntPtr.Zero, 0x7FFF, IntPtr.Zero, IntPtr.Zero, ref handled);
        Assert(!handled, "unrelated native messages remain unhandled");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RecordingSink : IDiagnosticSink
    {
        public List<DiagnosticEvent> Events { get; } = [];
        public void Record(DiagnosticEvent diagnostic) => Events.Add(diagnostic);
    }

    private sealed class HeadlessTestApp : Application { }
}
