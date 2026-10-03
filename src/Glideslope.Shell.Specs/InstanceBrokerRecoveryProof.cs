using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Glideslope.Core;

namespace Glideslope.Shell.Specs;

/// <summary>
/// single-instance recovery.
/// E5: the Windows pipe name carries the user SID and session id; a listener that cannot create its pipe
/// (the name already held, as by another user's owner under fast user switching) logs, retries on the
/// broker's clock and recovers; a second launch that reaches a pipe it may not open reports
/// owner_unreachable instead of crashing.
/// E9: an owner that has begun closing answers "closing", routes nothing into the shell, and the waiting
/// launch becomes the owner once the old one is disposed.
/// E13 (Linux): the runtime directory is used only when private to this user; otherwise the fallback.
/// </summary>
internal static class InstanceBrokerRecoveryProof
{
    public const string ExpectUnreachableChildMode = "--instance-forward-expect-unreachable";
    public const string ClosingChildMode = "--instance-closing-child";
    private static readonly TimeSpan SignalGuard = TimeSpan.FromSeconds(15);

    public static async Task RunAsync(string tempRoot)
    {
        PipeNameCarriesUserAndSession();
        await ListenerSurvivesAnOccupiedPipeNameAsync(tempRoot).ConfigureAwait(false);
        await ClosingOwnerHandsOverToTheNextLaunchAsync(tempRoot).ConfigureAwait(false);
        await UnixRuntimeDirectoryMustBePrivateAsync(tempRoot).ConfigureAwait(false);
    }

    private static void PipeNameCarriesUserAndSession()
    {
        const string sid = "S-1-5-21-1000-2000-3000-1001";
        var name = InstanceBroker.WindowsPipeNameFor("default", sid, 1);
        Assert(name.StartsWith("Glideslope-", StringComparison.Ordinal) && name.Length == "Glideslope-".Length + 16, "the pipe name keeps its bounded shape");
        Assert(name == InstanceBroker.WindowsPipeNameFor("default", sid, 1), "the pipe name is deterministic");
        Assert(name != InstanceBroker.WindowsPipeNameFor("default", sid, 2), "another session of the same user gets its own pipe");
        Assert(name != InstanceBroker.WindowsPipeNameFor("default", "S-1-5-21-1000-2000-3000-1002", 1), "another user gets its own pipe");
        Assert(name != InstanceBroker.WindowsPipeNameFor("proof", sid, 1), "another identity gets its own pipe");
        Assert(!name.Contains(sid, StringComparison.Ordinal), "the SID is hashed, never visible in the machine-wide pipe namespace");
        var sharedName = $"Glideslope-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("default")).AsSpan(0, 8))}";
        Assert(name != sharedName, "the 1.0 machine-wide name, shared by every user and session, is gone");
    }

