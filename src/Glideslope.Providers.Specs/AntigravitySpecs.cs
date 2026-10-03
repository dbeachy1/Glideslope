using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Glideslope.Domain;
using Glideslope.Providers;

/// <summary>
/// Specifications for the Gemini provider's Google Antigravity CLI integration. Uses Program's assertion,
/// diagnostic, and file-copy helpers, plus a separate fake-executable environment namespace.
/// </summary>
internal static class AntigravitySpecs
{
    // Synthetic zero-spend response for `agy -p /usage --output-format json`. Quota values, activity
    // counts, and reset times are invented; the CLI schema and zero-turn/token safety fields are retained.
    private const string SyntheticUsageFixture = """
        {"conversation_id":"","status":"SUCCESS","response":"Gemini Models\tWeekly Limit Remaining\t62.5%\t2027-02-16T16:00:00Z\nGemini Models\tFive Hour Limit Remaining\t37.5%\t2027-02-16T14:30:00Z\nClaude and GPT models\tWeekly Limit Remaining\t81.25%\t2027-02-16T16:15:00Z\nClaude and GPT models\tFive Hour Limit Remaining\t56.25%\t2027-02-16T14:45:00Z\n","duration_seconds":3.25,"num_turns":0,"usage":{"input_tokens":0,"output_tokens":0,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":0},"command":{"name":"usage","data":{"description":"Sample quota usage response for separate model groups and reset windows.","groups":[{"name":"Gemini Models","description":"Sample Gemini model quota group; models include Gemini Flash, Gemini Pro.","buckets":[{"id":"gemini-weekly","name":"Weekly Limit Remaining","description":"Sample weekly quota bucket.","window":"weekly","remaining_fraction":0.625,"reset_time":"2027-02-16T16:00:00Z"},{"id":"gemini-5h","name":"Five Hour Limit Remaining","description":"Sample five-hour quota bucket.","window":"5h","remaining_fraction":0.375,"reset_time":"2027-02-16T14:30:00Z"}]},{"name":"Claude and GPT models","description":"Sample shared model quota group.","buckets":[{"id":"3p-weekly","name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":0.8125,"reset_time":"2027-02-16T16:15:00Z"},{"id":"3p-5h","name":"Five Hour Limit Remaining","window":"5h","remaining_fraction":0.5625,"reset_time":"2027-02-16T14:45:00Z"}]}]}}}
        """;

    // A model turn wearing the shape of a failed /usage call: no "command" object, nonzero turns and tokens.
    private const string GuardNoCommandFixture = """
        {"status":"SUCCESS","num_turns":1,"usage":{"input_tokens":2,"output_tokens":3,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":5}}
        """;

    // A well-formed response, but for the wrong command entirely.
    private const string GuardHelpCommandFixture = """
        {"status":"SUCCESS","num_turns":0,"usage":{"input_tokens":0,"output_tokens":0,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":0},"command":{"name":"help","data":{}}}
        """;

    // A safe, zero-token usage response, but the Gemini group itself is absent (only the 3p group is
    // present) -- a non-latching schema change, never the command guard.
    private const string MissingGeminiGroupFixture = """
        {"status":"SUCCESS","num_turns":0,"usage":{"input_tokens":0,"output_tokens":0,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":0},"command":{"name":"usage","data":{"groups":[{"name":"Claude and GPT models","buckets":[{"id":"3p-weekly","window":"weekly","remaining_fraction":0.8125,"reset_time":"2027-02-16T16:15:00Z"},{"id":"3p-5h","window":"5h","remaining_fraction":0.5625,"reset_time":"2027-02-16T14:45:00Z"}]}]}}}
        """;

    private const string NonSuccessStatusFixture = """
        {"status":"ERROR","num_turns":0,"usage":{"input_tokens":0,"output_tokens":0,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":0},"command":{"name":"usage","data":{"groups":[]}}}
        """;

    // Sign-in failure shape: string-valued counters are accepted when the command cannot read usage.
    private const string SignInRequiredFixture = """
        {"conversation_id":"","status":"ERROR","response":"","error":"authentication failed or timed out","duration_seconds":"2.25","num_turns":"0","usage":{"total_tokens":"0"}}
        """;

    // A failed CLI exit must be rejected even when the response contains a model turn and token usage.
    private const string NonzeroExitWithTokensFixture = """
        {"status":"ERROR","error":"tool call denied","num_turns":1,"usage":{"input_tokens":8,"output_tokens":5,"thinking_tokens":1,"cache_read_tokens":2,"total_tokens":16}}
        """;

    // /usage answered as a chat prompt in plain text, with no JSON result line.
    private const string PlainTextReplyFixture = """
        Sure! Here is what I can tell you about your usage.
        You have plenty of quota left this week.
        """;

    public static async Task RunAllAsync(string tempRoot)
    {
        ParserSpecs();
        LocatorSpecs(tempRoot);
        await PrecheckSpecsAsync(tempRoot).ConfigureAwait(false);
        await ScopeSpecsAsync(tempRoot).ConfigureAwait(false);
        await LatchSpecsAsync(tempRoot).ConfigureAwait(false);
        await SignInHoldSpecsAsync(tempRoot, exitCode: 1).ConfigureAwait(false);
        await SignInHoldSpecsAsync(tempRoot, exitCode: 0).ConfigureAwait(false);
        await ProcessSpecsAsync(tempRoot).ConfigureAwait(false);
        await QuotaLatchShapeSpecsAsync(tempRoot).ConfigureAwait(false);
        await TimeoutHoldSpecsAsync(tempRoot).ConfigureAwait(false);
        await TimeoutWithOutputSpecsAsync(tempRoot).ConfigureAwait(false);
        await UserInitiatedReadSpecsAsync(tempRoot).ConfigureAwait(false);
        await TimeoutClassificationSpecsAsync(tempRoot).ConfigureAwait(false);
        await TimeoutWithoutSignInSpecsAsync(tempRoot).ConfigureAwait(false);
        await WorkingDirectoryAndEncodingSpecsAsync(tempRoot).ConfigureAwait(false);
    }

    // Synthetic sign-in stderr with a recognizable identity-provider URL marker.
    private const string SignInUrlStderr = "Sign in to continue: https://accounts.google.com/o/oauth2/auth?client_id=synthetic";

    private static void ParserSpecs()
    {
        var parsed = AntigravityUsageParser.Parse(SyntheticUsageFixture);
        Program.Equal(QuotaBucketRole.Weekly, parsed.Weekly.Role, "Gemini weekly role");
        Program.Equal(0.625d, parsed.Weekly.RemainingFraction, "Gemini weekly remaining fraction");
        Program.Equal(DateTimeOffset.Parse("2027-02-16T16:00:00Z"), parsed.Weekly.ResetAtUtc, "Gemini weekly reset");
        Program.Equal(TimeSpan.FromDays(7), parsed.Weekly.Duration, "Gemini weekly duration");
        Program.Equal(QuotaBucketRole.Short, parsed.Short.Role, "Gemini five-hour role");
        Program.Equal(0.375d, parsed.Short.RemainingFraction, "Gemini five-hour remaining fraction");
        Program.Equal(DateTimeOffset.Parse("2027-02-16T14:30:00Z"), parsed.Short.ResetAtUtc, "Gemini five-hour reset");
        Program.Equal(TimeSpan.FromHours(5), parsed.Short.Duration, "Gemini five-hour duration");
        if (parsed.Weekly.SourceSemantics.Contains("3p", StringComparison.Ordinal) ||
            parsed.Short.SourceSemantics.Contains("3p", StringComparison.Ordinal))
            throw new InvalidOperationException("Gemini buckets must never carry 3p-group source semantics.");

        Program.Throws<AntigravityUsageCommandUnsupportedException>(
            () => AntigravityUsageParser.Parse(GuardNoCommandFixture),
            "the guard trips on a nonzero-turn response with no command field");
        Program.Throws<AntigravityUsageCommandUnsupportedException>(
            () => AntigravityUsageParser.Parse(GuardHelpCommandFixture),
            "the guard trips on an unexpected command name");
        Program.Throws<JsonException>(
            () => AntigravityUsageParser.Parse(MissingGeminiGroupFixture),
            "a missing Gemini group is a non-latching schema change, not the command guard");
        Program.Throws<AntigravityUsageFailedException>(
            () => AntigravityUsageParser.Parse(NonSuccessStatusFixture),
            "a non-SUCCESS status is reported as a usage failure");
    }

    private static void LocatorSpecs(string tempRoot)
    {
        var localAppData = Path.Combine(tempRoot, "antigravity-locator-appdata");
        CreateFakeAgyExecutable(localAppData);
        var pathDirectory = Path.Combine(tempRoot, "antigravity-locator-path");
        Directory.CreateDirectory(pathDirectory);
        File.WriteAllText(Path.Combine(pathDirectory, "agy.exe"), "metadata-only fixture", Encoding.UTF8);

        var foundKnownLocation = AntigravityExecutableLocator.Find(localAppData, null, pathDirectory, isWindows: true);
        Program.Equal(Path.GetFullPath(Path.Combine(localAppData, "agy", "bin", "agy.exe")), foundKnownLocation,
            "the known Windows install location takes precedence over PATH");

        var emptyAppData = Path.Combine(tempRoot, "antigravity-locator-appdata-empty");
        var foundOnPath = AntigravityExecutableLocator.Find(emptyAppData, null, pathDirectory, isWindows: true);
        Program.Equal(Path.GetFullPath(Path.Combine(pathDirectory, "agy.exe")), foundOnPath,
            "PATH is used when the known install location is absent");

        var missing = AntigravityExecutableLocator.Find(emptyAppData, null,
            Path.Combine(tempRoot, "antigravity-locator-empty-path"), isWindows: true);
        Program.Equal<string?>(null, missing, "a completely absent agy reports null");
    }

