using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Glideslope.Core;

// InstanceBroker, the Windows side: single instance by a per-session mutex, and activation forwarded
// over a named pipe whose name is per user and session. The pipe server, the client that forwards an
// activation to the owner, and the pipe's creation with its access rules live here.
public sealed partial class InstanceBroker
{
    /// <summary>The activation pipe name for this user and session.</summary>
    [SupportedOSPlatform("windows")]
    internal static string WindowsPipeNameFor(string identity)
    {
        using var current = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        return WindowsPipeNameFor(identity, current.User?.Value ?? "no-user-sid", process.SessionId);
    }

    /// <summary>
    /// Named pipes are machine-wide, so the pipe name hashes the identity, user SID, and session ID to give each
    /// owner a distinct endpoint. The SID is hashed and never appears in the pipe namespace.
    /// </summary>
    internal static string WindowsPipeNameFor(string identity, string userSid, int sessionId) =>
        $"Glideslope-{StableName($"{identity}|{userSid}|{sessionId.ToString(CultureInfo.InvariantCulture)}")}";

    private async Task<InstanceAcquireResult> AcquireWindowsAsync(ActivationIntent intent, CancellationToken cancellationToken)
    {
        var lease = await WindowsMutexLease.TryAcquireAsync(_mutex!, TimeSpan.Zero, _diagnostics).ConfigureAwait(false);
        if (lease.Acquired)
            return BecomeWindowsOwner(lease, "owner");

        await lease.DisposeAsync().ConfigureAwait(false);
        var outcome = await ForwardWindowsAsync(intent, cancellationToken).ConfigureAwait(false);
        if (outcome != ForwardOutcome.OwnerClosing)
            return ForwardResult(outcome);

        // The owner is shutting down and will release the mutex when it has
        // flushed. Wait for that and take over, so a launch made during shutdown still opens the app.
        _diagnostics.Record(new DiagnosticEvent("instance_owner_closing", Status: $"waiting_ms={(long)OwnerClosingWait.TotalMilliseconds}"));
        var waited = await WindowsMutexLease.TryAcquireAsync(_mutex!, OwnerClosingWait, _diagnostics).ConfigureAwait(false);
        if (waited.Acquired)
            return BecomeWindowsOwner(waited, "owner_after_previous_closed");

        await waited.DisposeAsync().ConfigureAwait(false);
        return Failed("owner_closing_timeout");
    }

    private InstanceAcquireResult BecomeWindowsOwner(WindowsMutexLease lease, string status)
    {
        _mutexLease = lease;
        _listenerTask = RunPipeServerAsync(_dispose.Token);
        _diagnostics.Record(new DiagnosticEvent("instance_owner_acquired", Status: lease.RecoveredAbandoned ? "abandoned_owner_recovered" : status));
        return new InstanceAcquireResult(InstanceRole.Owner);
    }

    private async Task RunPipeServerAsync(CancellationToken cancellationToken)
    {
        var retryDelay = ListenerRetryInitial;
        var consecutiveFailures = 0;
        var announced = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipeServer();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Log and retry pipe creation failures with capped backoff so a transient conflict does not end
                // the listener.
                consecutiveFailures++;
                _diagnostics.Record(new DiagnosticEvent("instance_pipe_error",
                    Status: $"activation_listener_create_failed_{ex.GetType().Name},attempt={consecutiveFailures},retry_ms={(long)retryDelay.TotalMilliseconds}"));
                if (!await DelayListenerRetryAsync(retryDelay, cancellationToken).ConfigureAwait(false))
                    break;
                retryDelay = NextListenerRetry(retryDelay);
                continue;
            }

            if (!announced || consecutiveFailures > 0)
            {
                _diagnostics.Record(new DiagnosticEvent("instance_pipe_listening", Status: consecutiveFailures > 0 ? $"recovered_after_{consecutiveFailures}" : "first"));
                announced = true;
            }

            consecutiveFailures = 0;
            retryDelay = ListenerRetryInitial;
            _pipe = pipe;
            try
            {
                try
                {
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or InvalidOperationException)
                {
                    consecutiveFailures++;
                    _diagnostics.Record(new DiagnosticEvent("instance_pipe_error",
                        Status: $"activation_listener_failed_{ex.GetType().Name},attempt={consecutiveFailures},retry_ms={(long)retryDelay.TotalMilliseconds}"));
                    if (!await DelayListenerRetryAsync(retryDelay, cancellationToken).ConfigureAwait(false))
                        break;
                    retryDelay = NextListenerRetry(retryDelay);
                    continue;
                }

                try
                {
                    await ProcessClientAsync(pipe, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    _diagnostics.Record(new DiagnosticEvent("instance_pipe_error", Status: "activation_deadline_expired"));
                }
                catch (InvalidDataException)
                {
                    _diagnostics.Record(new DiagnosticEvent("instance_pipe_error", Status: "activation_payload_rejected"));
                }
                catch (Exception ex)
                {
                    // A bad client or failing activation handler must not end the listener.
                    _diagnostics.Record(new DiagnosticEvent("instance_pipe_error", Status: $"activation_client_failed_{ex.GetType().Name}"));
                }
            }
            finally
            {
                pipe.Dispose();
                if (ReferenceEquals(_pipe, pipe))
                {
                    _pipe = null;
                }
            }
        }

        _diagnostics.Record(new DiagnosticEvent("instance_listener_stopped", Status: "pipe"));
    }

    private async Task<ForwardOutcome> ForwardWindowsAsync(ActivationIntent intent, CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = CreateActivationDeadline(cancellationToken);
            // CurrentUserOnly refuses a pipe this user does not own, and an
            // UnauthorizedAccessException (a pipe of this name this user may not open) is caught below instead
            // of crashing the second launch.
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(deadline.Token).ConfigureAwait(false);
            await WriteLineAsync(client, intent.ToString(), deadline.Token).ConfigureAwait(false);
            var response = await ReadLineBoundedAsync(client, deadline.Token).ConfigureAwait(false);
            return ClassifyResponse(response);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or InvalidDataException or UnauthorizedAccessException)
        {
            _diagnostics.Record(new DiagnosticEvent("instance_forward_failed", Status: ex.GetType().Name));
            return ForwardOutcome.Unreachable;
        }
    }

    private NamedPipeServerStream CreatePipeServer()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
        }

        using var current = WindowsIdentity.GetCurrent();
        var currentUser = current.User
            ?? throw new InvalidOperationException("The current Windows identity has no SID.");
        var security = new PipeSecurity();
        // Set the owner explicitly so a forwarding launch can
        // require it (PipeOptions.CurrentUserOnly) even when this process runs elevated, where Windows would
        // otherwise make the Administrators group the owner.
        security.SetOwner(currentUser);
        security.SetAccessRule(new PipeAccessRule(
            currentUser,
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security,
            HandleInheritability.None,
            (PipeAccessRights)0);
    }
}
