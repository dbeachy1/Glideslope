using System.Diagnostics;
using System.Net;
using System.Text;
using Glideslope.Domain;
using Glideslope.Providers;
using Glideslope.Storage;

internal static class Program
{
    private const string TempPrefix = "Glideslope-ClaudeProviders-providers-v1-";
    private const string OwnerMarker = "glideslope-provider-specs-v1";
    private static async Task<int> Main(string[] args)
    {
        // Check the pipe-holder marker first because a holder inherits its fake CLI's environment markers.
        if (Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_PIPE_HOLDER") == "1")
            return RunPipeHolder();
        // The Codex transport runs this binary as codex.exe and speaks app-server JSON-RPC over stdio.
        if (Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_CODEX_CHILD") == "1")
            return RunFakeCodexChild();
        // Checked before any argument parsing: this same compiled binary also stands in for the real
        // `claude` executable in ClaudeCliSpecs.TransportProcessSpecsAsync. The transport always passes its own
        // fixed argument list, so behavior here is selected out of band
        // by environment variables the parent spec process sets, never by argv.
        if (Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_CLAUDE_CHILD") == "1")
            return RunFakeClaudeChild(args);
        // A separate env var from the Claude fake above: AntigravitySpecs relaunches this same binary as
        // "agy.exe" for its own process-level transport specs, and the two fakes must never interfere.
        if (Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_CHILD") == "1")
            return RunFakeAntigravityChild(args);

        if (args.Length == 5 && args[0] == "--scope-child")
            return await RunScopeChildAsync(args[1], args[2], args[3], args[4]).ConfigureAwait(false);
        if (args.Length == 2 && args[0] == "--live-codex-child")
            return await RunLiveCodexChildAsync(args[1]).ConfigureAwait(false);

        using var temp = OwnedTempRoot.Create();
        if (args.Length == 1 && args[0] == "--live-claude")
            return await ClaudeCliSpecs.RunLiveClaudeAsync(temp.Path).ConfigureAwait(false);
        if (args.Length == 1 && args[0] == "--live-antigravity")
            return await RunLiveAntigravityAsync(temp.Path).ConfigureAwait(false);
        if (args.Length == 1 && args[0] == "--live-codex")
            return await RunLiveCodexAsync(temp.Path).ConfigureAwait(false);
        if (args.Length == 1 && args[0] == "--live-codex-shape")
            return await RunLiveCodexShapeAsync().ConfigureAwait(false);

        TimeoutBudgetSpecs();
        ClaudeCliSpecs.ParserSpecs();
        CodexParserSpecs();
        CodexTransportUnitSpecs();
        await ClaudeCliSpecs.ProviderSpecsAsync().ConfigureAwait(false);
        await CodexProviderSpecsAsync().ConfigureAwait(false);
        await ScopeStorageSpecsAsync(temp.Path).ConfigureAwait(false);
        ExecutableDiscoverySpecs(temp.Path);
        CliWorkingDirectorySpecs(temp.Path);
        await ClaudeCliSpecs.TransportProcessSpecsAsync(temp.Path).ConfigureAwait(false);
        await CodexTransportProcessSpecsAsync(temp.Path).ConfigureAwait(false);
        await AntigravitySpecs.RunAllAsync(temp.Path).ConfigureAwait(false);
        Console.WriteLine("Provider and account-scope specifications passed.");
        return 0;
    }

    /// <summary>Fake-CLI entry point: this same binary is relaunched as "claude.exe" by the process-level
    /// CLI transport specs. Behavior comes from environment variables, never from argv (see the dispatch above),
    /// so it works unchanged no matter what fixed argument list the renewer under test passes.</summary>
    private static int RunFakeClaudeChild(string[] args)
    {
        var argsMarker = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_CLAUDE_ARGS_MARKER");
        if (!string.IsNullOrEmpty(argsMarker))
        {
            File.WriteAllText(argsMarker, string.Join('\u001f', args), Encoding.UTF8);
            // Record the updater switch passed to the fake CLI.
            File.WriteAllText(argsMarker + ".autoupdate", Environment.GetEnvironmentVariable("DISABLE_AUTOUPDATER") ?? "<unset>", Encoding.UTF8);
        }
        var cwdMarker = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_CLAUDE_CWD_MARKER");
        if (!string.IsNullOrEmpty(cwdMarker))
            File.WriteAllText(cwdMarker, Environment.CurrentDirectory, Encoding.UTF8);
        // Record how many entries the working directory contains when the CLI starts.
        var entriesMarker = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_CLAUDE_CWD_ENTRIES_MARKER");
        if (!string.IsNullOrEmpty(entriesMarker))
            File.WriteAllText(entriesMarker, Directory.EnumerateFileSystemEntries(Environment.CurrentDirectory).Count()
                .ToString(System.Globalization.CultureInfo.InvariantCulture), Encoding.UTF8);
        var holderMarker = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_CLAUDE_HOLDER_PID_MARKER");
        if (!string.IsNullOrEmpty(holderMarker))
            SpawnPipeHolder(holderMarker);

        // Stand-in output for the real CLI's JSON usage envelope: the spec points this at a file so the
        // transport under test captures exactly what the file holds (or an oversized flood).
        // The real CLI (Node) writes UTF-8 whatever the console code page; the transport reads UTF-8.
        Console.OutputEncoding = Encoding.UTF8;
        var stdoutFile = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_CLAUDE_STDOUT_FILE");
        if (!string.IsNullOrEmpty(stdoutFile) && File.Exists(stdoutFile))
            Console.Out.Write(File.ReadAllText(stdoutFile));
        var oversizedText = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_CLAUDE_OVERSIZED_BYTES");
        if (int.TryParse(oversizedText, out var oversized) && oversized > 0)
            Console.Out.Write(new string('x', oversized));
        // Specs select stderr text, including login wording, through this environment variable.
        var claudeStderr = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_CLAUDE_STDERR_TEXT");
        Console.Error.WriteLine(string.IsNullOrEmpty(claudeStderr) ? "synthetic fake-claude usage stderr line" : claudeStderr);

        if (Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_CLAUDE_HANG") == "1")
            Thread.Sleep(Timeout.Infinite); // Simulate a wedged CLI so the renewer's kill-on-timeout path runs.

        var exitCodeText = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_CLAUDE_EXIT_CODE");
        return int.TryParse(exitCodeText, out var exitCode) ? exitCode : 0;
    }

    /// <summary>Fake-CLI entry point: this same binary is relaunched as "agy.exe" by
    /// AntigravitySpecs' own process-level transport specs. A separate set of environment variables from
    /// the Claude fake above keeps the two completely independent.</summary>
    private static int RunFakeAntigravityChild(string[] args)
    {
        var argsMarker = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_ARGS_MARKER");
        if (!string.IsNullOrEmpty(argsMarker))
        {
            File.WriteAllText(argsMarker, string.Join('\u001f', args), Encoding.UTF8);
            // Record the updater switch passed to the fake CLI.
            File.WriteAllText(argsMarker + ".autoupdate", Environment.GetEnvironmentVariable("AGY_CLI_DISABLE_AUTO_UPDATE") ?? "<unset>", Encoding.UTF8);
        }
        var cwdMarker = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_CWD_MARKER");
        if (!string.IsNullOrEmpty(cwdMarker))
            File.WriteAllText(cwdMarker, Environment.CurrentDirectory, Encoding.UTF8);
        // Record how many entries the working directory contains when agy starts.
        var entriesMarker = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_CWD_ENTRIES_MARKER");
        if (!string.IsNullOrEmpty(entriesMarker))
            File.WriteAllText(entriesMarker, Directory.EnumerateFileSystemEntries(Environment.CurrentDirectory).Count()
                .ToString(System.Globalization.CultureInfo.InvariantCulture), Encoding.UTF8);

        // The fake writes UTF-8 regardless of the console code page, matching agy.
        Console.OutputEncoding = Encoding.UTF8;
        var stdoutFile = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_STDOUT_FILE");
        if (!string.IsNullOrEmpty(stdoutFile) && File.Exists(stdoutFile))
            Console.Out.Write(File.ReadAllText(stdoutFile));
        var oversizedBytesText = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_OVERSIZED_BYTES");
        if (int.TryParse(oversizedBytesText, out var oversizedBytes) && oversizedBytes > 0)
            Console.Out.Write(new string('x', oversizedBytes));
        // Specs select stderr text, including a Google sign-in URL, through this environment variable.
        var agyStderr = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_STDERR_TEXT");
        Console.Error.WriteLine(string.IsNullOrEmpty(agyStderr) ? "synthetic fake-agy usage stderr line" : agyStderr);

        // Signals the spec after every marker and output line has been flushed, so a hanging fake can be
        // timed out without racing startup. Console.Out and Console.Error flush on each write.
        var readyEvent = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_READY_EVENT");
        if (!string.IsNullOrEmpty(readyEvent) && OperatingSystem.IsWindows())
        {
            using var ready = EventWaitHandle.OpenExisting(readyEvent);
            ready.Set();
        }

        if (Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_HANG") == "1")
            Thread.Sleep(Timeout.Infinite); // Simulate a wedged CLI so the transport's kill-on-timeout path runs.

        var exitCodeText = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_EXIT_CODE");
        return int.TryParse(exitCodeText, out var exitCode) ? exitCode : 0;
    }

    /// <summary>A child process that inherits a fake CLI's output pipes and remains alive until the spec stops it.</summary>
    private static int RunPipeHolder()
    {
        Thread.Sleep(TimeSpan.FromSeconds(60));
        return 0;
    }

    /// <summary>Starts this binary as a pipe holder. Redirecting only stdin gives it the fake CLI's output pipes;
    /// writes its process id to <paramref name="pidMarker"/> for scoped cleanup.</summary>
    internal static void SpawnPipeHolder(string pidMarker)
    {
        var startInfo = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("No process path."))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        startInfo.Environment["GLIDESLOPE_FAKE_PIPE_HOLDER"] = "1";
        using var holder = Process.Start(startInfo) ?? throw new InvalidOperationException("The pipe holder did not start.");
        File.WriteAllText(pidMarker, holder.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), Encoding.UTF8);
    }

    /// <summary>Stops the pipe holder a spec started: the process with the recorded id, and only if its image has the
    /// expected file name (never by image name alone).</summary>
    internal static void StopPipeHolder(string pidMarker, string expectedFileName)
    {
        if (!File.Exists(pidMarker)) throw new InvalidOperationException("Specification failed: the pipe holder was never started.");
        var pid = int.Parse(File.ReadAllText(pidMarker), System.Globalization.CultureInfo.InvariantCulture);
        Process holder;
        try { holder = Process.GetProcessById(pid); }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("Pipe holder had already exited.");
            return;
        }
        using (holder)
        {
            if (!string.Equals(Path.GetFileName(holder.MainModule?.FileName), expectedFileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Specification failed: the recorded pipe holder id belongs to another program.");
            holder.Kill();
            holder.WaitForExit(5000);
        }
    }

    /// <summary>Runs this binary as codex.exe and implements the app-server JSON-RPC methods used by
    /// CodexAppServerTransport. It exits when stdin closes and can start a pipe holder for drain tests.</summary>
    private static int RunFakeCodexChild()
    {
        var holderMarker = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_CODEX_HOLDER_PID_MARKER");
        if (!string.IsNullOrEmpty(holderMarker)) SpawnPipeHolder(holderMarker);
        Console.OutputEncoding = new UTF8Encoding(false);
        var rateLimitsError = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_CODEX_RATE_LIMITS_ERROR");
        string? line;
        while ((line = Console.In.ReadLine()) is not null)
        {
            using var request = System.Text.Json.JsonDocument.Parse(line);
            var root = request.RootElement;
            if (!root.TryGetProperty("id", out var id)) continue;   // the "initialized" notification
            var idText = id.GetRawText();
            var method = root.GetProperty("method").GetString();
            var head = """{"jsonrpc":"2.0","id":""" + idText;
            var response = method switch
            {
                "initialize" => head + ""","result":{"userAgent":"fake-codex"}}""",
                "account/read" => head + ""","result":{"account":{"type":"chatgpt","email":"synthetic@example.invalid","planType":"team"}}}""",
                "account/rateLimits/read" when !string.IsNullOrEmpty(rateLimitsError) => head + ""","error":""" + rateLimitsError + "}",
                "account/rateLimits/read" =>
                    head + ""","result":{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":17,"windowDurationMins":10080,"resetsAt":1791036000},"secondary":null}}}}""",
                _ => head + ""","error":{"code":-32601,"message":"unknown method"}}"""
            };
            Console.Out.WriteLine(response);
            Console.Out.Flush();
        }
        Console.Error.WriteLine("synthetic fake-codex stderr line");
        return 0;
    }

    /// <summary>Checks provider timeouts fit within scheduler limits and disposal allows enough time to kill and drain.</summary>
    private static void TimeoutBudgetSpecs()
    {
        Equal(true, ClaudeAuthContextReader.WorstCaseRunDuration + ClaudeCliUsageTransport.WorstCaseRunDuration < ClaudeUsageProvider.ReadTimeout,
            "the Claude auth-status and usage runs both finish inside the provider's backstop");
        Equal(true, ClaudeUsageProvider.WorstCaseReadDuration < Glideslope.Monitoring.ProviderScheduler.DefaultReadTimeout,
            "the Claude provider finishes before the scheduler's read timeout");
        Equal(true, CodexAppServerTransport.WorstCaseReadDuration < Glideslope.Monitoring.ProviderScheduler.DefaultReadTimeout,
            "the Codex exchange times out before the scheduler's read timeout");
        Equal(true, AntigravityCliUsageTransport.WorstCaseRunDuration < Glideslope.Monitoring.ProviderScheduler.DefaultReadTimeout,
            "the agy run times out before the scheduler's read timeout");
        // A silent Antigravity hang is retried once; the prechecks are local file reads and add no process time.
        Equal(true, AntigravityCliUsageTransport.WorstCaseRunDuration * 2 < Glideslope.Monitoring.ProviderScheduler.DefaultReadTimeout,
            "two Antigravity runs (a silent hang's immediate retry) plus the local-file-only prechecks fit inside the scheduler's read timeout");
        Equal(true, Glideslope.Monitoring.ProviderScheduler.DisposeReadDrainTimeout >= ClaudeCliUsageTransport.KillAndDrainAllowance,
            "dispose waits at least as long as a Claude kill and drain");
    }

    /// <summary>Exercises provider error classification and the initialize clientInfo version.</summary>
    private static void CodexTransportUnitSpecs()
    {
        static (ProviderStatus Status, string Code, string Class, long? RpcCode) Classify(string json)
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return CodexAppServerTransport.ClassifyRequestError(document.RootElement);
        }
        var expired = Classify("""{"code":-32001,"message":"Unauthorized: token expired"}""");
        Equal(ProviderStatus.AuthenticationExpired, expired.Status, "an unauthorized, expired-token error asks for sign-in");
        Equal("codex_app_server_auth_expired", expired.Code, "auth-expired safe code");
        Equal("auth", expired.Class, "auth classification");
        Equal<long?>(-32001, expired.RpcCode, "the numeric JSON-RPC code is kept for the log");
        Equal(ProviderStatus.AuthenticationExpired, Classify("""{"code":401,"message":"x"}""").Status, "code 401 is an authentication error");
        Equal(ProviderStatus.AuthenticationExpired, Classify("""{"code":-32000,"message":"request failed","data":{"reason":"not logged in"}}""").Status,
            "login wording in the error data counts");
        Equal(ProviderStatus.Offline, Classify("""{"code":-32603,"message":"internal error"}""").Status, "an internal error stays Offline");
        Equal("codex_app_server_request_failed", Classify("""{"code":-32000,"message":"rate limit window closed, port 4010"}""").Code,
            "other errors keep the request-failed code");

        Equal("1.0.0", CodexAppServerTransport.ClientVersion(null), "no entry assembly falls back to 1.0.0");
        var name = new System.Reflection.AssemblyName("GlideslopeClientVersionFixture");
        var builder = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(name, System.Reflection.Emit.AssemblyBuilderAccess.Run);
        builder.SetCustomAttribute(new System.Reflection.Emit.CustomAttributeBuilder(
            typeof(System.Reflection.AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!, ["1.1.0+0123abcd"]));
        Equal("1.1.0", CodexAppServerTransport.ClientVersion(builder), "the informational version is reported without +metadata");
        var bare = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new System.Reflection.AssemblyName("GlideslopeBareFixture"),
            System.Reflection.Emit.AssemblyBuilderAccess.Run);
        Equal("1.0.0", CodexAppServerTransport.ClientVersion(bare), "an assembly without an informational version falls back to 1.0.0");
    }

    /// <summary>Runs the production Codex transport against this binary posing as codex.exe.</summary>
    private static async Task CodexTransportProcessSpecsAsync(string root)
    {
        var fixture = Path.Combine(root, "fake-codex-fixture");
        CopyDirectoryRecursive(AppContext.BaseDirectory, fixture);
        File.Copy(Path.Combine(fixture, "Glideslope.Providers.Specs.exe"), Path.Combine(fixture, "codex.exe"), overwrite: true);

        // A helper that inherits stderr cannot hold the read past the bounded drain.
        var holderMarker = Path.Combine(root, "fake-codex-holder-pid.txt");
        var diagnostics = new CapturingDiagnostics();
        try
        {
            await RunFakeCodexScenarioAsync(holderMarker, null, async () =>
            {
                var transport = new CodexAppServerTransport(fixture, null, true, null, null, diagnostics, TimeSpan.FromSeconds(60));
                var stopwatch = Stopwatch.StartNew();
                var response = await transport.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                stopwatch.Stop();
                var parsed = CodexUsageParser.Parse(response.AccountJson, response.RateLimitsJson, DateTimeOffset.UtcNow);
                Equal("codex.weekly", parsed.Buckets[0].Id, "the fake app-server's limits parse");
                if (stopwatch.Elapsed > TimeSpan.FromSeconds(20))
                    throw new InvalidOperationException("Specification failed: the Codex stderr drain was not bounded after exit.");
            }).ConfigureAwait(false);
            Equal(1, diagnostics.Events.Count(e => e.Code == "codex_app_server_stderr_held"), "a held stderr is logged");
            Equal(1, diagnostics.Events.Count(e => e.Code == "codex_app_server_finished" && e.Status.StartsWith("outcome=ok,", StringComparison.Ordinal)),
                "the exchange is logged once with its outcome");
        }
        finally
        {
            StopPipeHolder(holderMarker, "codex.exe");
        }

        // An unauthorized rate-limits error is AuthenticationExpired end to end; only the class is logged.
        await RunFakeCodexScenarioAsync(null, """{"code":-32001,"message":"401 Unauthorized: token expired"}""", async () =>
        {
            var transport = new CodexAppServerTransport(fixture, null, true, null, null, diagnostics, TimeSpan.FromSeconds(60));
            try
            {
                await transport.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException("Specification failed: an error response must not read as usage.");
            }
            catch (CodexAppServerTransportException exception)
            {
                Equal(ProviderStatus.AuthenticationExpired, exception.Status, "an unauthorized JSON-RPC error asks for sign-in");
                Equal("codex_app_server_auth_expired", exception.SafeErrorCode, "auth-expired code end to end");
            }
        }).ConfigureAwait(false);
        Equal(1, diagnostics.Events.Count(e => e.Code == "codex_app_server_request_error" &&
                                                e.Status == "method=account/rateLimits/read,class=auth,code=-32001"),
            "only the method, classification and numeric code are logged");
    }

    private static async Task RunFakeCodexScenarioAsync(string? holderMarker, string? rateLimitsError, Func<Task> action)
    {
        var names = new[] { "GLIDESLOPE_FAKE_CODEX_CHILD", "GLIDESLOPE_FAKE_CODEX_HOLDER_PID_MARKER", "GLIDESLOPE_FAKE_CODEX_RATE_LIMITS_ERROR" };
        var previous = names.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            Environment.SetEnvironmentVariable(names[0], "1");
            Environment.SetEnvironmentVariable(names[1], holderMarker);
            Environment.SetEnvironmentVariable(names[2], rateLimitsError);
            await action().ConfigureAwait(false);
        }
        finally
        {
            for (var index = 0; index < names.Length; index++)
                Environment.SetEnvironmentVariable(names[index], previous[index]);
        }
    }

    /// <summary>Checks that the owned working directory is emptied without following links, reused between runs,
    /// and replaced by a per-run temp directory when the project is inside a Git work tree.</summary>
    private static void CliWorkingDirectorySpecs(string root)
    {
        var diagnostics = new CapturingDiagnostics();
        var stable = Path.Combine(root, "workdir-specs", "cli-workdir", "claude");
        Directory.CreateDirectory(Path.Combine(stable, ".claude"));
        File.WriteAllText(Path.Combine(stable, ".claude", "settings.json"), "{}");
        File.WriteAllText(Path.Combine(stable, "CLAUDE.md"), "stale fixture");
        File.SetAttributes(Path.Combine(stable, "CLAUDE.md"), FileAttributes.ReadOnly);
        var workingDirectory = new CliWorkingDirectory(stable, "Glideslope-SpecWorkdir-", "claude", "spec", diagnostics);
        var lease = workingDirectory.Prepare() ?? throw new InvalidOperationException("Specification failed: no working directory.");
        Equal(Path.GetFullPath(stable), lease.Path, "the stable directory is used");
        Equal(false, lease.DeleteAfterRun, "the stable directory is kept after the run");
        Equal(0, Directory.EnumerateFileSystemEntries(stable).Count(), "the stable directory is empty before the run");
        Equal(1, diagnostics.Events.Count(e => e.Code == "spec_workdir_emptied" && e.Status == "removed_entries=2"), "emptying is logged");
        workingDirectory.Release(lease);
        Equal(true, Directory.Exists(stable), "release keeps the stable directory");

        var repository = Path.Combine(root, "workdir-specs-repo");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        var inRepository = new CliWorkingDirectory(Path.Combine(repository, "cache", "cli-workdir", "claude"),
            "Glideslope-SpecWorkdir-", "claude", "spec", diagnostics);
        var fallback = inRepository.Prepare() ?? throw new InvalidOperationException("Specification failed: no fallback directory.");
        Equal(true, fallback.DeleteAfterRun, "inside a Git work tree the run falls back to a per-run temp directory");
        Equal(true, Path.GetFileName(fallback.Path).StartsWith("Glideslope-SpecWorkdir-", StringComparison.Ordinal), "fallback naming");
        Equal(1, diagnostics.Events.Count(e => e.Code == "spec_workdir_fallback" && e.Status == "inside_git_tree"), "the fallback is logged with its reason");
        inRepository.Release(fallback);
        Equal(false, Directory.Exists(fallback.Path), "the fallback directory is removed after the run");
        inRepository.Release(inRepository.Prepare()!);
        Equal(1, diagnostics.Events.Count(e => e.Code == "spec_workdir_fallback"), "a lasting fallback is logged once, not on every run");
    }

    private static void CodexParserSpecs()
    {
        const string account = """
            {"account":{"type":"chatgpt","email":"synthetic@example.invalid","planType":"unknown_test_plan"}}
            """;
        const string bothWindowsReversed = """
            {"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":64,"windowDurationMins":300,"resetsAt":1790366400},"secondary":{"usedPercent":31.5,"windowDurationMins":10080,"resetsAt":1791036000}},"other":{"primary":{"usedPercent":99,"windowDurationMins":10080,"resetsAt":1791036000}}}}
            """;
        var parsed = CodexUsageParser.Parse(account, bothWindowsReversed, DateTimeOffset.Parse("2026-09-25T00:00:00Z"));
        Equal("unknown_test_plan", parsed.Plan, "Codex plan is informational rather than eligibility gating");
        Equal(2, parsed.Buckets.Length, "only selected Codex meter is mapped");
        Equal(QuotaBucketRole.Weekly, parsed.Buckets[0].Role, "weekly role selected by duration");
        Equal(0.685d, parsed.Buckets[0].RemainingFraction, "weekly normalization");
        Equal(TimeSpan.FromDays(7), parsed.Buckets[0].Duration, "weekly duration");
        Equal(QuotaBucketRole.Short, parsed.Buckets[1].Role, "short role selected by duration");
        Equal(0.36d, parsed.Buckets[1].RemainingFraction, "short normalization");

        var populatedWithAuthFlag = CodexUsageParser.Parse(
            """{"account":{"type":"chatgpt","email":"synthetic@example.invalid","planType":"team"},"requiresOpenaiAuth":true}""",
            rateLimitsJson: """{"rateLimits":{"primary":{"usedPercent":10,"windowDurationMins":10080,"resetsAt":1791036000}}}""",
            DateTimeOffset.UtcNow);
        Equal("synthetic@example.invalid", populatedWithAuthFlag.Subject,
            "populated ChatGPT account remains usable when account/read also sets requiresOpenaiAuth");

        const string weeklyOnly = """
            {"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":10,"windowDurationMins":10080,"resetsAt":1791036000},"secondary":null}}}
            """;
        var weekly = CodexUsageParser.Parse(account, weeklyOnly, DateTimeOffset.UtcNow);
        Equal(1, weekly.Buckets.Length, "weekly-only account remains usable");
        Equal(QuotaBucketRole.Weekly, weekly.Buckets[0].Role, "weekly-only role");

        const string credits = """
            {"rateLimits":{"primary":{"usedPercent":10,"windowDurationMins":10080,"resetsAt":1791036000},"secondary":null},"rateLimitResetCredits":{"availableCount":3,"credits":[{"id":"synthetic-credit-b","resetType":"codexRateLimits","status":"available","expiresAt":1790726400},{"id":"synthetic-credit-a","resetType":"codexRateLimits","status":"available","expiresAt":null},{"id":"synthetic-credit-c","resetType":"codexRateLimits","status":"redeeming","expiresAt":1790726400},{"id":"synthetic-credit-d","resetType":"codexRateLimits","status":"redeemed","expiresAt":1790726400},{"id":"synthetic-credit-e","resetType":"unknown","status":"unknown","expiresAt":1790726400}]}}
            """;
        var withCredits = CodexUsageParser.Parse(account, credits, DateTimeOffset.Parse("2026-09-25T00:00:00Z"));
        Equal(3, withCredits.ResetCredits?.AvailableCount, "authoritative credit count");
        Equal(2, withCredits.ResetCredits?.Details.Length, "only explicitly available Codex credit details retained");
        Equal(1, withCredits.ResetCredits?.UndisclosedCount, "missing available credit details remain undisclosed");
        Equal(DateTimeOffset.FromUnixTimeSeconds(1790726400), withCredits.ResetCredits?.Details[0].ExpiresAtUtc,
            "available credit Unix-seconds expiry normalized");
        Equal(null, withCredits.ResetCredits?.Details[1].ExpiresAtUtc,
            "available credit with null expiry remains date-unknown");

        Throws<System.Text.Json.JsonException>(() => CodexUsageParser.Parse(account,
            """{"rateLimitsByLimitId":{"other":{"primary":{"usedPercent":1,"windowDurationMins":10080}}},"rateLimits":{"primary":{"usedPercent":1,"windowDurationMins":10080}}}""",
            DateTimeOffset.UtcNow), "present Codex map without Codex entry does not fall back");
        Throws<System.Text.Json.JsonException>(() => CodexUsageParser.Parse(account,
            """{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":1,"windowDurationMins":1440}}}}""",
            DateTimeOffset.UtcNow), "missing weekly window rejected");
        Throws<CodexAccountStateException>(() => CodexUsageParser.Parse(
            """{"requiresOpenaiAuth":true,"account":null}""", weeklyOnly, DateTimeOffset.UtcNow), "signed-out Codex status");

        // a percentage just outside 0..100 is clamped rather than dropping the window.
        var overage = CodexUsageParser.Parse(account,
            """{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":104,"windowDurationMins":10080,"resetsAt":1791036000},"secondary":{"usedPercent":-2,"windowDurationMins":300,"resetsAt":1790366400}}}}""",
            DateTimeOffset.Parse("2026-09-25T00:00:00Z"));
        Equal(0d, overage.Buckets.Single(bucket => bucket.Role == QuotaBucketRole.Weekly).RemainingFraction, "104% used is clamped to nothing left");
        Equal(1d, overage.Buckets.Single(bucket => bucket.Role == QuotaBucketRole.Short).RemainingFraction, "-2% used is clamped to all left");
    }

    private static async Task CodexProviderSpecsAsync()
    {
        const string account = """
            {"account":{"type":"chatgpt","email":"synthetic@example.invalid","planType":"team"}}
            """;
        const string limits = """
            {"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":17,"windowDurationMins":10080,"resetsAt":1791036000},"secondary":null}}}
            """;
        var scopes = new List<(string ProviderId, string Issuer, string Subject)>();
        var provider = new CodexUsageProvider(new FakeCodexTransport(new CodexAppServerResponse(account, limits)),
            (providerId, issuer, subject, _) =>
            {
                scopes.Add((providerId, issuer, subject));
                return ValueTask.FromResult("opaque-codex-test-scope");
            });
        var ready = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Equal(ProviderStatus.Ready, ready.Status, "Codex provider ready status");
        Equal("opaque-codex-test-scope", ready.Snapshot?.AccountScope, "injected Codex scope");
        Equal("codex", scopes.Single().ProviderId, "Codex provider scope namespace");
        Equal("openai.codex", scopes.Single().Issuer, "Codex issuer scope namespace");
        Equal("synthetic@example.invalid", scopes.Single().Subject, "account read subject passed only to resolver");
        Equal("codex.weekly", ready.Snapshot?.Buckets.Single().Id, "opaque stable weekly bucket ID");

        var notSignedIn = await new CodexUsageProvider(new FakeCodexTransport(new CodexAppServerResponse(
                """{"requiresOpenaiAuth":true,"account":null}""", limits)),
            static (_, _, _, _) => ValueTask.FromResult("opaque-codex-test-scope"))
            .ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Equal(ProviderStatus.NeedsSignIn, notSignedIn.Status, "Codex signed-out status");
        Equal(null, notSignedIn.Snapshot, "Codex signed-out has no snapshot");

        var unavailableScope = new CodexUsageProvider(new FakeCodexTransport(new CodexAppServerResponse(account, limits)),
            static (_, _, _, _) => ValueTask.FromException<string>(new IOException("synthetic scope store failure")));
        var firstSession = await unavailableScope.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        var secondSession = await unavailableScope.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Equal(ProviderStatus.Ready, firstSession.Status, "scope-store failure keeps current Codex usage");
        Equal(CodexUsageProvider.HistoryScopeUnavailableCode, firstSession.SafeErrorCode, "safe Codex scope warning");
        Equal(firstSession.Snapshot?.AccountScope, secondSession.Snapshot?.AccountScope, "same synthetic account has stable process-local scope");
        if (firstSession.Snapshot?.AccountScope.Contains("synthetic", StringComparison.Ordinal) == true)
            throw new InvalidOperationException("Codex process-local scope exposes a source subject.");

        var changedAccount = """
            {"account":{"type":"chatgpt","email":"different-synthetic@example.invalid","planType":"team"}}
            """;
        var switching = new MutableCodexTransport(new CodexAppServerResponse(account, limits));
        var sessionProvider = new CodexUsageProvider(switching, static (_, _, _, _) => ValueTask.FromException<string>(new IOException()));
        var accountA = await sessionProvider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        switching.Response = new CodexAppServerResponse(changedAccount, limits);
        var accountB = await sessionProvider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        if (accountA.Snapshot?.AccountScope == accountB.Snapshot?.AccountScope)
            throw new InvalidOperationException("Changed Codex subject shared a process-local history scope.");

        var unavailable = await new CodexUsageProvider(new FailingCodexTransport(ProviderStatus.MissingApplication, "codex_cli_missing"),
            static (_, _, _, _) => ValueTask.FromResult("unused"))
            .ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Equal(ProviderStatus.MissingApplication, unavailable.Status, "missing Codex CLI status");
        Equal("codex_cli_missing", unavailable.SafeErrorCode, "safe missing Codex CLI code");

        using var cancellation = new CancellationTokenSource();
        var waitingProvider = new CodexUsageProvider(new WaitingCodexTransport(), static (_, _, _, _) => ValueTask.FromResult("unused"));
        var pending = waitingProvider.ReadAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => pending, "Codex cancellation propagates").ConfigureAwait(false);
    }

    private static async Task ScopeStorageSpecsAsync(string root)
    {
        var directory = Path.Combine(root, "scope-store");
        var firstStore = new AccountScopeKeyStore(directory);
        var first = await firstStore.ResolveAsync("claude", "issuer", "user-a", CancellationToken.None).ConfigureAwait(false);
        var secondStore = new AccountScopeKeyStore(directory);
        var repeated = await secondStore.ResolveAsync("claude", "issuer", "user-a", CancellationToken.None).ConfigureAwait(false);
        var changed = await secondStore.ResolveAsync("claude", "issuer", "user-b", CancellationToken.None).ConfigureAwait(false);
        var childRepeated = await RunScopeInChildAsync(directory, "claude", "issuer", "user-a").ConfigureAwait(false);
        var childChanged = await RunScopeInChildAsync(directory, "claude", "issuer", "user-b").ConfigureAwait(false);
        Equal(first, repeated, "scope stable across store instances");
        Equal(first, childRepeated, "scope stable across independent process");
        if (first == changed) throw new InvalidOperationException("Different synthetic accounts shared a scope.");
        if (first == childChanged) throw new InvalidOperationException("Different synthetic accounts shared a cross-process scope.");
        if (first.Contains("user-a", StringComparison.Ordinal)) throw new InvalidOperationException("Scope contains its source subject.");

        var keyPath = Path.Combine(directory, "identity", "account-scope.key");
        Equal(32L, new FileInfo(keyPath).Length, "HMAC key length");
        if (!OperatingSystem.IsWindows())
            Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyPath), "user-only HMAC key mode");
    }