    private static async Task PrecheckSpecsAsync(string tempRoot)
    {
        var missingHome = Path.Combine(tempRoot, "antigravity-home-missing");
        Directory.CreateDirectory(missingHome);
        var missingReader = new AntigravityHomeContextReader(missingHome);
        Program.Equal(false, await missingReader.IsSignedInAsync(CancellationToken.None).ConfigureAwait(false),
            "a missing onboarding.json is not signed in");

        var incompleteHome = Path.Combine(tempRoot, "antigravity-home-incomplete");
        WriteOnboarding(incompleteHome, onboardingComplete: false);
        var incompleteReader = new AntigravityHomeContextReader(incompleteHome);
        Program.Equal(false, await incompleteReader.IsSignedInAsync(CancellationToken.None).ConfigureAwait(false),
            "onboardingComplete false is not signed in");

        var completeHome = Path.Combine(tempRoot, "antigravity-home-complete");
        WriteOnboarding(completeHome, onboardingComplete: true);
        var completeReader = new AntigravityHomeContextReader(completeHome);
        Program.Equal(true, await completeReader.IsSignedInAsync(CancellationToken.None).ConfigureAwait(false),
            "onboardingComplete true is signed in");

        // Provider level: a genuinely present fake executable must still never be launched when the real
        // onboarding-cache reader (not a scripted fake) reports incomplete sign-in.
        var localAppData = Path.Combine(tempRoot, "antigravity-precheck-appdata");
        CreateFakeAgyExecutable(localAppData);
        var transport = new ScriptedAntigravityTransport(
            new AntigravityProcessResult(AntigravityProcessOutcome.Completed, 0, SyntheticUsageFixture, false));
        var provider = new AntigravityUsageProvider(missingReader, transport,
            static (_, _, _, _) => ValueTask.FromResult("unused"),
            path: null, localApplicationData: localAppData, homeDirectory: missingHome, isWindows: true,
            diagnostics: null, timeProvider: null);
        var result = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Program.Equal(ProviderStatus.NeedsSignIn, result.Status, "a missing onboarding file reports NeedsSignIn");
        Program.Equal("antigravity_not_signed_in", result.SafeErrorCode, "safe not-signed-in diagnostic code");
        Program.Equal(0, transport.CallCount, "agy is never launched when onboarding is incomplete");

        var incompleteProvider = new AntigravityUsageProvider(new AntigravityHomeContextReader(incompleteHome), transport,
            static (_, _, _, _) => ValueTask.FromResult("unused"),
            path: null, localApplicationData: localAppData, homeDirectory: incompleteHome, isWindows: true,
            diagnostics: null, timeProvider: null);
        var incompleteResult = await incompleteProvider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Program.Equal(ProviderStatus.NeedsSignIn, incompleteResult.Status, "onboardingComplete false reports NeedsSignIn");
        Program.Equal(0, transport.CallCount, "agy is still never launched for an explicitly incomplete onboarding file");

        // A completely absent CLI is reported distinctly from an incomplete sign-in, and also never launches.
        var noCliProvider = new AntigravityUsageProvider(completeReader, transport,
            static (_, _, _, _) => ValueTask.FromResult("unused"),
            path: null, localApplicationData: Path.Combine(tempRoot, "antigravity-no-cli-appdata"),
            homeDirectory: Path.Combine(tempRoot, "antigravity-no-cli-home"), isWindows: true,
            diagnostics: null, timeProvider: null);
        var noCliResult = await noCliProvider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Program.Equal(ProviderStatus.MissingApplication, noCliResult.Status, "a missing agy executable reports MissingApplication");
        Program.Equal("antigravity_cli_not_found", noCliResult.SafeErrorCode, "safe missing-CLI diagnostic code");
        Program.Equal(0, transport.CallCount, "agy is never launched when it cannot be found at all");
    }

    private static async Task ScopeSpecsAsync(string tempRoot)
    {
        var localAppData = Path.Combine(tempRoot, "antigravity-scope-appdata");
        CreateFakeAgyExecutable(localAppData);

        var validHome = Path.Combine(tempRoot, "antigravity-scope-valid-home");
        WriteOnboarding(validHome, onboardingComplete: true);
        WriteProjectId(validHome, "synthetic-project-alpha");
        var calls = new List<(string ProviderId, string Issuer, string Subject)>();
        var validProvider = new AntigravityUsageProvider(new AntigravityHomeContextReader(validHome),
            new ScriptedAntigravityTransport(new AntigravityProcessResult(AntigravityProcessOutcome.Completed, 0, SyntheticUsageFixture, false)),
            (providerId, issuer, subject, _) => { calls.Add((providerId, issuer, subject)); return ValueTask.FromResult("opaque-antigravity-scope"); },
            path: null, localApplicationData: localAppData, homeDirectory: validHome, isWindows: true,
            diagnostics: null, timeProvider: null);
        var validResult = await validProvider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Program.Equal(ProviderStatus.Ready, validResult.Status, "a valid project id reaches Ready");
        Program.Equal(1, calls.Count, "the scope resolver is called exactly once");
        Program.Equal(ProviderIds.Gemini, calls[0].ProviderId, "Gemini scope provider id");
        Program.Equal("google.antigravity.cli", calls[0].Issuer, "Gemini scope issuer");
        Program.Equal("synthetic-project-alpha", calls[0].Subject, "the resolver receives the real project id");
        Program.Equal<string?>(null, validResult.SafeErrorCode, "a resolved scope carries no history-unavailable warning");

        var missingProjectHome = Path.Combine(tempRoot, "antigravity-scope-missing-home");
        WriteOnboarding(missingProjectHome, onboardingComplete: true);
        var missingCalls = new List<string>();
        var missingProvider = new AntigravityUsageProvider(new AntigravityHomeContextReader(missingProjectHome),
            new ScriptedAntigravityTransport(new AntigravityProcessResult(AntigravityProcessOutcome.Completed, 0, SyntheticUsageFixture, false)),
            (_, _, subject, _) => { missingCalls.Add(subject); return ValueTask.FromResult("unused"); },
            path: null, localApplicationData: localAppData, homeDirectory: missingProjectHome, isWindows: true,
            diagnostics: null, timeProvider: null);
        var missingResult = await missingProvider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Program.Equal(ProviderStatus.Ready, missingResult.Status, "a missing project id still reaches Ready via the session-scope fallback");
        Program.Equal(AntigravityUsageProvider.HistoryScopeUnavailableCode, missingResult.SafeErrorCode,
            "a missing project id reports the history-scope-unavailable code");
        Program.Equal(0, missingCalls.Count, "the resolver is never called without a valid project id");
        if (string.IsNullOrWhiteSpace(missingResult.Snapshot?.AccountScope))
            throw new InvalidOperationException("The session-scope fallback must still produce a usable opaque scope.");

        var invalidProjectHome = Path.Combine(tempRoot, "antigravity-scope-invalid-home");
        WriteOnboarding(invalidProjectHome, onboardingComplete: true);
        WriteProjectId(invalidProjectHome, "AB"); // Too short and uppercase: fails the CLI's own project-name shape.
        var invalidProvider = new AntigravityUsageProvider(new AntigravityHomeContextReader(invalidProjectHome),
            new ScriptedAntigravityTransport(new AntigravityProcessResult(AntigravityProcessOutcome.Completed, 0, SyntheticUsageFixture, false)),
            static (_, _, _, _) => ValueTask.FromResult("unused"),
            path: null, localApplicationData: localAppData, homeDirectory: invalidProjectHome, isWindows: true,
            diagnostics: null, timeProvider: null);
        var invalidResult = await invalidProvider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Program.Equal(AntigravityUsageProvider.HistoryScopeUnavailableCode, invalidResult.SafeErrorCode,
            "an invalid project id also falls back to the session scope");
        if (invalidResult.Snapshot?.AccountScope.Contains("AB", StringComparison.Ordinal) == true)
            throw new InvalidOperationException("The session-scope fallback must never expose the raw project id.");

        // A resolver that throws (a key-store failure) falls back the same way Claude and Codex do.
        var throwingHome = Path.Combine(tempRoot, "antigravity-scope-throwing-home");
        WriteOnboarding(throwingHome, onboardingComplete: true);
        WriteProjectId(throwingHome, "synthetic-project-beta");
        var throwingProvider = new AntigravityUsageProvider(new AntigravityHomeContextReader(throwingHome),
            new ScriptedAntigravityTransport(new AntigravityProcessResult(AntigravityProcessOutcome.Completed, 0, SyntheticUsageFixture, false)),
            static (_, _, _, _) => ValueTask.FromException<string>(new IOException("synthetic scope store failure")),
            path: null, localApplicationData: localAppData, homeDirectory: throwingHome, isWindows: true,
            diagnostics: null, timeProvider: null);
        var throwingResult = await throwingProvider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Program.Equal(ProviderStatus.Ready, throwingResult.Status, "a resolver failure preserves the current usage reading");
        Program.Equal(AntigravityUsageProvider.HistoryScopeUnavailableCode, throwingResult.SafeErrorCode,
            "a resolver failure reports the same history-scope-unavailable code");
    }

