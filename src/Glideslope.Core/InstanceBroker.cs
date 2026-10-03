using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Glideslope.Core;

public sealed partial class InstanceBroker : IInstanceBroker
{
    private static readonly TimeSpan ActivationDeadline = TimeSpan.FromSeconds(2);

    // How long a launch that finds the owner shutting down waits for it to exit.
    // The owner answers "closing" from BeginClosing until DisposeAsync releases the mutex or lock, which the
    // shell does last, after the monitoring session has flushed.
    private static readonly TimeSpan OwnerClosingWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UnixLockPollInterval = TimeSpan.FromMilliseconds(100);

    // A listener that cannot create its pipe (or accept on its socket)
    // retries with a capped backoff instead of ending.
    private static readonly TimeSpan ListenerRetryInitial = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ListenerRetryMaximum = TimeSpan.FromSeconds(30);
    private const int MaximumIntentBytes = 128;
    internal const string AcknowledgedResponse = "ack";
    internal const string ClosingResponse = "closing";

    private enum ForwardOutcome
    {
        Acknowledged,
        NotAcknowledged,
        OwnerClosing,
        Unreachable,
    }

    private enum UnixLockAttempt
    {
        Acquired,
        HeldByAnother,
        Failed,
    }

    private readonly string _preferredRuntimeDirectory;
    private readonly string? _runtimeFallbackDirectory;
    private string _runtimeDirectory;
    private readonly string _identity;
    private readonly string _pipeName;
    private readonly IDiagnosticSink _diagnostics;
    private readonly TimeProvider _time;
    private readonly Mutex? _mutex;
    private readonly CancellationTokenSource _dispose = new();
    private FileStream? _lockStream;
    private Socket? _socket;
    private NamedPipeServerStream? _pipe;
    private WindowsMutexLease? _mutexLease;
    private Task? _listenerTask;
    private Func<ActivationIntent, Task>? _onActivation;
    private string? _socketPath;
    private bool _ownsSocketPath;
    private int _disposed;
    private int _closing;

    public InstanceBroker(IUserPathProvider paths, IDiagnosticSink? diagnostics = null, string? identity = null, TimeProvider? timeProvider = null)
    {
        var userPaths = paths.Get();
        _preferredRuntimeDirectory = userPaths.RuntimeDirectory;
        _runtimeFallbackDirectory = userPaths.RuntimeFallbackDirectory;
        _runtimeDirectory = _preferredRuntimeDirectory;
        _identity = identity ?? ProofRoot.Current ?? Environment.GetEnvironmentVariable("GLIDESLOPE_PROOF_ROOT") ?? "default";
        _diagnostics = diagnostics ?? new NullDiagnosticSink();
        _time = timeProvider ?? TimeProvider.System;

        if (OperatingSystem.IsWindows())
        {
            // The mutex lives in the session's Local\ namespace, so it is already per user session. Its name
            // remains discoverable by an older process that is still running.
            _mutex = new Mutex(false, $"Local\\Glideslope-{StableName(_identity)}");
            _pipeName = WindowsPipeNameFor(_identity);
        }
        else
        {
            _pipeName = $"Glideslope-{StableName(_identity)}";
        }
    }

    internal Task? ListenerTaskForSpecs => _listenerTask;

