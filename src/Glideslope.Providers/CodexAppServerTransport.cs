using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Glideslope.Domain;

namespace Glideslope.Providers;

public sealed record CodexAppServerResponse(string AccountJson, string RateLimitsJson);

public interface ICodexAppServerTransport
{
    ValueTask<CodexAppServerResponse> ReadAsync(CancellationToken cancellationToken);
}

public sealed partial class CodexAppServerTransport : ICodexAppServerTransport
{
    private const int MaximumFrameCharacters = 1024 * 1024;
    private const string FallbackClientVersion = "1.0.0";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);
    // One timeout covers initialize, account/read, and rate-limits requests.
    private static readonly TimeSpan DefaultExchangeTimeout = StartupTimeout + ReadTimeout * 3;

    /// <summary>Maximum duration including process shutdown and bounded stderr draining.</summary>
    public static readonly TimeSpan WorstCaseReadDuration =
        DefaultExchangeTimeout + ShutdownTimeout + ShutdownTimeout + CliProcessSupport.PipeDrainAfterExit;

    private readonly string? _path;
    private readonly string? _homeDirectory;
    private readonly bool _isWindows;
    private readonly string? _localApplicationData;
    private readonly string? _roamingApplicationData;
    private readonly IProviderDiagnosticSink _diagnostics;
    private readonly TimeSpan _exchangeTimeout;

    public CodexAppServerTransport()
        : this((IProviderDiagnosticSink?)null)
    {
    }

    /// <summary>Production entry point with provider diagnostics.</summary>
    public CodexAppServerTransport(IProviderDiagnosticSink? diagnostics)
        : this(Environment.GetEnvironmentVariable("PATH"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            OperatingSystem.IsWindows(), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), diagnostics, DefaultExchangeTimeout)
    {
    }

    public CodexAppServerTransport(string? path, string? homeDirectory, bool isWindows, string? localApplicationData = null)
        : this(path, homeDirectory, isWindows, localApplicationData, null, null, DefaultExchangeTimeout)
    {
    }

    internal CodexAppServerTransport(string? path, string? homeDirectory, bool isWindows, string? localApplicationData,
        string? roamingApplicationData, IProviderDiagnosticSink? diagnostics, TimeSpan exchangeTimeout)
    {
        _path = path;
        _homeDirectory = homeDirectory;
        _isWindows = isWindows;
        _localApplicationData = localApplicationData;
        _roamingApplicationData = roamingApplicationData;
        _diagnostics = diagnostics ?? new NullProviderDiagnosticSink();
        _exchangeTimeout = exchangeTimeout;
    }

    /// <summary>
    /// Reports the running app's entry-assembly informational version in the app-server's initialize clientInfo,
    /// without any "+build" metadata. Falls back to "1.0.0" when the version is unavailable.
    /// </summary>
    internal static string ClientVersion(Assembly? entryAssembly)
    {
        var informational = entryAssembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational)) return FallbackClientVersion;
        var plus = informational.IndexOf('+');
        var trimmed = (plus >= 0 ? informational[..plus] : informational).Trim();
        return trimmed.Length == 0 ? FallbackClientVersion : trimmed;
    }

    public async ValueTask<CodexAppServerResponse> ReadAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var launch = CodexExecutableLocator.Resolve(_path, _homeDirectory, _isWindows, _localApplicationData,
            _roamingApplicationData, _diagnostics);
        if (launch is null)
        {
            Record("codex_app_server_finished", "outcome=codex_cli_missing", started);
            throw new CodexAppServerTransportException(ProviderStatus.MissingApplication, "codex_cli_missing");
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = launch.FileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(_homeDirectory) ? Environment.CurrentDirectory : _homeDirectory
        };
        // For npm installs, node runs the package's entry script before the CLI arguments.
        foreach (var argument in launch.LeadingArguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.ArgumentList.Add("app-server");
        process.StartInfo.ArgumentList.Add("--listen");
        process.StartInfo.ArgumentList.Add("stdio://");
        process.StartInfo.StandardInputEncoding = new UTF8Encoding(false);
        process.StartInfo.StandardOutputEncoding = new UTF8Encoding(false);
        process.StartInfo.StandardErrorEncoding = new UTF8Encoding(false);

        try
        {
            if (!process.Start())
            {
                Record("codex_app_server_finished", "outcome=codex_process_start_failed", started);
                throw new CodexAppServerTransportException(ProviderStatus.Offline, "codex_process_start_failed");
            }
        }
        catch (CodexAppServerTransportException) { throw; }
        catch (System.ComponentModel.Win32Exception exception)
        {
            Record("codex_app_server_finished",
                $"outcome=codex_cli_unavailable,native_error={exception.NativeErrorCode.ToString(CultureInfo.InvariantCulture)}", started);
            throw new CodexAppServerTransportException(ProviderStatus.MissingApplication, "codex_cli_unavailable");
        }

        // A descendant can inherit stderr and outlive codex, so the drain is cancellable and bounded after exit.
        using var pipes = new CancellationTokenSource();
        var stderrScan = new PipeScan();
        var stderrTask = CliProcessSupport.DrainScanningAsync(process.StandardError, stderrScan, pipes.Token);
        using var timeout = new CancellationTokenSource(_exchangeTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var outcome = "ok";
        try
        {
            await WriteRequestAsync(process, 1, "initialize", new
            {
                clientInfo = new { name = "glideslope", title = "Glideslope", version = ClientVersion(Assembly.GetEntryAssembly()) },
                capabilities = new { }
            }, linked.Token).ConfigureAwait(false);
            _ = await ReadResultAsync(process, 1, "initialize", linked.Token).ConfigureAwait(false);
            await WriteNotificationAsync(process, "initialized", new { }, linked.Token).ConfigureAwait(false);

            await WriteRequestAsync(process, 2, "account/read", new { refreshToken = false }, linked.Token).ConfigureAwait(false);
            var account = await ReadResultAsync(process, 2, "account/read", linked.Token).ConfigureAwait(false);

            await WriteRequestAsync(process, 3, "account/rateLimits/read", new { }, linked.Token).ConfigureAwait(false);
            var limits = await ReadResultAsync(process, 3, "account/rateLimits/read", linked.Token).ConfigureAwait(false);
            return new CodexAppServerResponse(account, limits);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "cancelled";
            throw;
        }
        catch (OperationCanceledException)
        {
            outcome = "codex_app_server_timeout";
            throw new CodexAppServerTransportException(ProviderStatus.Offline, "codex_app_server_timeout");
        }
        catch (CodexAppServerTransportException exception)
        {
            outcome = exception.SafeErrorCode;
            throw;
        }
        catch (EndOfStreamException)
        {
            outcome = "codex_app_server_eof";
            throw new CodexAppServerTransportException(ProviderStatus.Offline, "codex_app_server_eof");
        }
        catch (IOException exception)
        {
            outcome = $"codex_app_server_io,{exception.GetType().Name}";
            throw new CodexAppServerTransportException(ProviderStatus.Offline, "codex_app_server_io");
        }
        catch (JsonException)
        {
            outcome = "codex_app_server_protocol";
            throw new CodexAppServerTransportException(ProviderStatus.SchemaChanged, "codex_app_server_protocol");
        }
        finally
        {
            var stop = "unknown";
            try
            {
                stop = await StopOwnedProcessAsync(process).ConfigureAwait(false);
            }
            finally
            {
                var drain = await CliProcessSupport.AwaitPipesAfterExitAsync(pipes, stderrTask).ConfigureAwait(false);
                if (drain.HeldOpen) Record("codex_app_server_stderr_held", "helper_holds_stderr,drain_cancelled", started);
                if (drain.FaultTypeName is { } fault) Record("codex_app_server_stderr_read_failed", fault, started);
                Record("codex_app_server_finished",
                    $"outcome={outcome},stop={stop},exit={ExitCodeText(process)},stderr_chars={stderrScan.Characters},launch={launch.Kind}",
                    started);
            }
        }
    }

    private async Task<string> ReadResultAsync(Process process, int expectedId, string method, CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await ReadFrameAsync(process.StandardOutput, cancellationToken).ConfigureAwait(false);
            using var message = JsonDocument.Parse(frame);
            var root = message.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new CodexAppServerTransportException(ProviderStatus.SchemaChanged, "codex_app_server_protocol");
            if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var responseId))
                continue;
            if (responseId != expectedId) continue;
            if (root.TryGetProperty("error", out var error))
            {
                // Classify authentication failures for the sign-in notice; log only the class and numeric code.
                var classified = ClassifyRequestError(error);
                Record("codex_app_server_request_error",
                    $"method={method},class={classified.Classification},code={classified.Code?.ToString(CultureInfo.InvariantCulture) ?? "none"}", 0);
                throw new CodexAppServerTransportException(classified.Status, classified.SafeErrorCode);
            }
            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                throw new CodexAppServerTransportException(ProviderStatus.SchemaChanged, "codex_app_server_protocol");
            return result.GetRawText();
        }
    }

    /// <summary>A JSON-RPC error whose code is 401, or whose text (message or data)
    /// talks about an unauthorized, unauthenticated or expired sign-in, is AuthenticationExpired
    /// (codex_app_server_auth_expired), so the card shows the sign-in notice. Anything else stays Offline
    /// codex_app_server_request_failed.</summary>
    internal static (ProviderStatus Status, string SafeErrorCode, string Classification, long? Code) ClassifyRequestError(JsonElement error)
    {
        long? code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var codeElement) &&
                     codeElement.ValueKind == JsonValueKind.Number && codeElement.TryGetInt64(out var number)
            ? number
            : null;
        return code == 401 || AuthenticationWording().IsMatch(error.GetRawText())
            ? (ProviderStatus.AuthenticationExpired, "codex_app_server_auth_expired", "auth", code)
            : (ProviderStatus.Offline, "codex_app_server_request_failed", "other", code);
    }

    [GeneratedRegex(@"\b401\b|unauthori[sz]ed|unauthenticated|not\s+authenticated|authentication|(?:token|session|credentials?|login|sign[\s-]?in)\s+(?:has\s+|is\s+)?expired|expired\s+(?:token|session|credentials?|login)|not\s+(?:logged|signed)\s+in|(?:log|sign)\s*in\s+(?:again|required)|invalid_grant|refresh\s+token",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthenticationWording();

    private static async Task WriteRequestAsync(Process process, int id, string method, object parameters, CancellationToken token)
    {
        var json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
        await process.StandardInput.WriteLineAsync(json.AsMemory(), token).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task WriteNotificationAsync(Process process, string method, object parameters, CancellationToken token)
    {
        var json = JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters });
        await process.StandardInput.WriteLineAsync(json.AsMemory(), token).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<string> ReadFrameAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(1024);
        var one = new char[1];
        while (true)
        {
            var read = await reader.ReadAsync(one.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            if (one[0] == '\n') return builder.ToString().TrimEnd('\r');
            if (builder.Length >= MaximumFrameCharacters)
                throw new CodexAppServerTransportException(ProviderStatus.SchemaChanged, "codex_app_server_frame_too_large");
            builder.Append(one[0]);
        }
    }

    /// <summary>Closes stdin and waits for the app-server to exit, killing it after <see cref="ShutdownTimeout"/>.
    /// Returns how it stopped ("exited", "stdin_closed", "killed", "stop_failed") for the finished log.</summary>
    private async Task<string> StopOwnedProcessAsync(Process process)
    {
        try
        {
            if (process.HasExited) return "exited";
            await process.StandardInput.DisposeAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().WaitAsync(ShutdownTimeout).ConfigureAwait(false);
            return "stdin_closed";
        }
        catch (TimeoutException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Record("codex_app_server_kill_failed", exception.GetType().Name, 0);
            }
            try { await process.WaitForExitAsync().WaitAsync(ShutdownTimeout).ConfigureAwait(false); }
            catch (TimeoutException exception)
            {
                throw new InvalidOperationException("The owned Codex app-server process did not exit after stdin closed.", exception);
            }
            return "killed";
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            Record("codex_app_server_stop_failed", exception.GetType().Name, 0);
            return "stop_failed";
        }
    }

    private string ExitCodeText(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode.ToString(CultureInfo.InvariantCulture) : "running";
        }
        catch (InvalidOperationException exception)
        {
            Record("codex_app_server_exit_unknown", exception.GetType().Name, 0);
            return "unknown";
        }
    }

    private void Record(string code, string status, long started) =>
        _diagnostics.Record(new ProviderDiagnostic(code, ProviderIds.Codex, status,
            started == 0 ? 0 : Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
}

public sealed class CodexAppServerTransportException(ProviderStatus status, string safeErrorCode) : Exception
{
    public ProviderStatus Status { get; } = status;
    public string SafeErrorCode { get; } = safeErrorCode;
}

public static class CodexExecutableLocator
{
    /// <summary>On Windows, a PATH directory's codex.exe is preferred to its npm shim
    /// codex.cmd (the PATHEXT order), and %APPDATA%\npm\codex.cmd, npm's default global prefix, is checked last.</summary>
    public static string? Find(string? path, string? homeDirectory, bool isWindows, string? localApplicationData = null,
        string? roamingApplicationData = null)
    {
        var executableNames = isWindows ? new[] { "codex.exe", "codex.cmd" } : new[] { "codex" };
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var name in executableNames)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }

        // The Windows installer uses versioned directories and may not update PATH; prefer the newest executable.
        if (isWindows && !string.IsNullOrWhiteSpace(localApplicationData))
        {
            var installRoot = Path.Combine(localApplicationData, "OpenAI", "Codex", "bin");
            try
            {
                if (Directory.Exists(installRoot))
                {
                    var newest = Directory.EnumerateDirectories(installRoot)
                        .Select(directory => new FileInfo(Path.Combine(directory, "codex.exe")))
                        .Where(file => file.Exists)
                        .OrderByDescending(file => file.LastWriteTimeUtc)
                        .FirstOrDefault();
                    if (newest is not null) return newest.FullName;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An unreadable install directory falls through to the remaining locations.
            }
        }

        if (!string.IsNullOrWhiteSpace(homeDirectory))
        {
            var candidate = Path.Combine(homeDirectory, ".local", "bin", executableNames[0]);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        if (isWindows && !string.IsNullOrWhiteSpace(roamingApplicationData))
        {
            var npmShim = Path.Combine(roamingApplicationData, "npm", "codex.cmd");
            if (File.Exists(npmShim)) return Path.GetFullPath(npmShim);
        }
        return null;
    }

    /// <summary>Describes how to launch the codex executable located by <see cref="Find"/>. An npm shim is
    /// never run through cmd.exe (see NpmShim); node runs the @openai/codex package's own entry script.</summary>
    public static CliLaunchTarget? Resolve(string? path, string? homeDirectory, bool isWindows, string? localApplicationData,
        string? roamingApplicationData, IProviderDiagnosticSink? diagnostics)
    {
        var found = Find(path, homeDirectory, isWindows, localApplicationData, roamingApplicationData);
        if (found is null) return null;
        return found.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? NpmShim.Resolve(found, "@openai/codex", "codex", path, ProviderIds.Codex, diagnostics)
            : CliLaunchTarget.Native(found, ProviderIds.Codex, diagnostics);
    }
}