    private static async Task ListenerSurvivesAnOccupiedPipeNameAsync(string tempRoot)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(tempRoot, "pipe-occupied");
        var identity = $"occupied-{Guid.NewGuid():N}";
        var pipeName = InstanceBroker.WindowsPipeNameFor(identity);
        var sink = new SignalingDiagnosticSink();
        var time = new SignalingTimeProvider();
        var callbacks = new ConcurrentQueue<ActivationIntent>();
        // Stands in for another owner's pipe of the same name: a pipe this user may not open or add to.
        var squatter = CreateUnopenablePipe(pipeName);
        var owner = new InstanceBroker(new SpecPaths(root), sink, identity, time);
        try
        {
            var acquired = await owner.AcquireAsync(ActivationIntent.Manual, intent =>
            {
                callbacks.Enqueue(intent);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            Assert(acquired.Role == InstanceRole.Owner, "the first broker owns the mutex");

            var createFailed = await sink.WaitForAsync(e => e.Code == "instance_pipe_error" &&
                    e.Status!.StartsWith("activation_listener_create_failed_UnauthorizedAccessException", StringComparison.Ordinal))
                .WaitAsync(SignalGuard).ConfigureAwait(false);
            Assert(createFailed.Status!.Contains("retry_ms=1000", StringComparison.Ordinal), "the first retry waits one second");
            await time.TimerCreatedAsync(0).WaitAsync(SignalGuard).ConfigureAwait(false);
            Assert(owner.ListenerTaskForSpecs is { IsCompleted: false }, "the listener must survive a pipe it cannot create");

            // A second launch that reaches the inaccessible pipe reports owner_unreachable (exit 0 here).
            await RunChildAsync(ExpectUnreachableChildMode, root, identity).ConfigureAwait(false);

            squatter.Dispose();
            squatter = null;
            time.Advance(TimeSpan.FromSeconds(1));
            await sink.WaitForAsync(e => e.Code == "instance_pipe_listening" && e.Status == "recovered_after_1")
                .WaitAsync(SignalGuard).ConfigureAwait(false);

            await RunChildAsync("--instance-child", root, identity, ActivationIntent.Manual.ToString()).ConfigureAwait(false);
            Assert(callbacks.SequenceEqual([ActivationIntent.Manual]), "once the name is free the recovered listener forwards activations");
        }
        finally
        {
            squatter?.Dispose();
            await owner.DisposeAsync().ConfigureAwait(false);
        }

        Assert(sink.Snapshot().Any(e => e.Code == "instance_listener_stopped" && e.Status == "pipe"), "disposal stops the listener cleanly");
        Assert(!sink.Snapshot().Any(e => e.Code == "instance_listener_cleanup_failed"), "disposal does not fail");
    }

    private static async Task ClosingOwnerHandsOverToTheNextLaunchAsync(string tempRoot)
    {
        var root = Path.Combine(tempRoot, "closing-handoff");
        var identity = $"closing-{Guid.NewGuid():N}";
        var sink = new SignalingDiagnosticSink();
        var callbacks = new ConcurrentQueue<ActivationIntent>();
        var owner = new InstanceBroker(new SpecPaths(root), sink, identity);
        var disposed = false;
        Process? child = null;
        try
        {
            var acquired = await owner.AcquireAsync(ActivationIntent.Manual, intent =>
            {
                callbacks.Enqueue(intent);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            Assert(acquired.Role == InstanceRole.Owner, "the closing-handoff owner acquires");
            owner.BeginClosing();
            Assert(sink.Snapshot().Any(e => e.Code == "instance_owner_closing_started"), "beginning to close is logged");

            child = StartChild(ClosingChildMode, root, identity);
            await sink.WaitForAsync(e => e.Code == "instance_activation_refused").WaitAsync(SignalGuard).ConfigureAwait(false);
            await owner.DisposeAsync().ConfigureAwait(false);
            disposed = true;

            await child.WaitForExitAsync().ConfigureAwait(false);
            Assert(child.ExitCode == 0, $"the waiting launch should become the owner once the closing owner exits (exit {child.ExitCode})");
            Assert(callbacks.IsEmpty, "a closing owner routes no activation into the shell");
        }
        finally
        {
            if (!disposed)
                await owner.DisposeAsync().ConfigureAwait(false);
            if (child is not null)
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync().ConfigureAwait(false);
                }

                child.Dispose();
            }
        }
    }

    private static async Task UnixRuntimeDirectoryMustBePrivateAsync(string tempRoot)
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(tempRoot, "runtime-trust");
        Directory.CreateDirectory(root);

        // 1. The shared-temp parent is a symbolic link planted by someone else: the fallback is used and
        //    nothing is written through the link.
        var elsewhere = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var planted = Path.Combine(root, "Glideslope-runtime-planted");
        Directory.CreateSymbolicLink(planted, elsewhere);
        var fallback = Path.Combine(root, "cache", "Glideslope", "runtime");
        var symlinkResult = await AcquireOnceAsync(root, Path.Combine(planted, "Glideslope"), fallback).ConfigureAwait(false);
        Assert(symlinkResult.Role == InstanceRole.Owner, "a planted link falls back to the private directory");
        Assert(symlinkResult.Events.Any(e => e.Code == "instance_runtime_dir_rejected" && e.Status == "preferred,parent_symlink"), "the link is rejected and logged");
        Assert(symlinkResult.Events.Any(e => e.Code == "instance_runtime_dir_ready" && e.Status == "fallback"), "the fallback is logged");
        Assert(Directory.GetFileSystemEntries(elsewhere).Length == 0, "nothing is written through the planted link");
        Assert(File.GetUnixFileMode(fallback) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "the fallback is 0700");

        // 2. The parent is a regular file.
        var fileParent = Path.Combine(root, "Glideslope-runtime-file");
        await File.WriteAllTextAsync(fileParent, "not a directory").ConfigureAwait(false);
        var fileResult = await AcquireOnceAsync(root, Path.Combine(fileParent, "Glideslope"), Path.Combine(root, "cache2", "runtime")).ConfigureAwait(false);
        Assert(fileResult.Role == InstanceRole.Owner && fileResult.Events.Any(e => e.Status == "preferred,parent_not_directory"), "a file in the way falls back");

        // 3. A world-writable directory this user owns is made private and used.
        var loose = Path.Combine(root, "Glideslope-runtime-loose");
        Directory.CreateDirectory(loose);
        File.SetUnixFileMode(loose, (UnixFileMode)0x1FF);
        var looseResult = await AcquireOnceAsync(root, Path.Combine(loose, "Glideslope"), Path.Combine(root, "cache3", "runtime")).ConfigureAwait(false);
        Assert(looseResult.Role == InstanceRole.Owner && looseResult.Events.Any(e => e.Code == "instance_runtime_dir_ready" && e.Status == "preferred"), "an owned directory is tightened and used");
        Assert(File.GetUnixFileMode(loose) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "its mode is now 0700");

        // 4. No fallback (XDG_RUNTIME_DIR set) and the runtime directory itself is a link: a typed failure, no exception.
        var linkedLeaf = Path.Combine(root, "xdg-runtime-link");
        Directory.CreateSymbolicLink(linkedLeaf, elsewhere);
        var noFallback = await AcquireOnceAsync(root, linkedLeaf, fallback: null).ConfigureAwait(false);
        Assert(noFallback.Role == InstanceRole.ActivationFailed && noFallback.IssueCode == "runtime_directory_untrusted", "an untrusted directory with no fallback fails typed");
    }

