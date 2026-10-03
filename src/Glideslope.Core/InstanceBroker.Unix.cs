using System.Net.Sockets;
using System.Runtime.Versioning;

namespace Glideslope.Core;

// InstanceBroker, the Linux (Unix) side: single instance by a lock file, and activation forwarded over
// a Unix domain socket in a private runtime directory (mode 0700, owned by this user). The lock, the
// directory checks, the socket server and the forwarding client live here.
public sealed partial class InstanceBroker
{
    internal static string UnixSocketFileNameFor(string identity) => $"instance-{StableName(identity)}.sock";

    private static string UnixLockFileNameFor(string identity) => $"instance-{StableName(identity)}.lock";

    [UnsupportedOSPlatform("windows")]
    private async Task<InstanceAcquireResult> AcquireUnixAsync(ActivationIntent intent, CancellationToken cancellationToken)
    {
        var runtime = PrepareUnixRuntimeDirectory();
        if (runtime is null)
            return Failed("runtime_directory_untrusted");
        _runtimeDirectory = runtime;

        var attempt = TryOpenUnixLock();
        if (attempt == UnixLockAttempt.Acquired)
            return await BecomeUnixOwnerAsync("owner").ConfigureAwait(false);
        if (attempt == UnixLockAttempt.Failed)
            return Failed("instance_lock_unavailable");

        var outcome = await ForwardUnixAsync(intent, cancellationToken).ConfigureAwait(false);
        if (outcome != ForwardOutcome.OwnerClosing)
            return ForwardResult(outcome);

        // Wait for the closing owner to release its lock before retrying.
        _diagnostics.Record(new DiagnosticEvent("instance_owner_closing", Status: $"waiting_ms={(long)OwnerClosingWait.TotalMilliseconds}"));
        var started = _time.GetTimestamp();
        while (_time.GetElapsedTime(started) < OwnerClosingWait)
        {
            try
            {
                await Task.Delay(UnixLockPollInterval, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Failed("activation_cancelled");
            }

            attempt = TryOpenUnixLock();
            if (attempt == UnixLockAttempt.Acquired)
                return await BecomeUnixOwnerAsync("owner_after_previous_closed").ConfigureAwait(false);
            if (attempt == UnixLockAttempt.Failed)
                return Failed("instance_lock_unavailable");
        }

        return Failed("owner_closing_timeout");
    }

    [UnsupportedOSPlatform("windows")]
    private UnixLockAttempt TryOpenUnixLock()
    {
        var lockPath = Path.Combine(_runtimeDirectory, UnixLockFileNameFor(_identity));
        FileStream stream;
        try
        {
            stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            // Another process holds the lock: the forwarding path handles it (and logs its own outcome).
            return UnixLockAttempt.HeldByAnother;
        }
        catch (UnauthorizedAccessException ex)
        {
            _diagnostics.Record(new DiagnosticEvent("instance_lock_failed", Status: ex.GetType().Name));
            return UnixLockAttempt.Failed;
        }

        try
        {
            EnsurePrivateUnixFile(lockPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stream.Dispose();
            _diagnostics.Record(new DiagnosticEvent("instance_lock_failed", Status: $"mode_not_set,type={ex.GetType().Name}"));
            return UnixLockAttempt.Failed;
        }

        _lockStream = stream;
        return UnixLockAttempt.Acquired;
    }

    [UnsupportedOSPlatform("windows")]
    private async Task<InstanceAcquireResult> BecomeUnixOwnerAsync(string status)
    {
        _socketPath = Path.Combine(_runtimeDirectory, UnixSocketFileNameFor(_identity));
        try
        {
            // The lock is held before touching the endpoint. A leftover socket is therefore
            // stale, while a live endpoint always belongs to another lock holder.
            if (File.Exists(_socketPath))
            {
                File.Delete(_socketPath);
            }

            _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _socket.Bind(new UnixDomainSocketEndPoint(_socketPath));
            _socket.Listen(8);
            EnsurePrivateUnixFile(_socketPath);
            _ownsSocketPath = true;
            _listenerTask = RunSocketServerAsync(_dispose.Token);
            _diagnostics.Record(new DiagnosticEvent("instance_owner_acquired", Status: status));
            return new InstanceAcquireResult(InstanceRole.Owner);
        }
        catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException)
        {
            _diagnostics.Record(new DiagnosticEvent("instance_socket_bind_failed", Status: ex.GetType().Name));
            await DisposeAsync().ConfigureAwait(false);
            return new InstanceAcquireResult(InstanceRole.ActivationFailed, "socket_bind_failed");
        }
        catch (ArgumentException exception)
        {
            var issueCode = exception is ArgumentOutOfRangeException ? "socket_path_too_long" : "socket_path_invalid";
            await DisposeAsync().ConfigureAwait(false);
            _diagnostics.Record(new DiagnosticEvent("instance_socket_path_rejected", Status: issueCode));
            return new InstanceAcquireResult(InstanceRole.ActivationFailed, issueCode);
        }
    }

    /// <summary>
    /// Without XDG_RUNTIME_DIR, the runtime directory falls back to a private directory under shared /tmp.
    /// The directory (and, in /tmp, its parent) must be a real directory, not a symbolic link, and owned by
    /// this user with mode 0700; chmod succeeds only for the owner, so setting the mode also proves
    /// ownership. Otherwise the per-user directory under the cache root is used, and failing that the launch
    /// ends with a typed failure instead of an exception.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private string? PrepareUnixRuntimeDirectory()
    {
        var preferredIssue = TryPreparePrivateDirectory(_preferredRuntimeDirectory, verifyParent: _runtimeFallbackDirectory is not null);
        if (preferredIssue is null)
        {
            _diagnostics.Record(new DiagnosticEvent("instance_runtime_dir_ready", Status: "preferred"));
            return _preferredRuntimeDirectory;
        }

        _diagnostics.Record(new DiagnosticEvent("instance_runtime_dir_rejected", Status: $"preferred,{preferredIssue}"));
        if (_runtimeFallbackDirectory is null)
            return null;

        var fallbackIssue = TryPreparePrivateDirectory(_runtimeFallbackDirectory, verifyParent: false);
        if (fallbackIssue is null)
        {
            _diagnostics.Record(new DiagnosticEvent("instance_runtime_dir_ready", Status: "fallback"));
            return _runtimeFallbackDirectory;
        }

        _diagnostics.Record(new DiagnosticEvent("instance_runtime_dir_rejected", Status: $"fallback,{fallbackIssue}"));
        return null;
    }

    [UnsupportedOSPlatform("windows")]
    private static string? TryPreparePrivateDirectory(string path, bool verifyParent)
    {
        try
        {
            if (verifyParent)
            {
                var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
                if (string.IsNullOrEmpty(parent))
                    return "parent_missing";
                var parentIssue = EnsureOwnedPrivateDirectory(parent);
                if (parentIssue is not null)
                    return "parent_" + parentIssue;
            }

            return EnsureOwnedPrivateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"exception_{ex.GetType().Name}";
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static string? EnsureOwnedPrivateDirectory(string path)
    {
        const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (File.Exists(path))
            return "not_directory";

        var info = new DirectoryInfo(path);
        if (info.Exists && info.LinkTarget is not null)
            return "symlink";
        if (!info.Exists)
            Directory.CreateDirectory(path, PrivateMode);

        info.Refresh();
        if (info.LinkTarget is not null)
            return "symlink";

        try
        {
            File.SetUnixFileMode(path, PrivateMode);
        }
        catch (UnauthorizedAccessException)
        {
            // chmod is refused for anyone but the owner: this directory belongs to another user.
            return "not_owned";
        }

        return File.GetUnixFileMode(path) == PrivateMode ? null : "mode_not_private";
    }

    private async Task RunSocketServerAsync(CancellationToken cancellationToken)
    {
        var retryDelay = ListenerRetryInitial;
        while (!cancellationToken.IsCancellationRequested && _socket is not null)
        {
            Socket client;
            try
            {
                client = await _socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is SocketException or ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Log the exception type and retry with a backoff to avoid a
                // tight loop that logged the same failure without a type.
                _diagnostics.Record(new DiagnosticEvent("instance_socket_error",
                    Status: $"activation_listener_failed_{ex.GetType().Name},retry_ms={(long)retryDelay.TotalMilliseconds}"));
                if (!await DelayListenerRetryAsync(retryDelay, cancellationToken).ConfigureAwait(false))
                    break;
                retryDelay = NextListenerRetry(retryDelay);
                continue;
            }

            retryDelay = ListenerRetryInitial;
            try
            {
                await ProcessClientAsync(new NetworkStream(client, ownsSocket: false), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                _diagnostics.Record(new DiagnosticEvent("instance_socket_error", Status: "activation_deadline_expired"));
            }
            catch (InvalidDataException)
            {
                _diagnostics.Record(new DiagnosticEvent("instance_socket_error", Status: "activation_payload_rejected"));
            }
            catch (Exception ex)
            {
                _diagnostics.Record(new DiagnosticEvent("instance_socket_error", Status: $"activation_client_failed_{ex.GetType().Name}"));
            }
            finally
            {
                client.Dispose();
            }
        }

        _diagnostics.Record(new DiagnosticEvent("instance_listener_stopped", Status: "socket"));
    }

    private async Task<ForwardOutcome> ForwardUnixAsync(ActivationIntent intent, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_runtimeDirectory, UnixSocketFileNameFor(_identity));
        try
        {
            using var deadline = CreateActivationDeadline(cancellationToken);
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(path), deadline.Token).ConfigureAwait(false);
            await using var stream = new NetworkStream(client, ownsSocket: false);
            await WriteLineAsync(stream, intent.ToString(), deadline.Token).ConfigureAwait(false);
            var response = await ReadLineBoundedAsync(stream, deadline.Token).ConfigureAwait(false);
            return ClassifyResponse(response);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidDataException or UnauthorizedAccessException)
        {
            _diagnostics.Record(new DiagnosticEvent("instance_forward_failed", Status: ex.GetType().Name));
            return ForwardOutcome.Unreachable;
        }
    }

    private static void EnsurePrivateUnixFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