    public async Task<InstanceAcquireResult> AcquireAsync(
        ActivationIntent intent,
        Func<ActivationIntent, Task> onActivation,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _onActivation = onActivation;

        return OperatingSystem.IsWindows()
            ? await AcquireWindowsAsync(intent, cancellationToken).ConfigureAwait(false)
            : await AcquireUnixAsync(intent, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void BeginClosing()
    {
        if (Interlocked.Exchange(ref _closing, 1) == 0)
            _diagnostics.Record(new DiagnosticEvent("instance_owner_closing_started", Status: _listenerTask is null ? "no_listener" : "refusing_activations"));
    }

    private async Task ProcessClientAsync(Stream stream, CancellationToken serverToken)
    {
        await using var ownedStream = stream;
        using var clientDeadline = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        clientDeadline.CancelAfter(ActivationDeadline);

        var intent = ParseIntent(await ReadLineBoundedAsync(stream, clientDeadline.Token).ConfigureAwait(false));
        if (Volatile.Read(ref _closing) != 0)
        {
            // The shell is shutting down. Answer "closing" so the new launch
            // waits for this process to exit and then starts normally, instead of routing an activation into
            // a coordinator that is tearing its windows down.
            await WriteLineAsync(stream, ClosingResponse, clientDeadline.Token).ConfigureAwait(false);
            _diagnostics.Record(new DiagnosticEvent("instance_activation_refused", Status: $"owner_closing,intent={intent?.ToString() ?? "unrecognized"}"));
            return;
        }

        await WriteLineAsync(stream, AcknowledgedResponse, clientDeadline.Token).ConfigureAwait(false);
        _diagnostics.Record(new DiagnosticEvent("instance_activation_received", Status: intent?.ToString() ?? "unrecognized"));

        if (intent is not null && _onActivation is not null)
        {
            if (Volatile.Read(ref _closing) != 0)
            {
                // Closing began after the acknowledgement: the activation is dropped, not routed into teardown.
                _diagnostics.Record(new DiagnosticEvent("instance_activation_dropped", Status: "owner_closing"));
                return;
            }

            await _onActivation(intent.Value).WaitAsync(clientDeadline.Token).ConfigureAwait(false);
        }
    }

    private ForwardOutcome ClassifyResponse(string? response)
    {
        if (response == AcknowledgedResponse)
            return ForwardOutcome.Acknowledged;
        if (response == ClosingResponse)
            return ForwardOutcome.OwnerClosing;

        _diagnostics.Record(new DiagnosticEvent("instance_forward_unexpected_response", Status: response is null ? "none" : $"length_{response.Length}"));
        return ForwardOutcome.NotAcknowledged;
    }

    private InstanceAcquireResult ForwardResult(ForwardOutcome outcome) => outcome switch
    {
        ForwardOutcome.Acknowledged => new InstanceAcquireResult(InstanceRole.Forwarded),
        ForwardOutcome.Unreachable => Failed("owner_unreachable"),
        _ => Failed(),
    };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _closing, 1);
        _dispose.Cancel();
        _socket?.Dispose();
        _pipe?.Dispose();

        if (_listenerTask is not null)
        {
            try
            {
                await _listenerTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Disposal never throws. A listener that had faulted with an
                // exception this filter did not name (UnauthorizedAccessException under fast user switching)
                // fault must not turn an otherwise clean exit into a failure.
                _diagnostics.Record(new DiagnosticEvent("instance_listener_cleanup_failed", Status: $"teardown_incomplete,type={ex.GetType().Name}"));
            }
        }

        if (_ownsSocketPath && _socketPath is not null)
        {
            try
            {
                if (File.Exists(_socketPath))
                {
                    File.Delete(_socketPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _diagnostics.Record(new DiagnosticEvent("instance_socket_cleanup_failed", Status: $"teardown_incomplete,type={ex.GetType().Name}"));
            }
        }

        _lockStream?.Dispose();
        _lockStream = null;

        if (_mutexLease is not null)
        {
            await _mutexLease.DisposeAsync().ConfigureAwait(false);
            if (_mutexLease.Stopped)
            {
                _mutex?.Dispose();
            }

            _mutexLease = null;
        }
        else
        {
            _mutex?.Dispose();
        }

        _dispose.Dispose();
    }

    private async Task<bool> DelayListenerRetryAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static TimeSpan NextListenerRetry(TimeSpan current) =>
        current >= ListenerRetryMaximum / 2 ? ListenerRetryMaximum : current + current;

    private static CancellationTokenSource CreateActivationDeadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ActivationDeadline);
        return deadline;
    }

    private InstanceAcquireResult Failed(string issue = "activation_not_acknowledged")
    {
        _diagnostics.Record(new DiagnosticEvent("instance_activation_failed", Status: issue));
        return new InstanceAcquireResult(InstanceRole.ActivationFailed, issue);
    }

    private static string StableName(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 8));
    }

    private static ActivationIntent? ParseIntent(string? value)
    {
        return Enum.TryParse<ActivationIntent>(value, true, out var intent) ? intent : null;
    }

    private static async Task<string?> ReadLineBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[MaximumIntentBytes];
        var count = 0;
        var oneByte = new byte[1];

        while (count < MaximumIntentBytes)
        {
            var read = await stream.ReadAsync(oneByte.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return count == 0 ? null : throw new InvalidDataException("The activation payload ended before its delimiter.");
            }

            if (oneByte[0] == (byte)'\n')
            {
                var value = Encoding.UTF8.GetString(bytes, 0, count);
                return value.EndsWith('\r') ? value[..^1] : value;
            }

            bytes[count++] = oneByte[0];
        }

        throw new InvalidDataException("The activation payload exceeded the protocol limit.");
    }

    private static async Task WriteLineAsync(Stream stream, string value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\n");
        await stream.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