    private static async Task LatchSpecsAsync(string tempRoot)
    {
        var localAppData = Path.Combine(tempRoot, "antigravity-latch-appdata");
        CreateFakeAgyExecutable(localAppData);
        var home = Path.Combine(tempRoot, "antigravity-latch-home");
        WriteOnboarding(home, onboardingComplete: true);
        WriteProjectId(home, "synthetic-project-latch");

        var transport = new ScriptedAntigravityTransport(
            new AntigravityProcessResult(AntigravityProcessOutcome.Completed, 0, GuardHelpCommandFixture, false),
            new AntigravityProcessResult(AntigravityProcessOutcome.Completed, 0, SyntheticUsageFixture, false));
        var diagnostics = new Program.CapturingDiagnostics();
        var provider = new AntigravityUsageProvider(new AntigravityHomeContextReader(home), transport,
            static (_, _, _, _) => ValueTask.FromResult("opaque-antigravity-scope"),
            path: null, localApplicationData: localAppData, homeDirectory: home, isWindows: true,
            diagnostics: diagnostics, timeProvider: null);

        var first = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Program.Equal(ProviderStatus.SchemaChanged, first.Status, "an unsupported command trips the guard");
        Program.Equal(AntigravityUsageProvider.CommandUnsupportedCode, first.SafeErrorCode, "safe command-unsupported code");
        Program.Equal(1, transport.CallCount, "the first read launches agy exactly once");
        // The latch event carries the reason code in its status.
        Program.Equal(1, diagnostics.Events.Count(item => item.Code == "antigravity_latched"),
            "the latch trip is logged once");
        Program.Equal(1, diagnostics.Events.Count(item => item.Code == "antigravity_latched" &&
            item.Status == "not_zero_spend;exit_code=0"), "the latch reason code names the unproven spend");

        var second = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Program.Equal(ProviderStatus.SchemaChanged, second.Status, "the latched provider keeps reporting the guard failure");
        Program.Equal(AntigravityUsageProvider.CommandUnsupportedCode, second.SafeErrorCode, "the latched safe code is stable");
        Program.Equal(1, transport.CallCount, "a latched provider never launches agy again, even though the scripted transport now has a good response queued");
        Program.Equal(1, diagnostics.Events.Count(item => item.Code == "antigravity_latched"),
            "the latch trip is logged only once, not on every subsequent read");
    }

    private static async Task SignInHoldSpecsAsync(string tempRoot, int exitCode)
    {
        var localAppData = Path.Combine(tempRoot, $"antigravity-signin-appdata-{exitCode}");
        CreateFakeAgyExecutable(localAppData);
        var home = Path.Combine(tempRoot, $"antigravity-signin-home-{exitCode}");
        WriteOnboarding(home, onboardingComplete: true);
        WriteProjectId(home, "synthetic-project-signin");

        var transport = new ScriptedAntigravityTransport(
            new AntigravityProcessResult(AntigravityProcessOutcome.Completed, exitCode, SignInRequiredFixture, false),
            new AntigravityProcessResult(AntigravityProcessOutcome.Completed, 0, SyntheticUsageFixture, false));
        var clock = new Program.ManualTimeProvider(DateTimeOffset.Parse("2026-09-25T23:00:00Z"));
        var diagnostics = new Program.CapturingDiagnostics();
        var provider = new AntigravityUsageProvider(new AntigravityHomeContextReader(home), transport,
            static (_, _, _, _) => ValueTask.FromResult("opaque-antigravity-scope"),
            path: null, localApplicationData: localAppData, homeDirectory: home, isWindows: true,
            diagnostics: diagnostics, timeProvider: clock);

        var first = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Program.Equal(ProviderStatus.NeedsSignIn, first.Status, $"agy's sign-in failure (exit {exitCode}) asks the user to sign in");
        Program.Equal("antigravity_sign_in_required", first.SafeErrorCode, "safe sign-in-required code");
        Program.Equal(1, transport.CallCount, "the failing read launched agy once");
        Program.Equal(0, diagnostics.Events.Count(item => item.Code == "antigravity_latched"),
            "a zero-cost sign-in failure never trips the quota-spend latch");

        clock.Advance(TimeSpan.FromMinutes(29));
        var held = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Program.Equal(ProviderStatus.NeedsSignIn, held.Status, "within the hold the card still asks for sign-in");
        Program.Equal(1, transport.CallCount, "agy is not launched again during the 30-minute hold, so no repeated browser logins");

        clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Program.Equal(ProviderStatus.Ready, recovered.Status, "after the hold agy is tried again and a good read recovers");
        Program.Equal(2, transport.CallCount, "exactly one relaunch after the hold");
    }