    [UnsupportedOSPlatform("windows")]
    private static async Task<(InstanceRole Role, string? IssueCode, IReadOnlyList<DiagnosticEvent> Events)> AcquireOnceAsync(string root, string runtime, string? fallback)
    {
        var sink = new SignalingDiagnosticSink();
        var paths = new FixedPaths(new UserPaths(
            Path.Combine(root, "config", "settings.json"),
            Path.Combine(root, "data"),
            runtime,
            Path.Combine(root, "cache"),
            Path.Combine(root, "logs"),
            fallback));
        await using var broker = new InstanceBroker(paths, sink, $"runtime-{Guid.NewGuid():N}");
        var result = await broker.AcquireAsync(ActivationIntent.Manual, _ => Task.CompletedTask).ConfigureAwait(false);
        return (result.Role, result.IssueCode, sink.Snapshot());
    }

    public static async Task<int> RunExpectUnreachableChildAsync(string[] args)
    {
        if (args.Length != 3) return 2;
        await using var broker = new InstanceBroker(new SpecPaths(args[1]), identity: args[2]);
        var result = await broker.AcquireAsync(ActivationIntent.Manual, _ => Task.CompletedTask).ConfigureAwait(false);
        return result.Role == InstanceRole.ActivationFailed && result.IssueCode == "owner_unreachable" ? 0 : 5;
    }

