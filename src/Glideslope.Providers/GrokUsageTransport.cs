using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Glideslope.Domain;

namespace Glideslope.Providers;

public sealed record GrokRpcReply(string ResultJson, int? ErrorCode);
public sealed record GrokUsageExchange(GrokRpcReply Initialize, GrokRpcReply AuthInfo, GrokRpcReply Billing);

public interface IGrokUsageTransport
{
    ValueTask<GrokUsageExchange> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Runs only the three read-only ACP calls needed for Grok's weekly quota. It never authenticates,
/// creates a session, or sends a prompt; those methods could trigger interactive login or model work.</summary>
public sealed class GrokCliUsageTransport : IGrokUsageTransport
{
    internal const string DisableAutoUpdaterVariable = "GROK_DISABLE_AUTOUPDATER";
    private const int MaximumFrameCharacters = 256 * 1024;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan ShutdownAndDrainAllowance = ShutdownTimeout * 2 + CliProcessSupport.PipeDrainAfterExit;
    public static readonly TimeSpan WorstCaseReadDuration = DefaultTimeout + ShutdownAndDrainAllowance;
    private readonly string? _path;
    private readonly string? _homeDirectory;
    private readonly bool _isWindows;
    private readonly IProviderDiagnosticSink _diagnostics;
    private readonly TimeSpan _timeout;
    private readonly CliWorkingDirectory _workingDirectory;

