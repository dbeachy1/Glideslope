using System.Diagnostics;
using System.Text;
using Glideslope.Domain;
using Glideslope.Providers;

internal static class GrokUsageSpecs
{
    private const string Auth = """{"methodId":"cached_token","principalId":"fixture-principal","principalType":"user","teamId":"fixture-team"}""";
    private const string Weekly = """{"config":{"creditUsagePercent":4,"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY","start":"2026-10-28T14:22:27.097-04:00","end":"2026-11-04T13:22:27.097-05:00"}},"subscription_tier":"SuperGrok"}""";
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-10-29T12:00:00Z");

    public static async Task RunAllAsync(string tempRoot)
    {
        ParserSpecs();
        await ProviderSpecsAsync().ConfigureAwait(false);
        await TransportProcessSpecsAsync(tempRoot).ConfigureAwait(false);
        ExecutableDiscoverySpecs(tempRoot);
    }

    private static void ParserSpecs()
    {
        var parsed = GrokUsageParser.Parse(Auth, Weekly, ObservedAt);
        Program.Equal("[\"principal\",\"user\",\"fixture-principal\",\"fixture-team\"]", parsed.Subject,
            "principal identity is encoded as an unambiguous serialized tuple");
        Program.Equal("SuperGrok", parsed.Plan, "upstream snake_case tier name is accepted");
        Program.Equal(0.96, parsed.WeeklyWindow.RemainingFraction, "4 percent used becomes 96 percent remaining");
        Program.Equal(TimeSpan.FromDays(7), parsed.WeeklyWindow.Duration, "weekly duration uses UTC instants across DST");
        Program.Equal(DateTimeOffset.Parse("2026-11-04T18:22:27.097Z"), parsed.WeeklyWindow.ResetAtUtc, "period end is the reset");

        foreach (var used in new[] { (Value: 0d, Remaining: 1d), (Value: 4d, Remaining: .96d),
                     (Value: 100d, Remaining: 0d), (Value: 145d, Remaining: 0d) })
        {
            var fixture = Weekly.Replace("\"creditUsagePercent\":4", $"\"creditUsagePercent\":{used.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}", StringComparison.Ordinal);
            Program.Equal(used.Remaining, GrokUsageParser.Parse(Auth, fixture, ObservedAt).WeeklyWindow.RemainingFraction,
                $"used percentage {used.Value} normalizes and clamps safely");
        }

        var camelTier = Weekly.Replace("subscription_tier", "subscriptionTier", StringComparison.Ordinal);
        Program.Equal("SuperGrok", GrokUsageParser.Parse(Auth, camelTier, ObservedAt).Plan, "optional camelCase plan projection is tolerated");
        var monthly = Weekly.Replace("USAGE_PERIOD_TYPE_WEEKLY", "USAGE_PERIOD_TYPE_MONTHLY", StringComparison.Ordinal);
        Throws<GrokUsageAccountException>(() => GrokUsageParser.Parse(Auth, monthly, ObservedAt), "monthly pool is unsupported");
        var unknown = Weekly.Replace("USAGE_PERIOD_TYPE_WEEKLY", "USAGE_PERIOD_TYPE_UNKNOWN", StringComparison.Ordinal);
        Throws<GrokUsageAccountException>(() => GrokUsageParser.Parse(Auth, unknown, ObservedAt), "unknown pool is unsupported");
        var malformed = Weekly.Replace("\"creditUsagePercent\":4", "\"creditUsagePercent\":\"4\"", StringComparison.Ordinal);
        Throws<GrokUsageSchemaException>(() => GrokUsageParser.Parse(Auth, malformed, ObservedAt), "malformed usage percentage is a schema change");
        var noReset = Weekly.Replace("2026-11-04T13:22:27.097-05:00", "2026-11-04T13:22:27.097", StringComparison.Ordinal);
        Throws<GrokUsageSchemaException>(() => GrokUsageParser.Parse(Auth, noReset, ObservedAt), "timestamp without explicit offset is rejected");
        Throws<GrokUsageAccountException>(() => GrokUsageParser.Parse("""{"methodId":"api_key","principalId":"id"}""", Weekly, ObservedAt),
            "API key identity is unsupported");
        Throws<GrokUsageAccountException>(() => GrokUsageParser.Parse("""{"methodId":"cached_token"}""", Weekly, ObservedAt),
            "missing authenticated principal is rejected");
    }