    public static async Task<int> RunClosingChildAsync(string[] args)
    {
        if (args.Length != 3) return 2;
        await using var broker = new InstanceBroker(new SpecPaths(args[1]), identity: args[2]);
        var result = await broker.AcquireAsync(ActivationIntent.Manual, _ => Task.CompletedTask).ConfigureAwait(false);
        return result.Role == InstanceRole.Owner && result.IssueCode is null ? 0 : 4;
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateUnopenablePipe(string pipeName)
    {
        using var current = WindowsIdentity.GetCurrent();
        var user = current.User ?? throw new InvalidOperationException("spec_user_sid_missing");
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.SetAccessRule(new PipeAccessRule(user, PipeAccessRights.Synchronize, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security, HandleInheritability.None, (PipeAccessRights)0);
    }

    private static async Task RunChildAsync(string mode, string root, string identity, string? extra = null)
    {
        using var process = StartChild(mode, root, identity, extra);
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            Console.Error.WriteLine($"spec child '{mode}' exited with code {process.ExitCode}");
            Assert(process.ExitCode == 0, $"child {mode} exited with {process.ExitCode}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
    }

    private static Process StartChild(string mode, string root, string identity, string? extra = null)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("spec_process_path_missing");
        var startInfo = new ProcessStartInfo { FileName = processPath, UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add(typeof(InstanceBrokerRecoveryProof).Assembly.Location);
        }

        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add(root);
        startInfo.ArgumentList.Add(identity);
        if (extra is not null)
            startInfo.ArgumentList.Add(extra);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("spec_child_start_failed");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {message}");
    }

    /// <summary>The same layout as Program.TestPaths, so the --instance-child worker computes the same endpoints.</summary>
    private sealed class SpecPaths(string root) : IUserPathProvider
    {
        private readonly UserPaths _paths = new(Path.Combine(root, "config", "Glideslope", "settings.json"), Path.Combine(root, "data"), Path.Combine(root, "runtime"), Path.Combine(root, "cache"), Path.Combine(root, "logs"));
        public UserPaths Get() => _paths;
    }

    private sealed class FixedPaths(UserPaths paths) : IUserPathProvider
    {
        public UserPaths Get() => paths;
    }

    /// <summary>Records events from any thread and completes waiters when a matching event arrives.</summary>
    private sealed class SignalingDiagnosticSink : IDiagnosticSink
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

        public Task<DiagnosticEvent> WaitForAsync(Func<DiagnosticEvent, bool> predicate)
        {
            lock (_gate)
            {
                var existing = _events.FirstOrDefault(predicate);
                if (existing is not null) return Task.FromResult(existing);
                var signal = new TaskCompletionSource<DiagnosticEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((predicate, signal));
                return signal.Task;
            }
        }

        public IReadOnlyList<DiagnosticEvent> Snapshot()
        {
            lock (_gate) return _events.ToList();
        }
    }

    /// <summary>A fake clock for the listener's retry delay: timers fire only on Advance, and each created
    /// timer can be awaited by its creation index, so the spec never races the listener.</summary>
    private sealed class SignalingTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<SpecTimer> _timers = [];
        private readonly Dictionary<int, TaskCompletionSource<TimeSpan>> _created = [];
        private long _ticks;

        public override long GetTimestamp() { lock (_gate) return _ticks; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() { lock (_gate) return DateTimeOffset.UnixEpoch.AddTicks(_ticks); }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TaskCompletionSource<TimeSpan> signal;
            SpecTimer timer;
            lock (_gate)
            {
                timer = new SpecTimer(this, callback, state);
                timer.Schedule(_ticks, dueTime);
                _timers.Add(timer);
                signal = SignalFor(_timers.Count - 1);
            }

            signal.TrySetResult(dueTime);
            return timer;
        }

        public Task<TimeSpan> TimerCreatedAsync(int index)
        {
            lock (_gate) return SignalFor(index).Task;
        }

        public void Advance(TimeSpan amount)
        {
            List<SpecTimer> due;
            lock (_gate)
            {
                _ticks += amount.Ticks;
                due = _timers.Where(timer => timer.IsDue(_ticks)).ToList();
                foreach (var timer in due) timer.MarkFired();
            }

            foreach (var timer in due) timer.Fire();
        }

        private TaskCompletionSource<TimeSpan> SignalFor(int index)
        {
            if (!_created.TryGetValue(index, out var signal))
            {
                signal = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
                _created[index] = signal;
            }

            return signal;
        }

        private sealed class SpecTimer(SignalingTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            private long? _due;
            private bool _disposed;

            public void Schedule(long now, TimeSpan dueTime) => _due = dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime.Ticks;
            public bool IsDue(long now) => !_disposed && _due is { } due && due <= now;
            public void MarkFired() => _due = null;
            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    if (_disposed) return false;
                    Schedule(owner._ticks, dueTime);
                    return true;
                }
            }

            public void Dispose()
            {
                lock (owner._gate) _disposed = true;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