    public GrokCliUsageTransport(IProviderDiagnosticSink? diagnostics = null)
        : this(Environment.GetEnvironmentVariable("PATH"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            OperatingSystem.IsWindows(), diagnostics, DefaultTimeout,
            CliWorkingDirectory.DefaultStablePath("grok", diagnostics, ProviderIds.Grok)) { }

    internal GrokCliUsageTransport(string? path, string? homeDirectory, bool isWindows,
        IProviderDiagnosticSink? diagnostics, TimeSpan timeout, string? stableWorkingDirectory = null)
    {
        _path = path;
        _homeDirectory = homeDirectory;
        _isWindows = isWindows;
        _diagnostics = diagnostics ?? new NullProviderDiagnosticSink();
        _timeout = timeout;
        _workingDirectory = new CliWorkingDirectory(stableWorkingDirectory, "Glideslope-GrokUsage-", ProviderIds.Grok,
            "grok_usage", _diagnostics);
    }

    public async ValueTask<GrokUsageExchange> ReadAsync(CancellationToken cancellationToken)
    {
        var target = GrokExecutableLocator.Resolve(_path, _homeDirectory, _isWindows, _diagnostics);
        if (target is null) throw new GrokUsageTransportException(ProviderStatus.MissingApplication, "grok_cli_not_found");
        var lease = _workingDirectory.Prepare();
        if (lease is null) throw new GrokUsageTransportException(ProviderStatus.Offline, "grok_workdir_failed");
        var releaseLease = true;
        try { return await RunAsync(target, lease.Path, cancellationToken).ConfigureAwait(false); }
        catch (GrokOwnedProcessNotStoppedException)
        {
            releaseLease = false;
            throw;
        }
        finally
        {
            if (releaseLease) _workingDirectory.Release(lease);
        }
    }

    private async Task<GrokUsageExchange> RunAsync(CliLaunchTarget target, string workingDirectory, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = target.FileName, UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            WorkingDirectory = workingDirectory, StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var arg in target.LeadingArguments) process.StartInfo.ArgumentList.Add(arg);
        process.StartInfo.ArgumentList.Add("agent");
        process.StartInfo.ArgumentList.Add("--no-leader");
        process.StartInfo.ArgumentList.Add("stdio");
        process.StartInfo.Environment[DisableAutoUpdaterVariable] = "1";
        try
        {
            if (!process.Start()) throw new GrokUsageTransportException(ProviderStatus.Offline, "grok_process_start_failed");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            _diagnostics.Record(new ProviderDiagnostic("grok_launch_failed", ProviderIds.Grok,
                $"native_error={exception.NativeErrorCode.ToString(CultureInfo.InvariantCulture)}", 0));
            throw new GrokUsageTransportException(ProviderStatus.MissingApplication, "grok_cli_unavailable");
        }

        using var pipes = new CancellationTokenSource();
        var stderr = new PipeScan();
        var stderrTask = CliProcessSupport.DrainScanningAsync(process.StandardError, stderr, pipes.Token);
        using var timeout = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var processStopped = false;
        try
        {
            var initialize = await RequestAsync(process, 1, "initialize", new
            {
                protocolVersion = 1,
                clientInfo = new { name = "glideslope", version = "3.0.0" },
                clientCapabilities = new { }
            }, linked.Token).ConfigureAwait(false);
            if (initialize.ErrorCode is not null)
                throw new GrokUsageTransportException(ProviderStatus.SchemaChanged, "grok_initialize_failed");
            ValidateInitialize(initialize.ResultJson);

            var auth = await RequestAsync(process, 2, "_x.ai/auth/info", new { }, linked.Token).ConfigureAwait(false);
            if (auth.ErrorCode is not null)
                throw AuthMethodError(auth.ErrorCode.Value);
            _ = GrokUsageParser.ReadSubject(auth.ResultJson);
            var billing = await RequestAsync(process, 3, "_x.ai/billing", new { }, linked.Token).ConfigureAwait(false);
            if (billing.ErrorCode is not null)
                throw billing.ErrorCode switch
                {
                    -32601 => new GrokUsageTransportException(ProviderStatus.SchemaChanged, "grok_billing_unsupported"),
                    -32000 => new GrokUsageTransportException(ProviderStatus.NeedsSignIn, "grok_sign_in_required"),
                    _ => new GrokUsageTransportException(ProviderStatus.Offline, "grok_billing_unavailable")
                };
            return new GrokUsageExchange(initialize, auth, billing);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new GrokUsageTransportException(ProviderStatus.Offline, "grok_read_timeout");
        }
        catch (EndOfStreamException)
        {
            throw new GrokUsageTransportException(ProviderStatus.Offline, "grok_process_closed");
        }
        catch (IOException exception)
        {
            _diagnostics.Record(new ProviderDiagnostic("grok_transport_io", ProviderIds.Grok, exception.GetType().Name, 0));
            throw new GrokUsageTransportException(ProviderStatus.Offline, "grok_transport_io");
        }
        catch (JsonException)
        {
            throw new GrokUsageTransportException(ProviderStatus.SchemaChanged, "grok_protocol_invalid");
        }
        finally
        {
            try { process.StandardInput.Close(); } catch (IOException) { }
            try
            {
                if (!process.HasExited)
                {
                    using var shutdown = new CancellationTokenSource(ShutdownTimeout);
                    try { await process.WaitForExitAsync(shutdown.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(ShutdownTimeout).ConfigureAwait(false);
                    }
                }
                processStopped = process.HasExited;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                _diagnostics.Record(new ProviderDiagnostic("grok_process_stop_failed", ProviderIds.Grok, exception.GetType().Name, 0));
                throw new GrokOwnedProcessNotStoppedException(exception);
            }
            finally
            {
                var drain = await CliProcessSupport.AwaitPipesAfterExitAsync(pipes, stderrTask).ConfigureAwait(false);
                if (drain.HeldOpen)
                    _diagnostics.Record(new ProviderDiagnostic("grok_stderr_held_after_exit", ProviderIds.Grok, "drain_cancelled", 0));
            }
            if (!processStopped) throw new GrokOwnedProcessNotStoppedException();
        }
    }

    internal static void ValidateInitialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("protocolVersion", out var protocol) ||
            protocol.ValueKind != JsonValueKind.Number || !protocol.TryGetInt32(out var version) || version != 1)
            throw new GrokUsageTransportException(ProviderStatus.SchemaChanged, "grok_protocol_version_unsupported");
    }