    private static async Task ProcessSpecsAsync(string tempRoot)
    {
        var fixtureDirectory = Path.Combine(tempRoot, "fake-agy-fixture");
        Program.CopyDirectoryRecursive(AppContext.BaseDirectory, fixtureDirectory);
        File.Copy(Path.Combine(fixtureDirectory, "Glideslope.Providers.Specs.exe"),
            Path.Combine(fixtureDirectory, "agy.exe"), overwrite: true);
        var executablePath = Path.Combine(fixtureDirectory, "agy.exe");
        var expectedArguments = new[] { "-p", "/usage", "--output-format", "json" };

        // A completely missing executable path is reported without crashing the transport.
        var missingExecutablePath = Path.Combine(tempRoot, "fake-agy-missing", "agy.exe");
        var missingResult = await new AntigravityCliUsageTransport(null, TimeSpan.FromSeconds(5))
            .ReadAsync(missingExecutablePath, CancellationToken.None).ConfigureAwait(false);
        Program.Equal(AntigravityProcessOutcome.ExecutableNotFound, missingResult.Outcome,
            "a missing agy executable is reported without crashing");

        // Exact argument list, an empty owned working directory removed afterward, exit 0 -> Completed
        // with the synthetic fixture captured verbatim.
        var argsMarker = Path.Combine(tempRoot, "fake-agy-args.txt");
        var cwdMarker = Path.Combine(tempRoot, "fake-agy-cwd.txt");
        var stdoutFile = Path.Combine(tempRoot, "fake-agy-stdout.txt");
        File.WriteAllText(stdoutFile, SyntheticUsageFixture, Encoding.UTF8);
        await RunFakeAntigravityScenarioAsync(argsMarker, cwdMarker, stdoutFile, null, 0, false, async () =>
        {
            var transport = new AntigravityCliUsageTransport(null, TimeSpan.FromSeconds(15));
            var result = await transport.ReadAsync(executablePath, CancellationToken.None).ConfigureAwait(false);
            Program.Equal(AntigravityProcessOutcome.Completed, result.Outcome, "a clean exit reports Completed");
            Program.Equal(0, result.ExitCode, "exit code captured");
            Program.Equal(SyntheticUsageFixture, result.StandardOutput.TrimEnd('\r', '\n'), "stdout captured verbatim");
            Program.Equal(false, result.StandardOutputTooLarge, "stdout under the cap is not flagged too large");
        }).ConfigureAwait(false);
        var capturedArguments = File.ReadAllText(argsMarker).Split('\u001f');
        if (!capturedArguments.SequenceEqual(expectedArguments, StringComparer.Ordinal))
            throw new InvalidOperationException("Specification failed: exact Antigravity usage argument list.");
        // agy's background updater can create a console when `agy --version` runs,
        // so it must be off for every usage read Glideslope makes.
        Program.Equal(AntigravityCliUsageTransport.DisableAutoUpdateValue, File.ReadAllText(argsMarker + ".autoupdate"),
            "agy runs with its background updater off");
        var capturedWorkingDirectory = File.ReadAllText(cwdMarker);
        if (!capturedWorkingDirectory.StartsWith(Path.Combine(Path.GetTempPath(), "Glideslope-AntigravityUsage-"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Specification failed: Antigravity usage working directory naming.");
        if (Directory.Exists(capturedWorkingDirectory))
            throw new InvalidOperationException("Specification failed: Antigravity usage working directory was not removed afterward.");

        // Oversized stdout is capped at the exact byte limit and flagged, rather than growing unbounded.
        var oversizedCwdMarker = Path.Combine(tempRoot, "fake-agy-oversized-cwd.txt");
        await RunFakeAntigravityScenarioAsync(null, oversizedCwdMarker, null,
            (AntigravityCliUsageTransport.MaxStandardOutputBytes + 4096).ToString(), 0, false, async () =>
        {
            var transport = new AntigravityCliUsageTransport(null, TimeSpan.FromSeconds(15));
            var result = await transport.ReadAsync(executablePath, CancellationToken.None).ConfigureAwait(false);
            Program.Equal(AntigravityProcessOutcome.Completed, result.Outcome, "an oversized read still completes the process");
            Program.Equal(true, result.StandardOutputTooLarge, "oversized stdout is flagged");
            Program.Equal(AntigravityCliUsageTransport.MaxStandardOutputBytes, result.StandardOutput.Length,
                "captured stdout is capped at the byte limit");
        }).ConfigureAwait(false);

        // Wait for both the transport timer and the fake's ready signal before advancing the injected clock.
        var hangCwdMarker = Path.Combine(tempRoot, "fake-agy-hang-cwd.txt");
        using (var ready = new Program.FakeReadySignal())
        {
            await RunFakeAntigravityScenarioAsync(null, hangCwdMarker, null, null, 0, true, async () =>
            {
                var diagnostics = new Program.CapturingDiagnostics();
                var clock = new Program.ManualTimeProvider(DateTimeOffset.Parse("2026-09-27T00:00:00Z"));
                var timeout = TimeSpan.FromSeconds(25);
                var transport = new AntigravityCliUsageTransport(diagnostics, timeout, clock);
                var result = await TimeOutHangingFakeAsync(transport.ReadAsync(executablePath, CancellationToken.None),
                    ready, diagnostics, clock, timeout).ConfigureAwait(false);
                Program.Equal(AntigravityProcessOutcome.TimedOut, result.Outcome, "a wedged agy is reported as timed out");
            }, ready: ready).ConfigureAwait(false);
        }
        var hangWorkingDirectory = File.ReadAllText(hangCwdMarker);
        if (Directory.Exists(hangWorkingDirectory))
            throw new InvalidOperationException("Specification failed: killed Antigravity working directory was not removed afterward.");

        // A nonzero exit is still a Completed process outcome; the provider (not the transport) maps
        // that to a failure.
        await RunFakeAntigravityScenarioAsync(null, null, null, null, 9, false, async () =>
        {
            var transport = new AntigravityCliUsageTransport(null, TimeSpan.FromSeconds(15));
            var result = await transport.ReadAsync(executablePath, CancellationToken.None).ConfigureAwait(false);
            Program.Equal(AntigravityProcessOutcome.Completed, result.Outcome, "a nonzero exit still completes the process");
            Program.Equal(9, result.ExitCode, "nonzero exit code captured");
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// every completed run that is not proven zero-spend latches the provider off, at
    /// any exit code. Each shape runs end to end through the real AntigravityCliUsageTransport against the
    /// fake agy (this spec binary relaunched as agy.exe under the GLIDESLOPE_FAKE_ANTIGRAVITY_* markers),
    /// never the real CLI. After each latch, a second read with a good response configured must not launch
    /// the fake at all, which the args marker proves.
    /// </summary>
    private static async Task QuotaLatchShapeSpecsAsync(string tempRoot)
    {
        var localAppData = Path.Combine(tempRoot, "antigravity-rc2-runnable-appdata");
        CreateRunnableFakeAgy(localAppData);

        await AssertShapeLatchesAsync(tempRoot, localAppData, "nonzero-exit-tokens", NonzeroExitWithTokensFixture,
            oversizedBytes: null, exitCode: 1, expectedStatus: "not_zero_spend;exit_code=1").ConfigureAwait(false);
        await AssertShapeLatchesAsync(tempRoot, localAppData, "plain-text-reply", PlainTextReplyFixture,
            oversizedBytes: null, exitCode: 0, expectedStatus: "output_not_json;exit_code=0").ConfigureAwait(false);
        await AssertShapeLatchesAsync(tempRoot, localAppData, "oversized-output", stdout: null,
            oversizedBytes: (AntigravityCliUsageTransport.MaxStandardOutputBytes + 4096).ToString(), exitCode: 0,
            expectedStatus: "output_too_large;exit_code=0").ConfigureAwait(false);

        // A zero-spend JSON result never latches: a SUCCESS usage result reaches Ready and agy keeps being
        // launched, and agy's own zero-turn, zero-token failure status at exit 1 is an ordinary retryable
        // failure.
        var home = Path.Combine(tempRoot, "antigravity-rc21-zero-spend-home");
        WriteOnboarding(home, onboardingComplete: true);
        WriteProjectId(home, "synthetic-project-zero-spend");
        var diagnostics = new Program.CapturingDiagnostics();
        var provider = new AntigravityUsageProvider(new AntigravityHomeContextReader(home),
            new AntigravityCliUsageTransport(null, TimeSpan.FromSeconds(15)),
            static (_, _, _, _) => ValueTask.FromResult("opaque-antigravity-scope"),
            path: null, localApplicationData: localAppData, homeDirectory: home, isWindows: true,
            diagnostics: diagnostics, timeProvider: null);
        var usageFile = Path.Combine(tempRoot, "rc21-zero-spend-usage.txt");
        File.WriteAllText(usageFile, SyntheticUsageFixture, Encoding.UTF8);
        var failedFile = Path.Combine(tempRoot, "rc21-zero-spend-failed.txt");
        File.WriteAllText(failedFile, NonSuccessStatusFixture, Encoding.UTF8);

        await RunFakeAntigravityScenarioAsync(null, null, usageFile, null, 0, false, async () =>
        {
            var ready = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(ProviderStatus.Ready, ready.Status, "a zero-spend SUCCESS usage result reaches Ready");
        }).ConfigureAwait(false);
        await RunFakeAntigravityScenarioAsync(null, null, failedFile, null, 1, false, async () =>
        {
            var failed = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(ProviderStatus.UnknownError, failed.Status, "a zero-spend failure status at exit 1 is a retryable failure");
            Program.Equal("antigravity_usage_failed", failed.SafeErrorCode, "zero-spend failure safe code");
        }).ConfigureAwait(false);
        var relaunchMarker = Path.Combine(tempRoot, "rc21-zero-spend-relaunch-args.txt");
        await RunFakeAntigravityScenarioAsync(relaunchMarker, null, usageFile, null, 0, false, async () =>
        {
            var again = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(ProviderStatus.Ready, again.Status, "after zero-spend results the provider still reads");
        }).ConfigureAwait(false);
        Program.Equal(true, File.Exists(relaunchMarker), "agy is still launched after zero-spend results");
        Program.Equal(0, diagnostics.Events.Count(item => item.Code == "antigravity_latched"),
            "zero-spend results never trip the quota-spend latch");

        await AssertEmptyOutputDoesNotLatchAsync(tempRoot, localAppData, "empty-exit-1", stdout: null, exitCode: 1)
            .ConfigureAwait(false);
        await AssertEmptyOutputDoesNotLatchAsync(tempRoot, localAppData, "whitespace-exit-0", stdout: "  \r\n\t\n", exitCode: 0)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A run that printed nothing (or only whitespace) to stdout
    /// could not have carried a model reply, so it never latches whatever the exit code. It reports
    /// UnknownError, logs antigravity_empty_output with the exit code, and the next read launches agy
    /// again.
    /// </summary>
    private static async Task AssertEmptyOutputDoesNotLatchAsync(string tempRoot, string localAppData, string name,
        string? stdout, int exitCode)
    {
        var home = Path.Combine(tempRoot, $"antigravity-rc21-{name}-home");
        WriteOnboarding(home, onboardingComplete: true);
        WriteProjectId(home, "synthetic-project-empty-output");
        var diagnostics = new Program.CapturingDiagnostics();
        var provider = new AntigravityUsageProvider(new AntigravityHomeContextReader(home),
            new AntigravityCliUsageTransport(null, TimeSpan.FromSeconds(15)),
            static (_, _, _, _) => ValueTask.FromResult("opaque-antigravity-scope"),
            path: null, localApplicationData: localAppData, homeDirectory: home, isWindows: true,
            diagnostics: diagnostics, timeProvider: null);

        string? stdoutFile = null;
        if (stdout is not null)
        {
            stdoutFile = Path.Combine(tempRoot, $"rc21-{name}-stdout.txt");
            File.WriteAllText(stdoutFile, stdout, Encoding.UTF8);
        }
        var firstMarker = Path.Combine(tempRoot, $"rc21-{name}-first-args.txt");
        await RunFakeAntigravityScenarioAsync(firstMarker, null, stdoutFile, null, exitCode, false, async () =>
        {
            var first = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(ProviderStatus.UnknownError, first.Status, $"{name}: empty stdout is an ordinary retryable failure");
            Program.Equal("antigravity_usage_failed", first.SafeErrorCode, $"{name}: empty stdout safe code");
        }).ConfigureAwait(false);
        Program.Equal(true, File.Exists(firstMarker), $"{name}: the first read really launched the fake agy");
        Program.Equal(1, diagnostics.Events.Count(item => item.Code == "antigravity_empty_output" &&
            item.Status == $"exit_code={exitCode}"), $"{name}: antigravity_empty_output is logged with the exit code");
        Program.Equal(0, diagnostics.Events.Count(item => item.Code == "antigravity_latched"),
            $"{name}: empty stdout never trips the quota-spend latch");

        var usageFile = Path.Combine(tempRoot, $"rc21-{name}-good-stdout.txt");
        File.WriteAllText(usageFile, SyntheticUsageFixture, Encoding.UTF8);
        var secondMarker = Path.Combine(tempRoot, $"rc21-{name}-second-args.txt");
        await RunFakeAntigravityScenarioAsync(secondMarker, null, usageFile, null, 0, false, async () =>
        {
            var second = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(ProviderStatus.Ready, second.Status, $"{name}: the next read recovers");
        }).ConfigureAwait(false);
        Program.Equal(true, File.Exists(secondMarker), $"{name}: the next read launches agy again");
    }

    private static async Task AssertShapeLatchesAsync(string tempRoot, string localAppData, string name,
        string? stdout, string? oversizedBytes, int exitCode, string expectedStatus)
    {
        var home = Path.Combine(tempRoot, $"antigravity-rc21-{name}-home");
        WriteOnboarding(home, onboardingComplete: true);
        WriteProjectId(home, "synthetic-project-latch-shape");
        var diagnostics = new Program.CapturingDiagnostics();
        var provider = new AntigravityUsageProvider(new AntigravityHomeContextReader(home),
            new AntigravityCliUsageTransport(null, TimeSpan.FromSeconds(15)),
            static (_, _, _, _) => ValueTask.FromResult("opaque-antigravity-scope"),
            path: null, localApplicationData: localAppData, homeDirectory: home, isWindows: true,
            diagnostics: diagnostics, timeProvider: null);

        string? stdoutFile = null;
        if (stdout is not null)
        {
            stdoutFile = Path.Combine(tempRoot, $"rc21-{name}-stdout.txt");
            File.WriteAllText(stdoutFile, stdout, Encoding.UTF8);
        }
        var firstMarker = Path.Combine(tempRoot, $"rc21-{name}-first-args.txt");
        await RunFakeAntigravityScenarioAsync(firstMarker, null, stdoutFile, oversizedBytes, exitCode, false, async () =>
        {
            var first = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(ProviderStatus.SchemaChanged, first.Status, $"{name}: an unproven run latches the provider off");
            Program.Equal(AntigravityUsageProvider.CommandUnsupportedCode, first.SafeErrorCode, $"{name}: latched safe code");
        }).ConfigureAwait(false);
        Program.Equal(true, File.Exists(firstMarker), $"{name}: the first read really launched the fake agy");
        Program.Equal(1, diagnostics.Events.Count(item => item.Code == "antigravity_latched" && item.Status == expectedStatus),
            $"{name}: antigravity_latched is logged with reason {expectedStatus}");

        // A latched provider must not launch agy, even when a good response is available.
        var usageFile = Path.Combine(tempRoot, $"rc21-{name}-good-stdout.txt");
        File.WriteAllText(usageFile, SyntheticUsageFixture, Encoding.UTF8);
        var secondMarker = Path.Combine(tempRoot, $"rc21-{name}-second-args.txt");
        await RunFakeAntigravityScenarioAsync(secondMarker, null, usageFile, null, 0, false, async () =>
        {
            var second = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(ProviderStatus.SchemaChanged, second.Status, $"{name}: the latched provider keeps reporting the latch");
        }).ConfigureAwait(false);
        Program.Equal(false, File.Exists(secondMarker), $"{name}: a latched provider never launches agy again");
        Program.Equal(1, diagnostics.Events.Count(item => item.Code == "antigravity_latched"),
            $"{name}: the latch is logged once, not on every later read");
    }

    /// <summary>
    /// a run killed at the transport timeout (a signed-out agy waiting on a browser
    /// login) enters the 30-minute sign-in hold and reports NeedsSignIn, so the next scheduled read does
    /// not relaunch agy. Driven through the real transport with a hanging fake agy and a short injected
    /// timeout; the hold is measured on an injected clock, never a real wait.
    /// The hold requires the sign-in signal a signed-out agy gives, a
    /// Google sign-in URL on stderr, so the hanging fake prints one. A timeout without it is
    /// TimeoutWithoutSignInSpecsAsync.)
    /// The transport shares the provider's clock; the timeout advances after the fake prints its sign-in URL.
    /// </summary>
    private static async Task TimeoutHoldSpecsAsync(string tempRoot)
    {
        var localAppData = Path.Combine(tempRoot, "antigravity-rc2-runnable-appdata");
        var home = Path.Combine(tempRoot, "antigravity-rc22-home");
        WriteOnboarding(home, onboardingComplete: true);
        WriteProjectId(home, "synthetic-project-timeout");
        var clock = new Program.ManualTimeProvider(DateTimeOffset.Parse("2026-09-26T12:00:00Z"));
        var diagnostics = new Program.CapturingDiagnostics();
        var timeout = TimeSpan.FromSeconds(3);
        var provider = new AntigravityUsageProvider(new AntigravityHomeContextReader(home),
            new AntigravityCliUsageTransport(diagnostics, timeout, clock),
            static (_, _, _, _) => ValueTask.FromResult("opaque-antigravity-scope"),
            path: null, localApplicationData: localAppData, homeDirectory: home, isWindows: true,
            diagnostics: diagnostics, timeProvider: clock);

        using var ready = new Program.FakeReadySignal();
        await RunFakeAntigravityScenarioAsync(null, null, null, null, 0, true, async () =>
        {
            var timedOut = await TimeOutHangingFakeAsync(provider.ReadAsync(CancellationToken.None), ready, diagnostics, clock, timeout)
                .ConfigureAwait(false);
            Program.Equal(ProviderStatus.NeedsSignIn, timedOut.Status, "a timed-out agy run reports NeedsSignIn");
            Program.Equal("antigravity_sign_in_required", timedOut.SafeErrorCode, "a timed-out run uses the sign-in safe code");
            if (!timedOut.Actions.Any(action => action.Kind == ProviderActionKind.OpenOfficialInstructions))
                throw new InvalidOperationException("Specification failed: a timed-out run keeps the existing sign-in details.");
        }, stderrText: SignInUrlStderr, ready: ready).ConfigureAwait(false);
        Program.Equal(1, diagnostics.Events.Count(item => item.Code == "antigravity_usage_timeout"), "the timeout itself is logged");
        Program.Equal(1, diagnostics.Events.Count(item => item.Code == "antigravity_sign_in_required" &&
            item.Status == "hold_started_after_timeout"), "the timeout starts the sign-in hold and says why");
        Program.Equal(0, diagnostics.Events.Count(item => item.Code == "antigravity_latched"), "a timeout never trips the quota-spend latch");

        var usageFile = Path.Combine(tempRoot, "rc22-usage.txt");
        File.WriteAllText(usageFile, SyntheticUsageFixture, Encoding.UTF8);
        clock.Advance(TimeSpan.FromMinutes(29));
        var heldMarker = Path.Combine(tempRoot, "rc22-held-args.txt");
        await RunFakeAntigravityScenarioAsync(heldMarker, null, usageFile, null, 0, false, async () =>
        {
            var held = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(ProviderStatus.NeedsSignIn, held.Status, "the hold suppresses the next scheduled read");
        }).ConfigureAwait(false);
        Program.Equal(false, File.Exists(heldMarker), "agy is not relaunched during the hold after a timeout");

        clock.Advance(TimeSpan.FromMinutes(1));
        var afterMarker = Path.Combine(tempRoot, "rc22-after-args.txt");
        await RunFakeAntigravityScenarioAsync(afterMarker, null, usageFile, null, 0, false, async () =>
        {
            var after = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(ProviderStatus.Ready, after.Status, "after the hold a good read recovers");
        }).ConfigureAwait(false);
        Program.Equal(true, File.Exists(afterMarker), "agy is launched again once the hold ends");
    }

    /// <summary>
    /// The transport returns stdout captured before a timeout kill. A
    /// hanging fake agy that first prints a plain-text line (a model turn running long) latches the
    /// provider with reason timeout_unproven_output, and a later read never relaunches it. A hanging fake
    /// that first prints agy's zero-spend sign-in error keeps the sign-in hold instead. (The hanging fake
    /// with no stdout at all is TimeoutHoldSpecsAsync above.)
    /// Each timeout runs on an injected clock, advanced
    /// once the fake is running and has printed (TimeOutHangingFakeAsync), never a real wait.
    /// </summary>
    private static async Task TimeoutWithOutputSpecsAsync(string tempRoot)
    {
        var localAppData = Path.Combine(tempRoot, "antigravity-rc2-runnable-appdata");
        var executablePath = Path.Combine(localAppData, "agy", "bin", "agy.exe");
        var plainTextFile = Path.Combine(tempRoot, "rc2b-timeout-plain-text.txt");
        File.WriteAllText(plainTextFile, "Sure! Let me look into your usage for you.\n", Encoding.UTF8);
        var timeout = TimeSpan.FromSeconds(3);

        // Transport level: the captured text survives the kill.
        using (var ready = new Program.FakeReadySignal())
        {
            await RunFakeAntigravityScenarioAsync(null, null, plainTextFile, null, 0, true, async () =>
            {
                var diagnostics = new Program.CapturingDiagnostics();
                var clock = new Program.ManualTimeProvider(DateTimeOffset.Parse("2026-09-26T12:00:00Z"));
                var result = await TimeOutHangingFakeAsync(new AntigravityCliUsageTransport(diagnostics, timeout, clock)
                    .ReadAsync(executablePath, CancellationToken.None), ready, diagnostics, clock, timeout).ConfigureAwait(false);
                Program.Equal(AntigravityProcessOutcome.TimedOut, result.Outcome, "a hanging fake that printed first still times out");
                Program.Equal("Sure! Let me look into your usage for you.", result.StandardOutput.Trim(),
                    "stdout printed before the timeout kill is returned, not discarded");
            }, ready: ready).ConfigureAwait(false);
        }

        // Provider level: unproven output at a timeout latches.
        {
            var home = Path.Combine(tempRoot, "antigravity-rc2b-timeout-text-home");
            WriteOnboarding(home, onboardingComplete: true);
            WriteProjectId(home, "synthetic-project-timeout-text");
            var diagnostics = new Program.CapturingDiagnostics();
            var clock = new Program.ManualTimeProvider(DateTimeOffset.Parse("2026-09-26T12:00:00Z"));
            var provider = new AntigravityUsageProvider(new AntigravityHomeContextReader(home),
                new AntigravityCliUsageTransport(diagnostics, timeout, clock),
                static (_, _, _, _) => ValueTask.FromResult("opaque-antigravity-scope"),
                path: null, localApplicationData: localAppData, homeDirectory: home, isWindows: true,
                diagnostics: diagnostics, timeProvider: clock);
            using var ready = new Program.FakeReadySignal();
            await RunFakeAntigravityScenarioAsync(null, null, plainTextFile, null, 0, true, async () =>
            {
                var first = await TimeOutHangingFakeAsync(provider.ReadAsync(CancellationToken.None), ready, diagnostics, clock, timeout)
                    .ConfigureAwait(false);
                Program.Equal(ProviderStatus.SchemaChanged, first.Status, "a timeout with unproven stdout latches the provider off");
                Program.Equal(AntigravityUsageProvider.CommandUnsupportedCode, first.SafeErrorCode, "timeout latch safe code");
            }, ready: ready).ConfigureAwait(false);
            Program.Equal(1, diagnostics.Events.Count(item => item.Code == "antigravity_latched" &&
                item.Status == "timeout_unproven_output;exit_code=-1"), "the timeout latch is logged with its reason");
            Program.Equal(0, diagnostics.Events.Count(item => item.Code == "antigravity_sign_in_required"),
                "a timeout with unproven stdout does not start the sign-in hold");

            // Past the 30 minutes a hold would have lasted, a scheduled and a manual read still never launch.
            clock.Advance(TimeSpan.FromMinutes(31));
            var usageFile = Path.Combine(tempRoot, "rc2b-timeout-good-stdout.txt");
            File.WriteAllText(usageFile, SyntheticUsageFixture, Encoding.UTF8);
            var laterMarker = Path.Combine(tempRoot, "rc2b-timeout-later-args.txt");
            await RunFakeAntigravityScenarioAsync(laterMarker, null, usageFile, null, 0, false, async () =>
            {
                Program.Equal(ProviderStatus.SchemaChanged,
                    (await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Status,
                    "the latched provider keeps reporting the latch after 30 minutes");
                Program.Equal(ProviderStatus.SchemaChanged,
                    (await provider.ReadUserInitiatedAsync(CancellationToken.None).ConfigureAwait(false)).Status,
                    "a Refresh does not relaunch a provider latched at a timeout");
            }).ConfigureAwait(false);
            Program.Equal(false, File.Exists(laterMarker), "agy is never relaunched after a timeout latch");
        }

        // Provider level: agy's zero-spend sign-in error printed before hanging keeps the hold.
        {
            var home = Path.Combine(tempRoot, "antigravity-rc2b-timeout-signin-home");
            WriteOnboarding(home, onboardingComplete: true);
            WriteProjectId(home, "synthetic-project-timeout-signin");
            var signInFile = Path.Combine(tempRoot, "rc2b-timeout-signin.txt");
            File.WriteAllText(signInFile, SignInRequiredFixture + "\n", Encoding.UTF8);
            var diagnostics = new Program.CapturingDiagnostics();
            var clock = new Program.ManualTimeProvider(DateTimeOffset.Parse("2026-09-26T12:00:00Z"));
            var provider = new AntigravityUsageProvider(new AntigravityHomeContextReader(home),
                new AntigravityCliUsageTransport(diagnostics, timeout, clock),
                static (_, _, _, _) => ValueTask.FromResult("opaque-antigravity-scope"),
                path: null, localApplicationData: localAppData, homeDirectory: home, isWindows: true,
                diagnostics: diagnostics, timeProvider: clock);
            using var ready = new Program.FakeReadySignal();
            await RunFakeAntigravityScenarioAsync(null, null, signInFile, null, 0, true, async () =>
            {
                var result = await TimeOutHangingFakeAsync(provider.ReadAsync(CancellationToken.None), ready, diagnostics, clock, timeout)
                    .ConfigureAwait(false);
                Program.Equal(ProviderStatus.NeedsSignIn, result.Status, "a timeout after a zero-spend sign-in error keeps the hold");
            }, ready: ready).ConfigureAwait(false);
            Program.Equal(0, diagnostics.Events.Count(item => item.Code == "antigravity_latched"),
                "proven zero-spend output at a timeout never latches");
            Program.Equal(1, diagnostics.Events.Count(item => item.Code == "antigravity_sign_in_required" &&
                item.Status == "hold_started_after_timeout"), "the hold is logged as a timeout hold");
        }
    }

    /// <summary>
    /// a user-initiated read runs agy once despite the sign-in hold. Success clears the
    /// hold; failure leaves it in place. The onboarding precheck and the quota-spend latch still apply.
    /// </summary>
    private static async Task UserInitiatedReadSpecsAsync(string tempRoot)
    {
        var localAppData = Path.Combine(tempRoot, "antigravity-rc23-appdata");
        CreateFakeAgyExecutable(localAppData);
        var home = Path.Combine(tempRoot, "antigravity-rc23-home");
        WriteOnboarding(home, onboardingComplete: true);
        WriteProjectId(home, "synthetic-project-manual");
        var signIn = new AntigravityProcessResult(AntigravityProcessOutcome.Completed, 1, SignInRequiredFixture, false);
        var usage = new AntigravityProcessResult(AntigravityProcessOutcome.Completed, 0, SyntheticUsageFixture, false);
        var zeroSpendFailure = new AntigravityProcessResult(AntigravityProcessOutcome.Completed, 1, NonSuccessStatusFixture, false);

        // Hold active -> scheduled read suppressed -> user-initiated read runs agy and succeeds -> hold cleared.
        {
            var transport = new ScriptedAntigravityTransport(signIn, usage);
            var clock = new Program.ManualTimeProvider(DateTimeOffset.Parse("2026-09-26T12:00:00Z"));
            var diagnostics = new Program.CapturingDiagnostics();
            var provider = NewScriptedProvider(home, localAppData, transport, clock, diagnostics);
            Program.Equal(ProviderStatus.NeedsSignIn, (await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Status,
                "manual spec: the first read starts the hold");
            clock.Advance(TimeSpan.FromMinutes(2));
            Program.Equal(ProviderStatus.NeedsSignIn, (await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Status,
                "manual spec: a scheduled read inside the hold is suppressed");
            Program.Equal(1, transport.CallCount, "manual spec: the suppressed scheduled read did not launch agy");

            var manual = await provider.ReadUserInitiatedAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(2, transport.CallCount, "hold active -> manual read -> the provider runs agy");
            Program.Equal(ProviderStatus.Ready, manual.Status, "the user-initiated read after signing in reaches Ready");
            Program.Equal(1, diagnostics.Events.Count(item => item.Code == "antigravity_sign_in_hold_bypassed"),
                "the hold bypass is logged");
            Program.Equal(1, diagnostics.Events.Count(item => item.Code == "antigravity_sign_in_hold_cleared" &&
                item.Status == "user_initiated_read_succeeded"), "the cleared hold is logged with its cause");

            clock.Advance(TimeSpan.FromMinutes(1));
            Program.Equal(ProviderStatus.Ready, (await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Status,
                "after a successful manual read the next scheduled read runs normally");
            Program.Equal(3, transport.CallCount, "the cleared hold no longer suppresses scheduled reads");
        }

        // An automatic retry honors the sign-in hold like a scheduled poll, so it cannot open a
        // browser login; only the user's own Refresh bypasses the hold.
        {
            var transport = new ScriptedAntigravityTransport(signIn, usage);
            var clock = new Program.ManualTimeProvider(DateTimeOffset.Parse("2026-09-26T12:00:00Z"));
            var provider = NewScriptedProvider(home, localAppData, transport, clock, new Program.CapturingDiagnostics());
            await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            clock.Advance(TimeSpan.FromMinutes(2));
            Program.Equal(ProviderStatus.NeedsSignIn,
                (await provider.ReadAsync(ProviderReadIntent.AutomaticRetry, CancellationToken.None).ConfigureAwait(false)).Status,
                "an automatic retry inside the sign-in hold is suppressed");
            Program.Equal(1, transport.CallCount, "the automatic retry did not launch agy during the hold");
        }

        // A failed user-initiated read leaves the hold in place for scheduled reads.
        {
            var transport = new ScriptedAntigravityTransport(signIn, zeroSpendFailure, usage);
            var clock = new Program.ManualTimeProvider(DateTimeOffset.Parse("2026-09-26T12:00:00Z"));
            var provider = NewScriptedProvider(home, localAppData, transport, clock, new Program.CapturingDiagnostics());
            await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            clock.Advance(TimeSpan.FromMinutes(5));
            var manual = await provider.ReadUserInitiatedAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(2, transport.CallCount, "a manual read inside the hold runs agy once");
            Program.Equal(ProviderStatus.UnknownError, manual.Status, "the manual read's own failure is reported");
            clock.Advance(TimeSpan.FromMinutes(1));
            Program.Equal(ProviderStatus.NeedsSignIn, (await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Status,
                "after a failed manual read the hold continues");
            Program.Equal(2, transport.CallCount, "the continued hold still suppresses scheduled reads");
        }

        // A manual read that hits the sign-in failure again restarts the hold from that moment.
        {
            var transport = new ScriptedAntigravityTransport(signIn, signIn, usage);
            var clock = new Program.ManualTimeProvider(DateTimeOffset.Parse("2026-09-26T12:00:00Z"));
            var provider = NewScriptedProvider(home, localAppData, transport, clock, new Program.CapturingDiagnostics());
            await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            clock.Advance(TimeSpan.FromMinutes(20));
            Program.Equal(ProviderStatus.NeedsSignIn,
                (await provider.ReadUserInitiatedAsync(CancellationToken.None).ConfigureAwait(false)).Status,
                "a manual read that is still signed out reports NeedsSignIn");
            clock.Advance(TimeSpan.FromMinutes(15));
            await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(2, transport.CallCount, "the hold restarted by the manual read still holds 35 minutes after the first");
        }

        // A manual read never bypasses the quota-spend latch.
        {
            var transport = new ScriptedAntigravityTransport(
                new AntigravityProcessResult(AntigravityProcessOutcome.Completed, 0, GuardHelpCommandFixture, false), usage);
            var provider = NewScriptedProvider(home, localAppData, transport, null, new Program.CapturingDiagnostics());
            await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            var manual = await provider.ReadUserInitiatedAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(ProviderStatus.SchemaChanged, manual.Status, "a manual read reports the latch");
            Program.Equal(1, transport.CallCount, "a manual read never launches a latched provider's agy");
        }

        // A manual read never skips the onboarding precheck.
        {
            var incompleteHome = Path.Combine(tempRoot, "antigravity-rc23-incomplete-home");
            WriteOnboarding(incompleteHome, onboardingComplete: false);
            var transport = new ScriptedAntigravityTransport(usage);
            var provider = NewScriptedProvider(incompleteHome, localAppData, transport, null, new Program.CapturingDiagnostics());
            var manual = await provider.ReadUserInitiatedAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal("antigravity_not_signed_in", manual.SafeErrorCode, "a manual read still honors incomplete onboarding");
            Program.Equal(0, transport.CallCount, "a manual read never launches agy when onboarding is incomplete");
        }
    }

    /// <summary>
    /// the timeout classification, with a scripted transport. A complete
    /// zero-spend reading printed before the kill is used; an empty timeout with no sign-in URL is Offline and the
    /// second in a row (scheduled or manual) latches until Refresh; a completed run
    /// between two resets the count; a sign-in URL on stderr starts the hold and does not count.
    /// </summary>
    private static async Task TimeoutClassificationSpecsAsync(string tempRoot)
    {
        var localAppData = Path.Combine(tempRoot, "antigravity-c1-appdata");
        CreateFakeAgyExecutable(localAppData);
        var home = Path.Combine(tempRoot, "antigravity-c1-home");
        WriteOnboarding(home, onboardingComplete: true);
        WriteProjectId(home, "synthetic-project-timeouts");
        var usage = new AntigravityProcessResult(AntigravityProcessOutcome.Completed, 0, SyntheticUsageFixture, false);
        var emptyTimeout = new AntigravityProcessResult(AntigravityProcessOutcome.TimedOut, -1, string.Empty, false);
        var signInTimeout = emptyTimeout with { StandardErrorShowedSignIn = true };

        // A timed-out run whose stdout is a complete zero-spend SUCCESS result is a good reading.
        {
            var transport = new ScriptedAntigravityTransport(
                new AntigravityProcessResult(AntigravityProcessOutcome.TimedOut, -1, SyntheticUsageFixture, false), usage);
            var diagnostics = new Program.CapturingDiagnostics();
            var provider = NewScriptedProvider(home, localAppData, transport, null, diagnostics);
            var result = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(ProviderStatus.Ready, result.Status, "a complete zero-spend reading printed before the kill is used");
            Program.Equal(1, diagnostics.Events.Count(item => item.Code == "antigravity_usage_timeout_output_used"), "using it is logged");
            Program.Equal(0, diagnostics.Events.Count(item => item.Code == "antigravity_sign_in_required"), "no sign-in hold for a good reading");
            await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(2, transport.CallCount, "the next scheduled read runs agy (no hold)");
        }

        // Two unproven timeouts in a row latch. The first silent hang is retried at once in the same read, so
        // two hangs latch within one read.
        {
            var transport = new ScriptedAntigravityTransport(emptyTimeout, emptyTimeout, usage);
            var diagnostics = new Program.CapturingDiagnostics();
            var provider = NewScriptedProvider(home, localAppData, transport, null, diagnostics);
            var second = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(2, transport.CallCount, "the first silent hang is killed and agy relaunched at once");
            Program.Equal(1, diagnostics.Events.Count(item => item.Code == "antigravity_usage_retry_after_hang"),
                "the immediate retry is logged");
            Program.Equal(ProviderStatus.Offline, second.Status, "the second unproven timeout in a row latches");
            Program.Equal(AntigravityUsageProvider.UnresponsiveCode, second.SafeErrorCode,
                "a hang latch reports the tool as not responding");
            Program.Equal(1, diagnostics.Events.Count(item => item.Code == "antigravity_latched" &&
                item.Status == "unproven_timeout,consecutive=2;exit_code=-1"), "the latch names the unproven timeouts");
            // A scheduled poll honors the hang latch; Refresh clears it and runs agy once.
            Program.Equal(AntigravityUsageProvider.UnresponsiveCode,
                (await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false)).SafeErrorCode,
                "a scheduled poll keeps reporting the hang latch");
            Program.Equal(2, transport.CallCount, "a scheduled poll never runs agy while latched by hangs");
            var refreshed = await provider.ReadUserInitiatedAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(ProviderStatus.Ready, refreshed.Status, "Refresh clears a hang latch and reads again");
            Program.Equal(3, transport.CallCount, "Refresh runs agy exactly once");
            Program.Equal(1, diagnostics.Events.Count(item => item.Code == "antigravity_latch_cleared_by_refresh"),
                "the clearing is logged");
            Program.Equal(0, diagnostics.Events.Count(item => item.Code == "antigravity_sign_in_required"),
                "an unproven timeout never starts the sign-in hold");
        }

        // The scheduler's AutomaticRetry clears a hang latch as Refresh does and runs agy once.
        {
            var transport = new ScriptedAntigravityTransport(emptyTimeout, emptyTimeout, usage);
            var diagnostics = new Program.CapturingDiagnostics();
            var provider = NewScriptedProvider(home, localAppData, transport, null, diagnostics);
            await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(2, transport.CallCount, "setup: a hang and its immediate retry latch");
            var retried = await provider.ReadAsync(ProviderReadIntent.AutomaticRetry, CancellationToken.None).ConfigureAwait(false);
            Program.Equal(ProviderStatus.Ready, retried.Status, "an automatic retry clears a hang latch and reads again");
            Program.Equal(3, transport.CallCount, "the automatic retry runs agy exactly once");
        }

        // A Refresh that hangs again runs agy once and stops, limiting each user action to one run.
        {
            var transport = new ScriptedAntigravityTransport(emptyTimeout, emptyTimeout, emptyTimeout, usage);
            var diagnostics = new Program.CapturingDiagnostics();
            var provider = NewScriptedProvider(home, localAppData, transport, null, diagnostics);
            await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(2, transport.CallCount, "setup: a hang and its immediate retry stop polling");
            var refreshed = await provider.ReadUserInitiatedAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(3, transport.CallCount, "a Refresh that hangs again launches agy exactly once");
            Program.Equal(AntigravityUsageProvider.UnresponsiveCode, refreshed.SafeErrorCode, "and stops again");
            Program.Equal(1, diagnostics.Events.Count(item => item.Code == "antigravity_usage_retry_after_hang"),
                "only the first hang was retried at once, not the Refresh's");
        }

        // A completed run between two timeouts resets the count.
        {
            var transport = new ScriptedAntigravityTransport(emptyTimeout, usage, emptyTimeout, usage);
            var diagnostics = new Program.CapturingDiagnostics();
            var provider = NewScriptedProvider(home, localAppData, transport, null, diagnostics);
            // Each hang is retried immediately and completes, so both reads end Ready.
            Program.Equal(ProviderStatus.Ready, (await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Status,
                "the immediate retry after a hang recovers");
            Program.Equal(2, transport.CallCount, "one hang and one retry");
            Program.Equal(ProviderStatus.Ready, (await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Status,
                "a hang after a completed run is the first again, so it is retried too");
            Program.Equal(4, transport.CallCount, "a second hang and its retry");
            Program.Equal(0, diagnostics.Events.Count(item => item.Code == "antigravity_latched"), "no latch across a completed run");
            Program.Equal(2, diagnostics.Events.Count(item => item.Code == "antigravity_unproven_timeouts_reset" &&
                item.Status == "run_completed,previous=1"), "each reset is logged");
        }

        // A sign-in URL on stderr starts the hold and is not an unproven timeout.
        {
            var transport = new ScriptedAntigravityTransport(emptyTimeout, signInTimeout, emptyTimeout, usage);
            var clock = new Program.ManualTimeProvider(DateTimeOffset.Parse("2026-09-26T12:00:00Z"));
            var diagnostics = new Program.CapturingDiagnostics();
            var provider = NewScriptedProvider(home, localAppData, transport, clock, diagnostics);
            // The silent hang is retried immediately, and that retry is the sign-in timeout.
            var held = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(ProviderStatus.NeedsSignIn, held.Status, "a timeout with a sign-in URL on stderr asks for sign-in");
            Program.Equal(1, diagnostics.Events.Count(item => item.Code == "antigravity_sign_in_required" &&
                item.Status == "hold_started_after_timeout"), "the hold is logged");
            await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(2, transport.CallCount, "the hold suppresses the next scheduled read");
            clock.Advance(TimeSpan.FromMinutes(30));
            var afterHold = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(AntigravityUsageProvider.UnresponsiveCode, afterHold.SafeErrorCode,
                "the sign-in timeout did not reset the count: the next unproven timeout is the second");
        }

        // A completed run with nothing on stdout but a sign-in URL on stderr starts the hold too.
        {
            var transport = new ScriptedAntigravityTransport(
                new AntigravityProcessResult(AntigravityProcessOutcome.Completed, 1, string.Empty, false) { StandardErrorShowedSignIn = true });
            var diagnostics = new Program.CapturingDiagnostics();
            var provider = NewScriptedProvider(home, localAppData, transport, null, diagnostics);
            Program.Equal(ProviderStatus.NeedsSignIn, (await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Status,
                "an exit with only a sign-in URL on stderr asks for sign-in");
            Program.Equal(1, diagnostics.Events.Count(item => item.Code == "antigravity_sign_in_required" &&
                item.Status == "hold_started_stderr_sign_in"), "the stderr sign-in hold is logged");
        }
    }

    /// <summary>A silent timeout is Offline; a second consecutive timeout latches the provider until a
    /// user-initiated refresh. The test advances an injected clock after each launch signal.</summary>
    private static async Task TimeoutWithoutSignInSpecsAsync(string tempRoot)
    {
        var localAppData = Path.Combine(tempRoot, "antigravity-rc2-runnable-appdata");
        var home = Path.Combine(tempRoot, "antigravity-c1-process-home");
        WriteOnboarding(home, onboardingComplete: true);
        WriteProjectId(home, "synthetic-project-c1-process");
        var diagnostics = new Program.CapturingDiagnostics();
        var clock = new Program.ManualTimeProvider(DateTimeOffset.Parse("2026-09-27T00:00:00Z"));
        var timeout = TimeSpan.FromSeconds(3);
        var provider = new AntigravityUsageProvider(new AntigravityHomeContextReader(home),
            new AntigravityCliUsageTransport(diagnostics, timeout, clock),
            static (_, _, _, _) => ValueTask.FromResult("opaque-antigravity-scope"),
            path: null, localApplicationData: localAppData, homeDirectory: home, isWindows: true,
            diagnostics: diagnostics, timeProvider: clock);
        await RunFakeAntigravityScenarioAsync(null, null, null, null, 0, true, async () =>
        {
            // The first silent hang is killed and agy relaunched immediately; the relaunch hangs too, so the
            // one read ends latched, reported as not responding (not as a sign-in). Each of the two launches gets
            // its own process_started signal and its own exact-timeout advance.
            var readTask = provider.ReadAsync(CancellationToken.None);
            await diagnostics.WaitForCountAsync("antigravity_usage_process_started", 1).ConfigureAwait(false);
            clock.Advance(timeout);
            await diagnostics.WaitForCountAsync("antigravity_usage_process_started", 2).ConfigureAwait(false);
            clock.Advance(timeout);
            var result = await readTask.ConfigureAwait(false);
            Program.Equal(AntigravityUsageProvider.UnresponsiveCode, result.SafeErrorCode,
                "two silent hangs in a row (the second the immediate retry) latch, reported as not responding");
        }).ConfigureAwait(false);
        var laterMarker = Path.Combine(tempRoot, "c1-process-later-args.txt");
        var usageFile = Path.Combine(tempRoot, "c1-process-usage.txt");
        File.WriteAllText(usageFile, SyntheticUsageFixture, Encoding.UTF8);
        await RunFakeAntigravityScenarioAsync(laterMarker, null, usageFile, null, 0, false, async () =>
            await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Program.Equal(false, File.Exists(laterMarker), "a scheduled poll never launches agy after the hang latch");
        // Refresh clears the hang latch and launches agy once.
        await RunFakeAntigravityScenarioAsync(laterMarker, null, usageFile, null, 0, false, async () =>
            Program.Equal(ProviderStatus.Ready, (await provider.ReadUserInitiatedAsync(CancellationToken.None).ConfigureAwait(false)).Status,
                "Refresh after the hang latch reads again")).ConfigureAwait(false);
        Program.Equal(true, File.Exists(laterMarker), "Refresh launches agy after the hang latch");
        Program.Equal(2, diagnostics.Events.Count(item => item.Code == "antigravity_usage_timeout"), "both timeouts are logged");
    }

    /// <summary>Checks working-directory cleanup, UTF-8 stdout decoding, and sign-in URL detection on stderr.</summary>
    private static async Task WorkingDirectoryAndEncodingSpecsAsync(string tempRoot)
    {
        var executablePath = Path.Combine(tempRoot, "antigravity-rc2-runnable-appdata", "agy", "bin", "agy.exe");
        var stable = Path.Combine(tempRoot, "agy-stable", "cli-workdir", "agy");
        Directory.CreateDirectory(stable);
        File.WriteAllText(Path.Combine(stable, "leftover.txt"), "stale fixture");
        var unicodeFile = Path.Combine(tempRoot, "c10-usage.txt");
        File.WriteAllText(unicodeFile, SyntheticUsageFixture.Replace("Gemini Flash, Gemini Pro", "Gemini Flash · Gemini Pro"), Encoding.UTF8);
        var cwdMarker = Path.Combine(tempRoot, "agy-stable-cwd.txt");
        var entriesMarker = Path.Combine(tempRoot, "agy-stable-entries.txt");
        await RunFakeAntigravityScenarioAsync(null, cwdMarker, unicodeFile, null, 0, false, async () =>
        {
            var transport = new AntigravityCliUsageTransport(null, TimeSpan.FromSeconds(15), stable);
            var result = await transport.ReadAsync(executablePath, CancellationToken.None).ConfigureAwait(false);
            Program.Equal(AntigravityProcessOutcome.Completed, result.Outcome, "a run in the stable directory completes");
            Program.Equal(true, result.StandardOutput.Contains('·'), "stdout is read as UTF-8");
            Program.Equal(false, result.StandardErrorShowedSignIn, "ordinary stderr is not a sign-in URL");
        }, entriesMarker: entriesMarker).ConfigureAwait(false);
        Program.Equal(Path.GetFullPath(stable), Path.GetFullPath(File.ReadAllText(cwdMarker)), "agy runs in the stable directory");
        Program.Equal("0", File.ReadAllText(entriesMarker), "the stable directory is empty when agy starts");
        Program.Equal(true, Directory.Exists(stable), "the stable directory is kept");

        await RunFakeAntigravityScenarioAsync(null, null, null, null, 1, false, async () =>
        {
            var result = await new AntigravityCliUsageTransport(null, TimeSpan.FromSeconds(15)).ReadAsync(executablePath, CancellationToken.None)
                .ConfigureAwait(false);
            Program.Equal(true, result.StandardErrorShowedSignIn, "a sign-in URL on stderr is reported");
        }, stderrText: SignInUrlStderr).ConfigureAwait(false);
    }

    private static AntigravityUsageProvider NewScriptedProvider(string home, string localAppData,
        ScriptedAntigravityTransport transport, TimeProvider? clock, Program.CapturingDiagnostics diagnostics) =>
        new(new AntigravityHomeContextReader(home), transport,
            static (_, _, _, _) => ValueTask.FromResult("opaque-antigravity-scope"),
            path: null, localApplicationData: localAppData, homeDirectory: home, isWindows: true,
            diagnostics: diagnostics, timeProvider: clock);

    /// <summary>Installs a runnable fake agy at the known Windows location under
    /// <paramref name="localApplicationData"/>: a copy of this spec binary's output named agy.exe, the same
    /// self-reinvocation fixture ProcessSpecsAsync uses, so the production locator finds it.</summary>
    private static void CreateRunnableFakeAgy(string localApplicationData)
    {
        var installDirectory = Path.Combine(localApplicationData, "agy", "bin");
        Program.CopyDirectoryRecursive(AppContext.BaseDirectory, installDirectory);
        File.Copy(Path.Combine(installDirectory, "Glideslope.Providers.Specs.exe"),
            Path.Combine(installDirectory, "agy.exe"), overwrite: true);
    }

    private static async Task RunFakeAntigravityScenarioAsync(
        string? argsMarker, string? cwdMarker, string? stdoutFile, string? oversizedBytes, int exitCode, bool hang, Func<Task> action,
        string? stderrText = null, string? entriesMarker = null, Program.FakeReadySignal? ready = null)
    {
        // the stderr text and the working-directory entry count are set around the
        // original scenario variables below and restored afterwards; the ready event follows the same rule.
        var previousStderr = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_STDERR_TEXT");
        var previousEntries = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_CWD_ENTRIES_MARKER");
        var previousReady = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_READY_EVENT");
        Environment.SetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_STDERR_TEXT", stderrText);
        Environment.SetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_CWD_ENTRIES_MARKER", entriesMarker);
        Environment.SetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_READY_EVENT", ready?.Name);
        try
        {
            await RunFakeAntigravityCoreScenarioAsync(argsMarker, cwdMarker, stdoutFile, oversizedBytes, exitCode, hang, action).ConfigureAwait(false);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_STDERR_TEXT", previousStderr);
            Environment.SetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_CWD_ENTRIES_MARKER", previousEntries);
            Environment.SetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_READY_EVENT", previousReady);
        }
    }

    /// <summary>Times out one hanging fake agy launch on the injected clock. It waits for
    /// both signals first: the transport's antigravity_usage_process_started (its timeout timer is armed on the clock,
    /// so the advance is not lost) and the fake's ready event (its markers and output are written, so the kill does
    /// not beat its startup). Then it advances by exactly the timeout. A read that ends before the fake is ready fails
    /// the spec rather than hanging it.</summary>
    private static async Task<T> TimeOutHangingFakeAsync<T>(ValueTask<T> read, Program.FakeReadySignal ready,
        Program.CapturingDiagnostics diagnostics, Program.ManualTimeProvider clock, TimeSpan timeout)
    {
        var readTask = read.AsTask();
        var armed = Task.WhenAll(ready.Task, diagnostics.WaitForCountAsync("antigravity_usage_process_started", 1));
        if (await Task.WhenAny(readTask, armed).ConfigureAwait(false) == readTask)
            throw new InvalidOperationException("Specification failed: the read ended before the hanging fake agy was running.");
        clock.Advance(timeout);
        return await readTask.ConfigureAwait(false);
    }

    private static async Task RunFakeAntigravityCoreScenarioAsync(
        string? argsMarker, string? cwdMarker, string? stdoutFile, string? oversizedBytes, int exitCode, bool hang, Func<Task> action)
    {
        var previousChild = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_CHILD");
        var previousArgs = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_ARGS_MARKER");
        var previousCwd = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_CWD_MARKER");
        var previousStdoutFile = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_STDOUT_FILE");
        var previousOversized = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_OVERSIZED_BYTES");
        var previousExit = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_EXIT_CODE");
        var previousHang = Environment.GetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_HANG");
        var previousAutoUpdate = Environment.GetEnvironmentVariable(AntigravityCliUsageTransport.DisableAutoUpdateVariable);
        try
        {
            // Clear any inherited updater switch so only the transport's own override can set it to "1".
            Environment.SetEnvironmentVariable(AntigravityCliUsageTransport.DisableAutoUpdateVariable, "0");
            Environment.SetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_CHILD", "1");
            Environment.SetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_ARGS_MARKER", argsMarker);
            Environment.SetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_CWD_MARKER", cwdMarker);
            Environment.SetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_STDOUT_FILE", stdoutFile);
            Environment.SetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_OVERSIZED_BYTES", oversizedBytes);
            Environment.SetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_EXIT_CODE", exitCode.ToString());
            Environment.SetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_HANG", hang ? "1" : null);
            await action().ConfigureAwait(false);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_CHILD", previousChild);
            Environment.SetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_ARGS_MARKER", previousArgs);
            Environment.SetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_CWD_MARKER", previousCwd);
            Environment.SetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_STDOUT_FILE", previousStdoutFile);
            Environment.SetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_OVERSIZED_BYTES", previousOversized);
            Environment.SetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_EXIT_CODE", previousExit);
            Environment.SetEnvironmentVariable("GLIDESLOPE_FAKE_ANTIGRAVITY_HANG", previousHang);
            Environment.SetEnvironmentVariable(AntigravityCliUsageTransport.DisableAutoUpdateVariable, previousAutoUpdate);
        }
    }

    private static void CreateFakeAgyExecutable(string localApplicationData)
    {
        var installDirectory = Path.Combine(localApplicationData, "agy", "bin");
        Directory.CreateDirectory(installDirectory);
        // Metadata-only fixture: this file is never actually executed in the precheck/scope/latch specs,
        // which all use a scripted in-memory transport. It only needs to exist so the locator finds it.
        File.WriteAllText(Path.Combine(installDirectory, "agy.exe"), "metadata-only fixture", Encoding.UTF8);
    }

    private static void WriteOnboarding(string homeDirectory, bool onboardingComplete)
    {
        var cacheDirectory = Path.Combine(homeDirectory, ".gemini", "antigravity-cli", "cache");
        Directory.CreateDirectory(cacheDirectory);
        var flag = onboardingComplete ? "true" : "false";
        var json = $$"""{"consumerOnboardingComplete":{{flag}},"enterpriseOnboardingComplete":false,"onboardingComplete":{{flag}}}""";
        File.WriteAllText(Path.Combine(cacheDirectory, "onboarding.json"), json, Encoding.UTF8);
    }

    private static void WriteProjectId(string homeDirectory, string value)
    {
        var cacheDirectory = Path.Combine(homeDirectory, ".gemini", "antigravity-cli", "cache");
        Directory.CreateDirectory(cacheDirectory);
        File.WriteAllText(Path.Combine(cacheDirectory, "default_project_id.txt"), value, Encoding.UTF8);
    }

    /// <summary>Returns each scripted process result in turn, then keeps repeating the last one; counts
    /// calls so specs can prove the latch (or a failed precheck) really did stop agy from launching again.</summary>
    private sealed class ScriptedAntigravityTransport : IAntigravityUsageTransport
    {
        private readonly Queue<AntigravityProcessResult> _results;
        public int CallCount { get; private set; }

        public ScriptedAntigravityTransport(params AntigravityProcessResult[] results) =>
            _results = new Queue<AntigravityProcessResult>(results);

        public ValueTask<AntigravityProcessResult> ReadAsync(string executablePath, CancellationToken cancellationToken)
        {
            CallCount++;
            var result = _results.Count > 1 ? _results.Dequeue() : _results.Peek();
            return ValueTask.FromResult(result);
        }
    }
}