    private static void ExecutableDiscoverySpecs(string root)
    {
        var home = Path.Combine(root, "fake-home");
        var install = Path.Combine(home, ".local", "bin");
        Directory.CreateDirectory(install);
        var executable = Path.Combine(install, "claude");
        File.WriteAllText(executable, "metadata-only fixture");
        var found = ClaudeExecutableLocator.Find(Path.Combine(root, "empty-path"), home, isWindows: false);
        Equal(Path.GetFullPath(executable), found, "outside-PATH user install discovered");

        var pathExecutable = Path.Combine(root, "path-claude");
        Directory.CreateDirectory(pathExecutable);
        var pathBinary = Path.Combine(pathExecutable, "claude");
        File.WriteAllText(pathBinary, "metadata-only fixture");
        Equal(Path.GetFullPath(pathBinary), ClaudeExecutableLocator.Find(pathExecutable, home, isWindows: false),
            "PATH executable takes precedence");

        // Windows Codex installs into hash-named directories and switches directory on each update.
        var localAppData = Path.Combine(root, "fake-localappdata");
        var older = Path.Combine(localAppData, "OpenAI", "Codex", "bin", "80f78947ad880e6e");
        var newer = Path.Combine(localAppData, "OpenAI", "Codex", "bin", "d23520d1e41bfb24");
        Directory.CreateDirectory(older);
        Directory.CreateDirectory(newer);
        Directory.CreateDirectory(Path.Combine(localAppData, "OpenAI", "Codex", "bin", "no-exe-here"));
        var olderExe = Path.Combine(older, "codex.exe");
        var newerExe = Path.Combine(newer, "codex.exe");
        File.WriteAllText(olderExe, "metadata-only fixture");
        File.WriteAllText(newerExe, "metadata-only fixture");
        File.SetLastWriteTimeUtc(olderExe, new DateTime(2026, 9, 23, 17, 5, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newerExe, new DateTime(2026, 9, 25, 20, 19, 0, DateTimeKind.Utc));
        Equal(Path.GetFullPath(newerExe),
            CodexExecutableLocator.Find(Path.Combine(root, "empty-path"), home, isWindows: true, localAppData),
            "newest Windows Codex install directory is found without PATH");
        var codexOnPath = Path.Combine(root, "path-codex");
        Directory.CreateDirectory(codexOnPath);
        File.WriteAllText(Path.Combine(codexOnPath, "codex.exe"), "metadata-only fixture");
        Equal(Path.GetFullPath(Path.Combine(codexOnPath, "codex.exe")),
            CodexExecutableLocator.Find(codexOnPath, home, isWindows: true, localAppData),
            "a Codex on PATH still takes precedence");
        Equal<string?>(null, CodexExecutableLocator.Find(Path.Combine(root, "empty-path"), home, isWindows: false, localAppData),
            "the Windows install directory is not searched on Linux");

        // npm global installs. The .cmd shim is found, and resolved to node running the
        // package's own bin entry; cmd.exe is never involved.
        var diagnostics = new CapturingDiagnostics();
        var npm = Path.Combine(root, "npm-prefix");
        var claudePackage = Path.Combine(npm, "node_modules", "@anthropic-ai", "claude-code");
        Directory.CreateDirectory(claudePackage);
        File.WriteAllText(Path.Combine(npm, "claude.cmd"), "metadata-only fixture");
        File.WriteAllText(Path.Combine(npm, "node.exe"), "metadata-only fixture");
        File.WriteAllText(Path.Combine(claudePackage, "package.json"), """{"name":"@anthropic-ai/claude-code","version":"1.0.0","bin":{"claude":"cli.js"}}""");
        File.WriteAllText(Path.Combine(claudePackage, "cli.js"), "metadata-only fixture");
        Equal(Path.GetFullPath(Path.Combine(npm, "claude.cmd")), ClaudeExecutableLocator.Find(npm, home, isWindows: true),
            "an npm shim on PATH is found");
        var viaNode = ClaudeExecutableLocator.Resolve(npm, home, isWindows: true, null, diagnostics);
        Equal(Path.GetFullPath(Path.Combine(npm, "node.exe")), viaNode?.FileName, "the shim runs as node");
        Equal(Path.GetFullPath(Path.Combine(claudePackage, "cli.js")), viaNode?.LeadingArguments.SingleOrDefault(),
            "node runs the package's bin entry");
        Equal("npm", viaNode?.Kind, "npm launch kind");
        if (string.IsNullOrEmpty(viaNode?.VersionKey)) throw new InvalidOperationException("Specification failed: an npm build has a version key.");
        File.WriteAllText(Path.Combine(claudePackage, "package.json"), """{"name":"@anthropic-ai/claude-code","version":"1.0.1","bin":{"claude":"cli.js"}}""");
        File.SetLastWriteTimeUtc(Path.Combine(claudePackage, "package.json"), new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc));
        if (ClaudeExecutableLocator.Resolve(npm, home, isWindows: true, null, diagnostics)?.VersionKey == viaNode!.VersionKey)
            throw new InvalidOperationException("Specification failed: an npm update must change the version key.");

        var both = Path.Combine(root, "npm-and-native");
        Directory.CreateDirectory(both);
        File.WriteAllText(Path.Combine(both, "claude.cmd"), "metadata-only fixture");
        File.WriteAllText(Path.Combine(both, "claude.exe"), "metadata-only fixture");
        Equal(Path.GetFullPath(Path.Combine(both, "claude.exe")), ClaudeExecutableLocator.Find(both, home, isWindows: true),
            "a native exe beats the shim in the same directory");
        Equal("native", ClaudeExecutableLocator.Resolve(both, home, isWindows: true, null, diagnostics)?.Kind, "native launch kind");

        var roaming = Path.Combine(root, "fake-roaming");
        var roamingNpm = Path.Combine(roaming, "npm");
        Directory.CreateDirectory(roamingNpm);
        File.WriteAllText(Path.Combine(roamingNpm, "claude.cmd"), "metadata-only fixture");
        Equal(Path.GetFullPath(Path.Combine(roamingNpm, "claude.cmd")),
            ClaudeExecutableLocator.Find(Path.Combine(root, "empty-path"), Path.Combine(root, "no-home"), isWindows: true, roaming),
            "%APPDATA%\\npm is checked when PATH has no claude");
        Equal<CliLaunchTarget?>(null, ClaudeExecutableLocator.Resolve(Path.Combine(root, "empty-path"), Path.Combine(root, "no-home"), true, roaming, diagnostics),
            "a shim without its package is unresolved");
        Equal(1, diagnostics.Events.Count(e => e.Code == "cli_npm_shim_unresolved" && e.Status == "manifest_missing"), "the unresolved shim is logged");

        var escape = Path.Combine(root, "npm-escape");
        var escapePackage = Path.Combine(escape, "node_modules", "@anthropic-ai", "claude-code");
        Directory.CreateDirectory(escapePackage);
        File.WriteAllText(Path.Combine(escape, "claude.cmd"), "metadata-only fixture");
        File.WriteAllText(Path.Combine(escape, "node.exe"), "metadata-only fixture");
        File.WriteAllText(Path.Combine(escapePackage, "package.json"), """{"bin":"../../../evil.js"}""");
        Equal<CliLaunchTarget?>(null, ClaudeExecutableLocator.Resolve(escape, home, true, null, diagnostics), "a bin outside the package is refused");
        Equal(1, diagnostics.Events.Count(e => e.Status == "bin_outside_package"), "the refusal is logged");

        var noNode = Path.Combine(root, "npm-no-node");
        var noNodePackage = Path.Combine(noNode, "node_modules", "@anthropic-ai", "claude-code");
        Directory.CreateDirectory(noNodePackage);
        File.WriteAllText(Path.Combine(noNode, "claude.cmd"), "metadata-only fixture");
        File.WriteAllText(Path.Combine(noNodePackage, "package.json"), """{"bin":{"claude":"cli.js"}}""");
        File.WriteAllText(Path.Combine(noNodePackage, "cli.js"), "metadata-only fixture");
        Equal<CliLaunchTarget?>(null, ClaudeExecutableLocator.Resolve(noNode, home, true, null, diagnostics), "no node means unresolved");
        Equal(1, diagnostics.Events.Count(e => e.Status == "node_missing"), "missing node is logged");

        var nativeBin = Path.Combine(root, "npm-native-bin");
        var nativePackage = Path.Combine(nativeBin, "node_modules", "@anthropic-ai", "claude-code", "bin");
        Directory.CreateDirectory(nativePackage);
        File.WriteAllText(Path.Combine(nativeBin, "claude.cmd"), "metadata-only fixture");
        File.WriteAllText(Path.Combine(nativeBin, "node_modules", "@anthropic-ai", "claude-code", "package.json"), """{"bin":{"claude":"bin/claude.exe"}}""");
        File.WriteAllText(Path.Combine(nativePackage, "claude.exe"), "metadata-only fixture");
        var native = ClaudeExecutableLocator.Resolve(nativeBin, home, true, null, diagnostics);
        Equal(Path.GetFullPath(Path.Combine(nativePackage, "claude.exe")), native?.FileName, "a native bin entry runs directly");
        Equal(0, native?.LeadingArguments.Length ?? -1, "a native bin entry has no leading arguments");

        var codexNpm = Path.Combine(root, "npm-codex");
        var codexPackage = Path.Combine(codexNpm, "node_modules", "@openai", "codex", "bin");
        Directory.CreateDirectory(codexPackage);
        File.WriteAllText(Path.Combine(codexNpm, "codex.cmd"), "metadata-only fixture");
        File.WriteAllText(Path.Combine(codexNpm, "node.exe"), "metadata-only fixture");
        File.WriteAllText(Path.Combine(codexNpm, "node_modules", "@openai", "codex", "package.json"), """{"bin":{"codex":"bin/codex.js"}}""");
        File.WriteAllText(Path.Combine(codexPackage, "codex.js"), "metadata-only fixture");
        var codexViaNode = CodexExecutableLocator.Resolve(codexNpm, home, true, null, null, diagnostics);
        Equal(Path.GetFullPath(Path.Combine(codexNpm, "node.exe")), codexViaNode?.FileName, "a Codex npm shim runs as node");
        Equal(Path.GetFullPath(Path.Combine(codexPackage, "codex.js")), codexViaNode?.LeadingArguments.SingleOrDefault(), "node runs codex.js");
    }