    private static async Task ProviderSpecsAsync()
    {
        var scopeInput = "";
        var provider = new GrokUsageProvider(new FakeTransport(new GrokUsageExchange(
            new GrokRpcReply("""{"protocolVersion":1}""", null), new GrokRpcReply(Auth, null), new GrokRpcReply(CurrentWeekly(), null))),
            (providerId, issuer, subject, _) =>
            {
                Program.Equal(ProviderIds.Grok, providerId, "scope resolver receives Grok provider ID");
                Program.Equal("xai.grok", issuer, "scope resolver receives Grok issuer");
                scopeInput = subject;
                return ValueTask.FromResult("opaque-proof-scope");
            });
        var result = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Program.Equal(ProviderStatus.Ready, result.Status, "weekly Grok billing is ready");
        Program.Equal("opaque-proof-scope", result.Snapshot?.AccountScope, "only opaque scope is persisted to snapshot");
        Program.Equal(false, result.Snapshot!.AccountScope.Contains("fixture-principal", StringComparison.Ordinal), "source identity is not included in scope");
        Program.Equal(true, scopeInput.Contains("fixture-principal", StringComparison.Ordinal), "the principal is supplied only to the resolver");
        Program.Equal(1, result.Snapshot.Buckets.Length, "Grok produces only its reported weekly bucket");

        var unsupported = new GrokUsageProvider(new FakeTransport(new GrokUsageExchange(
            new GrokRpcReply("""{"protocolVersion":1}""", null), new GrokRpcReply(Auth, null),
            new GrokRpcReply(CurrentWeekly().Replace("USAGE_PERIOD_TYPE_WEEKLY", "USAGE_PERIOD_TYPE_MONTHLY", StringComparison.Ordinal), null))),
            static (_, _, _, _) => ValueTask.FromResult("unused"));
        Program.Equal(ProviderStatus.UnsupportedAccount,
            (await unsupported.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Status,
            "provider maps monthly pool to UnsupportedAccount");
    }

    private static async Task TransportProcessSpecsAsync(string tempRoot)
    {
        var fixture = Path.Combine(tempRoot, "fake-grok-fixture");
        Program.CopyDirectoryRecursive(AppContext.BaseDirectory, fixture);
        File.Copy(Path.Combine(fixture, "Glideslope.Providers.Specs.exe"), Path.Combine(fixture, "grok.exe"), overwrite: true);
        var marker = Path.Combine(tempRoot, "fake-grok-sequence.txt");
        var updater = Path.Combine(tempRoot, "fake-grok-updater.txt");
        var cwd = Path.Combine(tempRoot, "fake-grok-cwd.txt");
        await WithFakeEnvironmentAsync(marker, updater, cwd, hangMarker: null, hang: false, async () =>
        {
            var transport = new GrokCliUsageTransport(fixture, null, true, null, TimeSpan.FromSeconds(10));
            var exchange = await transport.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(false, exchange.Billing.ErrorCode.HasValue, "billing request completes");
        }).ConfigureAwait(false);
        Program.Equal("initialize\u001f_x.ai/auth/info\u001f_x.ai/billing", File.ReadAllText(marker),
            "ACP sends only initialize, auth info and billing; it never authenticates or prompts");
        Program.Equal("1", File.ReadAllText(updater), "the provider process disables Grok auto-updates");
        var workingDirectory = File.ReadAllText(cwd);
        Program.Equal(false, Directory.Exists(workingDirectory), "owned temporary work directory is removed after CLI exit");

        foreach (var test in new[]
        {
            (Scenario: "signed-out", Status: ProviderStatus.NeedsSignIn, Calls: 2),
            (Scenario: "api-key", Status: ProviderStatus.UnsupportedAccount, Calls: 2),
            (Scenario: "malformed-auth", Status: ProviderStatus.SchemaChanged, Calls: 2),
            (Scenario: "protocol", Status: ProviderStatus.SchemaChanged, Calls: 1),
            (Scenario: "auth-missing", Status: ProviderStatus.SchemaChanged, Calls: 2),
            (Scenario: "auth-offline", Status: ProviderStatus.Offline, Calls: 2),
            (Scenario: "billing-sign-in", Status: ProviderStatus.NeedsSignIn, Calls: 3),
            (Scenario: "billing-missing", Status: ProviderStatus.SchemaChanged, Calls: 3),
            (Scenario: "malformed-error", Status: ProviderStatus.SchemaChanged, Calls: 3)
        })
        {
            await WithFakeEnvironmentAsync(marker, null, cwd, null, hang: false, async () =>
            {
                var transport = new GrokCliUsageTransport(fixture, null, true, null, TimeSpan.FromSeconds(10));
                var provider = new GrokUsageProvider(transport, static (_, _, _, _) => ValueTask.FromResult("unused"));
                var failure = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                Program.Equal(test.Status, failure.Status, $"{test.Scenario} produces the actionable provider status");
                Program.Equal(true, failure.Snapshot is null, $"{test.Scenario} cannot produce a usage snapshot");
                if (test.Status == ProviderStatus.NeedsSignIn)
                    Program.Equal(true, failure.Actions.Any(action => action.Kind == ProviderActionKind.OpenOfficialInstructions),
                        "signed-out failures offer official sign-in instructions");
            }, test.Scenario).ConfigureAwait(false);
            Program.Equal(test.Calls, File.ReadAllText(marker).Split('\u001f').Length,
                $"{test.Scenario} stops before later requests when initialization or account selection is invalid");
            Program.Equal(false, Directory.Exists(File.ReadAllText(cwd)), $"{test.Scenario} stops before releasing its work directory");
        }

        var hangMarker = Path.Combine(tempRoot, "fake-grok-hang-started.txt");
        var hangCwd = Path.Combine(tempRoot, "fake-grok-hang-cwd.txt");
        await WithFakeEnvironmentAsync(null, null, hangCwd, hangMarker, hang: true, async () =>
        {
            var transport = new GrokCliUsageTransport(fixture, null, true, null, TimeSpan.FromSeconds(30));
            using var cancellation = new CancellationTokenSource();
            var pending = transport.ReadAsync(cancellation.Token).AsTask();
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(hangMarker) && deadline.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20).ConfigureAwait(false);
            Program.Equal(true, File.Exists(hangMarker), "fake process started before cancellation");
            cancellation.Cancel();
            await Program.ThrowsAsync<OperationCanceledException>(() => pending, "cancellation propagates after the owned child stops").ConfigureAwait(false);
        }).ConfigureAwait(false);
        Program.Equal(false, Directory.Exists(File.ReadAllText(hangCwd)), "cancelled CLI is stopped before its owned work directory is removed");
    }