    private static GrokUsageTransportException AuthMethodError(int code) => code switch
    {
        -32601 => new GrokUsageTransportException(ProviderStatus.SchemaChanged, "grok_auth_info_unsupported"),
        -32000 => new GrokUsageTransportException(ProviderStatus.NeedsSignIn, "grok_auth_unavailable"),
        _ => new GrokUsageTransportException(ProviderStatus.Offline, "grok_auth_info_unavailable")
    };

    private static async Task<GrokRpcReply> RequestAsync(Process process, int id, string method, object parameters,
        CancellationToken cancellationToken)
    {
        var frame = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
        if (frame.Length > MaximumFrameCharacters) throw new JsonException("Request exceeds protocol frame limit.");
        await process.StandardInput.WriteLineAsync(frame.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var line = await ReadBoundedLineAsync(process.StandardOutput, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !IsJsonRpc(root)) throw new JsonException("Invalid JSON-RPC frame.");
            if (root.TryGetProperty("method", out _))
            {
                if (root.TryGetProperty("id", out var serverId))
                {
                    var rejection = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = serverId, error = new { code = -32601, message = "Unsupported client request" } });
                    await process.StandardInput.WriteLineAsync(rejection.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                continue;
            }
            if (!root.TryGetProperty("id", out var responseId) || !IdMatches(responseId, id)) continue;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind != JsonValueKind.Object || !error.TryGetProperty("code", out var codeValue) ||
                    codeValue.ValueKind != JsonValueKind.Number || !codeValue.TryGetInt32(out var code))
                    throw new JsonException("Invalid JSON-RPC error.");
                return new GrokRpcReply("{}", code);
            }
            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) throw new JsonException("Missing JSON-RPC result.");
            return new GrokRpcReply(result.GetRawText(), null);
        }
    }

    private static async Task<string> ReadBoundedLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(1024);
        var one = new char[1];
        while (true)
        {
            var count = await reader.ReadAsync(one.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException();
            if (one[0] == '\n') return builder.ToString().TrimEnd('\r');
            if (builder.Length >= MaximumFrameCharacters) throw new JsonException("JSON-RPC frame is too large.");
            builder.Append(one[0]);
        }
    }

    private static bool IsJsonRpc(JsonElement root) => root.TryGetProperty("jsonrpc", out var version) &&
        version.ValueKind == JsonValueKind.String && version.GetString() == "2.0";

    private static bool IdMatches(JsonElement response, int expected) =>
        response.ValueKind == JsonValueKind.Number && response.TryGetInt32(out var value) && value == expected;
}

public sealed class GrokUsageTransportException(ProviderStatus status, string safeErrorCode) : Exception
{
    public ProviderStatus Status { get; } = status;
    public string SafeErrorCode { get; } = safeErrorCode;
}

internal sealed class GrokOwnedProcessNotStoppedException(Exception? inner = null)
    : InvalidOperationException("The owned Grok CLI did not stop; its temporary working directory was retained.", inner);

public static class GrokExecutableLocator
{
    public static string? Find(string? path, string? homeDirectory, bool isWindows)
    {
        var names = isWindows ? new[] { "grok.exe", "grok.cmd" } : new[] { "grok" };
        if (!string.IsNullOrWhiteSpace(path))
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                foreach (var name in names)
                {
                    var candidate = Path.Combine(directory, name);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
        if (!string.IsNullOrWhiteSpace(homeDirectory))
        {
            var bin = Path.Combine(homeDirectory, ".grok", "bin");
            foreach (var name in names)
            {
                var candidate = Path.Combine(bin, name);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }
        return null;
    }

    public static CliLaunchTarget? Resolve(string? path, string? homeDirectory, bool isWindows, IProviderDiagnosticSink? diagnostics)
    {
        var found = Find(path, homeDirectory, isWindows);
        if (found is null) return null;
        return found.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? NpmShim.Resolve(found, "@xai-official/grok", "grok", path, ProviderIds.Grok, diagnostics)
            : CliLaunchTarget.Native(found, ProviderIds.Grok, diagnostics);
    }
}
