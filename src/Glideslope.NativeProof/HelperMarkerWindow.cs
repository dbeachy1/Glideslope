namespace Glideslope.NativeProof;

/// <summary>
/// A minimal native window created on the hidden desktop, used only as a z-order marker for
/// BringToFrontScenario:
/// activating one card raises all three above a helper window. It runs its own message loop on a
/// dedicated thread that is attached to the hidden desktop via SetThreadDesktop, since window creation
/// (and the messages later sent to activate a card) must happen from a thread on that desktop.
/// </summary>
internal sealed class HelperMarkerWindow : IDisposable
{
    private const string ClassName = "GlideslopeNativeProofHelperMarker";
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly NativeMethods.WndProc _wndProcDelegate;
    private readonly Action<string> _log;
    private nint _hwnd;
    private nint _instance;
    private string? _startupFailure;

    public HelperMarkerWindow(nint hiddenDesktop, Action<string> log)
    {
        _log = log;
        _wndProcDelegate = WndProc;
        _thread = new Thread(() => Run(hiddenDesktop, log)) { IsBackground = true, Name = "helper-marker-window" };
        // SetThreadDesktop must run before the thread acquires USER state. STA startup initializes COM and
        // attaches the thread to the current desktop, so use MTA; this helper does not use COM.
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
        {
            _startupFailure ??= "helper_thread_startup_timed_out";
            log("helper_marker_window_startup_timed_out waitSeconds=10");
        }
    }

    public nint Handle => _hwnd;
    public string? StartupFailure => _startupFailure;

    private void Run(nint hiddenDesktop, Action<string> log)
    {
        // This must be the thread's very first action. SetThreadDesktop fails with ERROR_BUSY (170) once
        // the calling thread owns any window, hook or USER state on its current desktop, so it has to run
        // before the window class is registered, the window is created or any message-loop call is made
        // on this thread (and the thread must not be STA; see the constructor). Only this brand-new
        // thread switches desktops; the proof's main thread stays where it is.
        if (!NativeMethods.SetThreadDesktop(hiddenDesktop))
        {
            _startupFailure = $"set_thread_desktop_failed_win32_{System.Runtime.InteropServices.Marshal.GetLastWin32Error()}";
            _ready.Set();
            return;
        }

        log($"helper_marker_thread_desktop_set threadId={Environment.CurrentManagedThreadId} apartment={Thread.CurrentThread.GetApartmentState()}");

        _instance = NativeMethods.GetModuleHandleW(null);
        var windowClass = new NativeMethods.WNDCLASSEXW
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = _instance,
            lpszClassName = ClassName,
        };
        if (NativeMethods.RegisterClassExW(ref windowClass) == 0)
        {
            _startupFailure = $"register_class_failed_win32_{System.Runtime.InteropServices.Marshal.GetLastWin32Error()}";
            _ready.Set();
            return;
        }

        _hwnd = NativeMethods.CreateWindowExW(0, ClassName, "Glideslope Native Proof Helper",
            NativeMethods.WS_OVERLAPPEDWINDOW | NativeMethods.WS_VISIBLE, 0, 0, 200, 200, 0, 0, _instance, 0);
        if (_hwnd == 0)
        {
            _startupFailure = $"create_window_failed_win32_{System.Runtime.InteropServices.Marshal.GetLastWin32Error()}";
            _ready.Set();
            return;
        }

        log($"helper_marker_window_created hwnd={_hwnd:X}");
        _ready.Set();

        while (NativeMethods.GetMessageW(out var message, 0, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessageW(ref message);
        }
    }

    // WM_APP + 1: "make wParam the foreground window", handled on the helper's own thread.
    private const uint WM_APP_ACTIVATE = 0x8000 + 1;
    private readonly ManualResetEventSlim _activateDone = new(false);
    private bool _activateResult;

    /// <summary>
    /// Hands the hidden desktop's foreground to <paramref name="target"/> from the helper's thread.
    /// The proof coordinator runs on the visible desktop, so its input thread cannot activate a window on
    /// the hidden desktop. This helper runs on the hidden desktop and performs the activation there.
    /// </summary>
    public bool ActivateFromHelperThread(nint target, TimeSpan timeout)
    {
        if (_hwnd == 0) return false;
        _activateDone.Reset();
        _activateResult = false;
        if (!NativeMethods.PostMessageW(_hwnd, (int)WM_APP_ACTIVATE, target, 0)) return false;
        if (!_activateDone.Wait(timeout))
        {
            _log($"helper_activate_timed_out waitSeconds={timeout.TotalSeconds:0}");
            return false;
        }
        return _activateResult;
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == NativeMethods.WM_DESTROY)
        {
            NativeMethods.PostQuitMessage(0);
            return 0;
        }

        if (msg == WM_APP_ACTIVATE)
        {
            var target = wParam;
            var targetThreadId = NativeMethods.GetWindowThreadProcessId(target, out _);
            var helperThreadId = NativeMethods.GetCurrentThreadId();
            var attached = NativeMethods.AttachThreadInput(helperThreadId, targetThreadId, true);
            try
            {
                _activateResult = NativeMethods.SetForegroundWindow(target);
                _log($"helper_activate target=0x{target:X} attached={attached} setForeground={_activateResult}" +
                     (_activateResult ? string.Empty : $" win32={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}"));
            }
            finally
            {
                if (attached) NativeMethods.AttachThreadInput(helperThreadId, targetThreadId, false);
                _activateDone.Set();
            }
            return 0;
        }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private bool _disposed;

    public void Dispose()
    {
        // Idempotent: Program disposes the helper explicitly before closing the hidden desktop and the
        // `using` disposes it again at scope end.
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != 0)
        {
            NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_CLOSE, 0, 0);
            _hwnd = 0;
        }
        // Wait for the thread whether or not a window was created: a thread that only ran
        // SetThreadDesktop still keeps the desktop busy until it exits.
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(5)))
            _log($"helper_marker_thread_join_timed_out waitSeconds=5");

        if (_instance != 0)
        {
            NativeMethods.UnregisterClassW(ClassName, _instance);
        }

        _ready.Dispose();
        _activateDone.Dispose();
    }
}