    private static void ExecutableDiscoverySpecs(string tempRoot)
    {
        var path = Path.Combine(tempRoot, "grok-path");
        var home = Path.Combine(tempRoot, "grok-home");
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(home, ".grok", "bin"));
        var fallback = Path.Combine(home, ".grok", "bin", "grok.exe");
        File.WriteAllText(fallback, "fixture");
        Program.Equal(Path.GetFullPath(fallback), GrokExecutableLocator.Find(path, home, true), "native ~/.grok/bin discovery works when PATH is stale");
        var onPath = Path.Combine(path, "grok.exe");
        File.WriteAllText(onPath, "fixture");
        Program.Equal(Path.GetFullPath(onPath), GrokExecutableLocator.Find(path, home, true), "PATH executable takes priority");
    }

    public static async Task<int> RunLiveProofAsync()
    {
        var diagnostics = new Program.CapturingDiagnostics();
        var transport = new GrokCliUsageTransport(Environment.GetEnvironmentVariable("PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), OperatingSystem.IsWindows(), diagnostics,
            TimeSpan.FromSeconds(60));
        var provider = new GrokUsageProvider(transport, static (_, _, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult("live-proof-scope");
        }, diagnostics);
        var result = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        if (result.Snapshot is not { } snapshot)
        {
            Console.WriteLine($"Grok live-proof status: {result.Status}; code: {result.SafeErrorCode ?? "none"}");
            return 1;
        }
        var weekly = snapshot.Buckets.Single(bucket => bucket.Role == QuotaBucketRole.Weekly);
        Console.WriteLine($"Grok live-proof status: {result.Status}; remaining: {weekly.RemainingFraction:P0}; reset UTC: {weekly.ResetAtUtc:u}; source: {weekly.SourceSemantics}");
        return result.Status == ProviderStatus.Ready ? 0 : 1;
    }

    private static async Task WithFakeEnvironmentAsync(string? sequenceMarker, string? updaterMarker, string? cwdMarker,
        string? hangMarker, bool hang, Func<Task> action, string? scenario = null)
    {
        var names = new[] { "GLIDESLOPE_FAKE_GROK_CHILD", "GLIDESLOPE_FAKE_GROK_SEQUENCE_MARKER",
            "GLIDESLOPE_FAKE_GROK_UPDATER_MARKER", "GLIDESLOPE_FAKE_GROK_CWD_MARKER",
            "GLIDESLOPE_FAKE_GROK_HANG_MARKER", "GLIDESLOPE_FAKE_GROK_HANG", GrokCliUsageTransport.DisableAutoUpdaterVariable,
            "GLIDESLOPE_FAKE_GROK_SCENARIO" };
        var old = names.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            Environment.SetEnvironmentVariable(names[0], "1");
            Environment.SetEnvironmentVariable(names[1], sequenceMarker);
            Environment.SetEnvironmentVariable(names[2], updaterMarker);
            Environment.SetEnvironmentVariable(names[3], cwdMarker);
            Environment.SetEnvironmentVariable(names[4], hangMarker);
            Environment.SetEnvironmentVariable(names[5], hang ? "1" : null);
            Environment.SetEnvironmentVariable(names[6], "0");
            Environment.SetEnvironmentVariable(names[7], scenario);
            await action().ConfigureAwait(false);
        }
        finally
        {
            for (var i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], old[i]);
        }
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Specification failed: {message}.");
    }

    private static string CurrentWeekly()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var end = start.AddDays(7);
        var fixture = System.Text.Json.JsonSerializer.Serialize(new
        {
            config = new { creditUsagePercent = 4, currentPeriod = new { type = "USAGE_PERIOD_TYPE_WEEKLY", start, end } },
            subscription_tier = "SuperGrok"
        });
        return fixture;
    }

    private sealed class FakeTransport(GrokUsageExchange exchange) : IGrokUsageTransport
    {
        public ValueTask<GrokUsageExchange> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(exchange);
        }
    }
}