    internal static void CopyDirectoryRecursive(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, filePath);
            var destinationPath = Path.Combine(destinationDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(filePath, destinationPath, overwrite: true);
        }
    }

    // Read-only live probe: runs the production Antigravity provider once against the real `agy` install and
    // prints only status and bucket availability. Run it twice as separate processes for the restart proof.
    // Scope keys go to a task-owned directory under tempRoot, so no application data is touched.
    private static async Task<int> RunLiveAntigravityAsync(string tempRoot)
    {
        var scopes = new AccountScopeKeyStore(Path.Combine(tempRoot, "live-antigravity-data"));
        // The production transport uses the app's stable agy working directory under
        // %LOCALAPPDATA%\Glideslope; this probe uses an isolated directory so it cannot empty the app's.
        var transport = new AntigravityCliUsageTransport(null, AntigravityCliUsageTransport.DefaultTimeout,
            Path.Combine(tempRoot, "live-cli-workdir", "agy"));
        var provider = new AntigravityUsageProvider(new AntigravityHomeContextReader(), transport, scopes.ResolveAsync);
        var result = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"Antigravity live-read status: {result.Status} ({result.SafeErrorCode ?? "ok"})");
        if (result.Snapshot is null) return 1;
        foreach (var bucket in result.Snapshot.Buckets)
            Console.WriteLine($"Available bucket: {bucket.Role}; fraction in range: {bucket.RemainingFraction is >= 0 and <= 1}; reset present: {bucket.ResetAtUtc is not null}; duration: {bucket.Duration}");
        return 0;
    }

    private static async Task<int> RunLiveCodexAsync(string tempRoot)
    {
        var dataDirectory = Path.Combine(tempRoot, "live-codex-data");
        var first = await RunLiveCodexChildProcessAsync(dataDirectory).ConfigureAwait(false);
        if (first.ExitCode != 0 || first.Result is null)
        {
            Console.WriteLine($"Codex live-read status: {first.SafeStatus}");
            return 1;
        }

        var second = await RunLiveCodexChildProcessAsync(dataDirectory).ConfigureAwait(false);
        if (second.ExitCode != 0 || second.Result is null)
        {
            Console.WriteLine($"Codex live-read status: {second.SafeStatus}");
            return 1;
        }

        var firstParts = first.Result.Split('|', 6);
        var secondParts = second.Result.Split('|', 6);
        var stableScope = firstParts.Length == 6 && secondParts.Length == 6 &&
                          string.Equals(firstParts[1], secondParts[1], StringComparison.Ordinal);
        Console.WriteLine($"Codex live-read status: {firstParts[0]} / {secondParts[0]}; independent scope continuity: {(stableScope ? "pass" : "fail")}");
        if (firstParts.Length == 6)
            Console.WriteLine($"Available bucket roles: {firstParts[2]}");
        if (firstParts.Length == 6)
        {
            Console.WriteLine($"Reset-credit inventory known: {firstParts[3]}");
            Console.WriteLine($"Available reset-credit detail present: {firstParts[4]}");
            Console.WriteLine($"Available reset-credit expiry present: {firstParts[5]}");
        }
        return firstParts[0] == ProviderStatus.Ready.ToString() && secondParts[0] == ProviderStatus.Ready.ToString() && stableScope ? 0 : 1;
    }

    /// <summary>
    /// Read-only live probe: fetches the real
    /// Claude usage response once and prints only its SHAPE - property names, value kinds, array
    /// element shapes and string lengths - never a value, so the output can be pasted into a chat.
    /// The parser reads five_hour, seven_day and limits[] today; anything else here is what it ignores.
    /// </summary>
    private static async Task<int> RunLiveCodexShapeAsync()
    {
        try
        {
            var response = await new CodexAppServerTransport().ReadAsync(CancellationToken.None).ConfigureAwait(false);
            using var document = System.Text.Json.JsonDocument.Parse(response.AccountJson);
            var root = document.RootElement;
            var topLevel = root.ValueKind == System.Text.Json.JsonValueKind.Object
                ? string.Join(',', root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal))
                : "non-object";
            System.Text.Json.JsonElement account = default;
            var accountExists = root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("account", out account);
            var accountNull = !accountExists || account.ValueKind == System.Text.Json.JsonValueKind.Null;
            var accountProperties = accountExists && account.ValueKind == System.Text.Json.JsonValueKind.Object
                ? string.Join(',', account.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal))
                : "unavailable";
            var accountType = "unavailable";
            if (accountExists && account.ValueKind == System.Text.Json.JsonValueKind.Object &&
                account.TryGetProperty("type", out var type) && type.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var value = type.GetString();
                accountType = value is "chatgpt" or "apiKey" ? value : "other";
            }
            var authRequired = root.ValueKind == System.Text.Json.JsonValueKind.Object &&
                               root.TryGetProperty("requiresOpenaiAuth", out var required) &&
                               required.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                ? required.GetBoolean().ToString().ToLowerInvariant()
                : "unavailable";
            Console.WriteLine("Codex account/read: successful JSON-RPC result");
            Console.WriteLine($"Account result properties: {topLevel}");
            Console.WriteLine($"Account object is null or absent: {accountNull.ToString().ToLowerInvariant()}");
            Console.WriteLine($"Account object properties: {accountProperties}");
            Console.WriteLine($"Public account type discriminator: {accountType}");
            Console.WriteLine($"requiresOpenaiAuth: {authRequired}");

            using var limitsDocument = System.Text.Json.JsonDocument.Parse(response.RateLimitsJson);
            var limitsRoot = limitsDocument.RootElement;
            System.Text.Json.JsonElement credits = default;
            var creditsPresent = limitsRoot.ValueKind == System.Text.Json.JsonValueKind.Object &&
                                 limitsRoot.TryGetProperty("rateLimitResetCredits", out credits) &&
                                 credits.ValueKind == System.Text.Json.JsonValueKind.Object;
            Console.WriteLine($"Reset-credit summary present: {creditsPresent.ToString().ToLowerInvariant()}");
            if (creditsPresent)
            {
                Console.WriteLine($"Reset-credit summary properties: {string.Join(',', credits.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal))}");
                if (credits.TryGetProperty("credits", out var details) && details.ValueKind == System.Text.Json.JsonValueKind.Array && details.GetArrayLength() > 0)
                {
                    var detail = details[0];
                    Console.WriteLine($"Reset-credit detail properties: {string.Join(',', detail.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal))}");
                    Console.WriteLine($"resetType JSON kind: {GetJsonKind(detail, "resetType")}");
                    Console.WriteLine($"public resetType: {GetPublicResetType(detail)}");
                    Console.WriteLine($"status JSON kind: {GetJsonKind(detail, "status")}");
                    Console.WriteLine($"public status: {GetPublicCreditStatus(detail)}");
                    Console.WriteLine($"expiresAt JSON kind: {GetJsonKind(detail, "expiresAt")}");
                }
                else
                {
                    Console.WriteLine("Reset-credit detail row present: false");
                }
            }

            var parsed = CodexUsageParser.Parse(response.AccountJson, response.RateLimitsJson, DateTimeOffset.UtcNow);
            Console.WriteLine($"Normalized reset-credit inventory known: {(parsed.ResetCredits is not null).ToString().ToLowerInvariant()}");
            Console.WriteLine($"Normalized reset-credit detail present: {(parsed.ResetCredits?.Details.Length > 0).ToString().ToLowerInvariant()}");
            Console.WriteLine($"Available credit expiry present: {(parsed.ResetCredits?.Details.Any(detail => detail.ExpiresAtUtc is not null) == true).ToString().ToLowerInvariant()}");
            return 0;
        }
        catch (CodexAppServerTransportException exception)
        {
            Console.WriteLine($"Codex account/read safe failure: {exception.SafeErrorCode}");
            return 1;
        }
    }

    private static string GetJsonKind(System.Text.Json.JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) ? value.ValueKind.ToString().ToLowerInvariant() : "absent";

    private static string GetPublicResetType(System.Text.Json.JsonElement detail) =>
        detail.TryGetProperty("resetType", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String &&
        value.GetString() is "codexRateLimits" or "unknown" ? value.GetString()! : "other_or_unavailable";

    private static string GetPublicCreditStatus(System.Text.Json.JsonElement detail) =>
        detail.TryGetProperty("status", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String &&
        value.GetString() is "available" or "redeeming" or "redeemed" or "unknown" ? value.GetString()! : "other_or_unavailable";

    private static async Task<int> RunLiveCodexChildAsync(string dataDirectory)
    {
        var scopes = new AccountScopeKeyStore(dataDirectory);
        var provider = new CodexUsageProvider(new CodexAppServerTransport(), scopes.ResolveAsync);
        var result = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        if (result.Snapshot is null)
        {
            Console.Error.WriteLine(result.SafeErrorCode ?? "codex_read_unavailable");
            return 1;
        }

        // The opaque scope is sent only to the parent specification process for an equality check;
        // the parent emits only the continuity result and never prints this value.
        var roles = string.Join(',', result.Snapshot.Buckets.Select(bucket =>
            $"{bucket.Role}:{(bucket.Duration is null ? "duration-unavailable" : "duration-present")}:{(bucket.ResetAtUtc is null ? "reset-unavailable" : "reset-present")}"));
        var credits = result.Snapshot.ResetCredits;
        var inventoryKnown = (credits is not null).ToString().ToLowerInvariant();
        var detailPresent = (credits?.Details.Length > 0).ToString().ToLowerInvariant();
        var expiryPresent = (credits?.Details.Any(detail => detail.ExpiresAtUtc is not null) == true).ToString().ToLowerInvariant();
        Console.WriteLine($"{result.Status}|{result.Snapshot.AccountScope}|{roles}|{inventoryKnown}|{detailPresent}|{expiryPresent}");
        return 0;
    }

    private static async Task<(int ExitCode, string? Result, string SafeStatus)> RunLiveCodexChildProcessAsync(string dataDirectory)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        ConfigureCurrentProcess(process.StartInfo, "--live-codex-child", dataDirectory);
        if (!process.Start()) throw new InvalidOperationException("Could not start the owned Codex live-read specification child.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
            var output = (await stdoutTask.ConfigureAwait(false)).Trim();
            var error = (await stderrTask.ConfigureAwait(false)).Trim();
            return (process.ExitCode, process.ExitCode == 0 ? output : null,
                process.ExitCode == 0 ? "ready" : SafeCode(error));
        }
        catch (TimeoutException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync().ConfigureAwait(false);
            _ = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);
            return (1, null, "timeout");
        }
    }

    private static string SafeCode(string candidate) =>
        candidate.Length is > 0 and <= 80 && candidate.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            ? candidate
            : "codex_read_unavailable";

    private static async Task<int> RunScopeChildAsync(string directory, string providerId, string issuer, string subject)
    {
        var store = new AccountScopeKeyStore(directory);
        var scope = await store.ResolveAsync(providerId, issuer, subject, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine(scope);
        return 0;
    }

    private static async Task<string> RunScopeInChildAsync(
        string directory, string providerId, string issuer, string subject)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        ConfigureCurrentProcess(process.StartInfo, "--scope-child", directory, providerId, issuer, subject);
        if (!process.Start()) throw new InvalidOperationException("Could not start the owned scope specification child.");

        var stderrTask = process.StandardError.ReadToEndAsync();
        // Wait for the child process to exit; its exit is the signal this check needs.
        await process.WaitForExitAsync().ConfigureAwait(false);
        var scope = (await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false)).Trim();
        var stderrText = await stderrTask.ConfigureAwait(false);
        Console.Error.WriteLine(
            $"scope specification child exited with code {process.ExitCode}, scope length {scope.Length}, stderr length {stderrText.Length}");
        if (process.ExitCode != 0 || scope.Length != 64 || scope.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("The owned scope specification child did not return an opaque scope.");
        return scope;
    }

    private static void ConfigureCurrentProcess(System.Diagnostics.ProcessStartInfo startInfo, params string[] arguments)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The provider-spec executable path is unavailable.");
        startInfo.FileName = executable;
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,
                $"{typeof(Program).Assembly.GetName().Name}.dll"));
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
    }

    internal static void Equal<T>(T expected, T? actual, string label)
    {
        if (!EqualityComparer<T?>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Specification failed: {label}.");
    }

    internal static void Throws<TException>(Action action, string label) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Specification failed: {label}.");
    }

    internal static async Task ThrowsAsync<TException>(Func<Task> action, string label) where TException : Exception
    {
        try { await action().ConfigureAwait(false); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Specification failed: {label}.");
    }

    /// <summary>A manually advanced clock that runs one-shot timers deterministically, including the
    /// cancellation timeout used by the Antigravity transport.</summary>
    internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = start;

        public override DateTimeOffset GetUtcNow() { lock (_gate) return _utcNow; }

        /// <summary>Moves the clock forward by exactly <paramref name="delta"/> and fires every timer (a
        /// CancellationTokenSource's own internal timer, in particular) whose due time has now been reached.
        /// No spec waits on a real timeout: it waits for a diagnostic signal instead (see
        /// CapturingDiagnostics.WaitForCountAsync) and only then advances by the exact amount it is
        /// proving.</summary>
        public void Advance(TimeSpan delta)
        {
            if (delta < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delta));
            List<(TimerCallback Callback, object? State)> due = [];
            lock (_gate)
            {
                _utcNow += delta;
                foreach (var timer in _timers.ToArray())
                {
                    if (timer.TryFire(_utcNow, out var callback, out var state))
                        due.Add((callback, state));
                }
            }
            foreach (var (callback, state) in due) callback(state);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            lock (_gate)
            {
                var timer = new ManualTimer(this, callback, state);
                timer.Reschedule(_utcNow, dueTime, period);
                _timers.Add(timer);
                return timer;
            }
        }

        private void Reschedule(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate) timer.Reschedule(_utcNow, dueTime, period);
        }

        private void Remove(ManualTimer timer)
        {
            lock (_gate) _timers.Remove(timer);
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? _dueAt;
            private TimeSpan _period;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                owner.Reschedule(this, dueTime, period);
                return true;
            }

            public void Reschedule(DateTimeOffset now, TimeSpan dueTime, TimeSpan period)
            {
                _period = period;
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime;
            }

            public bool TryFire(DateTimeOffset now, out TimerCallback fireCallback, out object? fireState)
            {
                fireCallback = callback;
                fireState = state;
                if (_disposed || _dueAt is not { } due || due > now) return false;
                _dueAt = _period > TimeSpan.Zero ? due + _period : null;
                return true;
            }

            public void Dispose()
            {
                _disposed = true;
                owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>Signals waiting specs when CapturingDiagnostics records a new event, without polling.</summary>
    private sealed class Pulse
    {
        private readonly object _gate = new();
        private TaskCompletionSource _current = New();
        public Task Task { get { lock (_gate) return _current.Task; } }
        public void Signal()
        {
            TaskCompletionSource prior;
            lock (_gate) { prior = _current; _current = New(); }
            prior.TrySetResult();
        }
        private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A named event the fake agy sets once it has written its markers and
    /// printed its output (GLIDESLOPE_FAKE_ANTIGRAVITY_READY_EVENT). antigravity_usage_process_started alone only
    /// says the parent started the child; a spec that kills the child by advancing its clock straight after it
    /// raced the child's own startup and killed it before it printed. Waiting on this instead is a signal, not a
    /// real-time wait.</summary>
    internal sealed class FakeReadySignal : IDisposable
    {
        private readonly EventWaitHandle _event;
        private readonly TaskCompletionSource _set = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly RegisteredWaitHandle _registration;

        public FakeReadySignal()
        {
            Name = "Glideslope-FakeAgyReady-" + Guid.NewGuid().ToString("N");
            _event = new EventWaitHandle(false, EventResetMode.ManualReset, Name);
            _registration = ThreadPool.RegisterWaitForSingleObject(_event, static (state, _) =>
                ((TaskCompletionSource)state!).TrySetResult(), _set, Timeout.Infinite, executeOnlyOnce: true);
        }

        public string Name { get; }
        public Task Task => _set.Task;

        public void Dispose()
        {
            _registration.Unregister(null);
            _event.Dispose();
        }
    }

    internal sealed class CapturingDiagnostics : IProviderDiagnosticSink
    {
        private readonly object _gate = new();
        private readonly Pulse _recorded = new();
        public List<ProviderDiagnostic> Events { get; } = [];

        public void Record(ProviderDiagnostic diagnostic)
        {
            lock (_gate) Events.Add(diagnostic);
            _recorded.Signal();
        }

        private int CountOf(string code) { lock (_gate) return Events.Count(item => item.Code == code); }

        /// <summary>Waits until <paramref name="code"/> has been recorded at least
        /// <paramref name="atLeast"/> times, capturing the current pulse before checking the count so a
        /// concurrent record cannot be missed.</summary>
        public async Task WaitForCountAsync(string code, int atLeast)
        {
            while (true)
            {
                var pulseTask = _recorded.Task;
                if (CountOf(code) >= atLeast) return;
                await pulseTask.ConfigureAwait(false);
            }
        }
    }

    private sealed class FakeCodexTransport(CodexAppServerResponse response) : ICodexAppServerTransport
    {
        public ValueTask<CodexAppServerResponse> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(response);
        }
    }

    private sealed class MutableCodexTransport(CodexAppServerResponse response) : ICodexAppServerTransport
    {
        public CodexAppServerResponse Response { get; set; } = response;
        public ValueTask<CodexAppServerResponse> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Response);
        }
    }

    private sealed class FailingCodexTransport(ProviderStatus status, string safeCode) : ICodexAppServerTransport
    {
        public ValueTask<CodexAppServerResponse> ReadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<CodexAppServerResponse>(new CodexAppServerTransportException(status, safeCode));
    }

    private sealed class WaitingCodexTransport : ICodexAppServerTransport
    {
        public async ValueTask<CodexAppServerResponse> ReadAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Unreachable after cancellation.");
        }
    }

    private sealed class OwnedTempRoot : IDisposable
    {
        private readonly string _lockPath;
        private FileStream? _activeLock;
        public string Path { get; }

        private OwnedTempRoot(string path, string lockPath, FileStream activeLock)
        {
            Path = path;
            _lockPath = lockPath;
            _activeLock = activeLock;
        }

        public static OwnedTempRoot Create()
        {
            CleanupStaleRoots();
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            FileStream? activeLock = null;
            try
            {
                var lockPath = System.IO.Path.Combine(root, ".active-lock");
                activeLock = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                var marker = System.IO.Path.Combine(root, ".owner");
                File.WriteAllText(marker, OwnerMarker, Encoding.UTF8);
                return new OwnedTempRoot(root, lockPath, activeLock);
            }
            catch
            {
                activeLock?.Dispose();
                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public void Dispose()
        {
            _activeLock?.Dispose();
            _activeLock = null;
            VerifyOwnedPath(Path);
            if (File.Exists(System.IO.Path.Combine(Path, ".owner")) &&
                File.ReadAllText(System.IO.Path.Combine(Path, ".owner")) != OwnerMarker)
                throw new IOException("Temporary root ownership marker changed.");
            Directory.Delete(Path, recursive: true);
            if (Directory.Exists(Path) || File.Exists(_lockPath))
                throw new IOException("Owned provider specification temporary root remains after cleanup.");
        }

        private static void CleanupStaleRoots()
        {
            var temp = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
            foreach (var candidate in Directory.EnumerateDirectories(temp, TempPrefix + "*", SearchOption.TopDirectoryOnly))
            {
                var canonical = System.IO.Path.GetFullPath(candidate);
                if (!string.Equals(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetDirectoryName(canonical) ?? string.Empty), temp, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(System.IO.Path.Combine(canonical, ".owner"))) continue;
                if (!string.Equals(File.ReadAllText(System.IO.Path.Combine(canonical, ".owner")), OwnerMarker, StringComparison.Ordinal)) continue;

                var lockPath = System.IO.Path.Combine(canonical, ".active-lock");
                try
                {
                    using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                }
                catch (IOException)
                {
                    continue;
                }
                VerifyOwnedPath(canonical);
                Directory.Delete(canonical, recursive: true);
            }
        }

        private static void VerifyOwnedPath(string candidate)
        {
            var canonical = System.IO.Path.GetFullPath(candidate);
            var temp = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
            if (!string.Equals(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetDirectoryName(canonical) ?? string.Empty), temp, StringComparison.OrdinalIgnoreCase) ||
                !System.IO.Path.GetFileName(canonical).StartsWith(TempPrefix, StringComparison.Ordinal))
                throw new IOException("Refusing cleanup outside the exact provider specification temporary-root pattern.");
        }
    }
}
