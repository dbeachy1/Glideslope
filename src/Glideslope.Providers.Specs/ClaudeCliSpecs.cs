using System.Diagnostics;
using Glideslope.Domain;
using Glideslope.Providers;
using Glideslope.Storage;

/// <summary>
/// Claude CLI source design §3.7: the Claude card reads the official CLI only. These specs
/// cover the JSON envelope's zero-spend verdict, the loose parsing of the usage lines, the provider's
/// statuses and its spent-turn latch, and the real transport against a fake `claude.exe` (this same spec
/// binary relaunched, see Program.RunFakeClaudeChild).
/// </summary>
internal static class ClaudeCliSpecs
{
    private static readonly TimeZoneInfo NewYork = FindZone("America/New_York", "Eastern Standard Time");
    private static readonly DateTimeOffset ReceivedAt = new(2027, 2, 3, 16, 30, 0, TimeSpan.Zero);   // Wed Feb 3, 11:30 am New York

    private const string UsageText =
        "You are currently using your subscription to power your Claude Code usage\n\n" +
        "Current session: 37.5% used · resets Feb 3, 1:15pm (America/New_York)\n" +
        "Current week (all models): 28% used · resets Feb 10, 9:30am (America/New_York)\n" +
        "Current week (Fable): 12.5% used · resets Feb 9, 4:45pm (America/New_York)\n\n" +
        "What's contributing to your limits usage?\n" +
        "Last 24h · 7 requests · 2 sessions\n  43% of your usage came from background sessions\n";

    // The real CLI writes the "·" separator as raw UTF-8, not as a \u escape; the fixture must too, or the
    // transport's UTF-8 reader would go untested.
    private static readonly System.Text.Json.JsonSerializerOptions RawUnicode = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string Envelope(string result, int turns = 0, double cost = 0, bool isError = false, string command = "usage") =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["is_error"] = isError,
            ["num_turns"] = turns,
            ["total_cost_usd"] = cost,
            ["result"] = result,
            ["local_command"] = command,
            ["session_id"] = "synthetic-session"
        }, RawUnicode);

    public static void ParserSpecs()
    {
        // Envelope verdicts (design §2 rule 2).
        Program.Equal(ClaudeCliEnvelopeVerdict.ZeroSpend, ClaudeCliUsageParser.ReadEnvelope(Envelope(UsageText)).Verdict, "a zero-turn usage result is zero spend");
        Program.Equal(ClaudeCliEnvelopeVerdict.SpentTurn, ClaudeCliUsageParser.ReadEnvelope(Envelope(UsageText, turns: 1)).Verdict, "one model turn is a spent turn");
        Program.Equal(ClaudeCliEnvelopeVerdict.SpentTurn, ClaudeCliUsageParser.ReadEnvelope(Envelope(UsageText, cost: 0.01)).Verdict, "any cost is a spent turn");
        Program.Equal(ClaudeCliEnvelopeVerdict.SpentTurn, ClaudeCliUsageParser.ReadEnvelope(Envelope("Sure, here is a poem", turns: 1, command: "")).Verdict,
            "a model reply with a turn is a spent turn even without a local command");
        Program.Equal(ClaudeCliEnvelopeVerdict.NotUsage, ClaudeCliUsageParser.ReadEnvelope(Envelope(UsageText, isError: true)).Verdict, "an error result is not usage");
        Program.Equal(ClaudeCliEnvelopeVerdict.NotUsage, ClaudeCliUsageParser.ReadEnvelope(Envelope(UsageText, command: "help")).Verdict, "another local command is not usage");
        Program.Equal(ClaudeCliEnvelopeVerdict.NotUsage, ClaudeCliUsageParser.ReadEnvelope("Current week: 28% used").Verdict, "plain text is not a usage envelope");
        Program.Equal(ClaudeCliEnvelopeVerdict.NotUsage, ClaudeCliUsageParser.ReadEnvelope("""{"type":"result","local_command":"usage","num_turns":0}""").Verdict, "a missing result text is not usage");
        Program.Equal(ClaudeCliEnvelopeVerdict.NotUsage, ClaudeCliUsageParser.ReadEnvelope("""{"type":"result","local_command":"usage","result":"x"}""").Verdict, "missing num_turns is not proven zero spend");
        Program.Equal(ClaudeCliEnvelopeVerdict.NotUsage, ClaudeCliUsageParser.ReadEnvelope("").Verdict, "empty output is not usage");
        Program.Equal(UsageText, ClaudeCliUsageParser.ReadEnvelope(Envelope(UsageText)).ResultText, "the result text is returned for parsing");

        // The session, all-model weekly, and optional Fable weekly lines.
        var parsed = ClaudeCliUsageParser.Parse(UsageText, ReceivedAt, TimeZoneInfo.Utc);
        Program.Equal(ClaudeCliUsageParser.ShortBucketId, parsed.ShortWindow?.Id, "session line is the 5-hour bucket");
        Program.Equal(QuotaBucketRole.Short, parsed.ShortWindow?.Role, "5-hour role");
        Program.Equal(0.625d, parsed.ShortWindow?.RemainingFraction, "37.5% used is 62.5% remaining");
        Program.Equal(TimeSpan.FromHours(5), parsed.ShortWindow?.Duration, "5-hour duration");
        Program.Equal(new DateTimeOffset(2027, 2, 3, 18, 15, 0, TimeSpan.Zero), parsed.ShortWindow?.ResetAtUtc, "1:15pm New York is 18:15 UTC");
        Program.Equal(ClaudeCliUsageParser.WeeklyBucketId, parsed.WeeklyWindow.Id, "'all models' line is the weekly bucket");
        Program.Equal(QuotaBucketRole.Weekly, parsed.WeeklyWindow.Role, "weekly role");
        Program.Equal(0.72d, parsed.WeeklyWindow.RemainingFraction, "28% used is 72% remaining");
        Program.Equal(new DateTimeOffset(2027, 2, 10, 14, 30, 0, TimeSpan.Zero), parsed.WeeklyWindow.ResetAtUtc, "Feb 10 9:30am New York is 14:30 UTC");
        Program.Equal($"{ClaudeCliUsageParser.SourceId}/seven_day", parsed.WeeklyWindow.SourceSemantics, "weekly semantics carry the CLI source");
        Program.Equal("claude.featured.weekly.fable.fable", parsed.FeaturedWeeklyWindow?.Id, "the Fable line keeps the stored featured bucket id");
        Program.Equal(QuotaBucketRole.FeaturedWeekly, parsed.FeaturedWeeklyWindow?.Role, "featured weekly role");
        Program.Equal(0.875d, parsed.FeaturedWeeklyWindow?.RemainingFraction, "12.5% used is 87.5% remaining");
        Program.Equal($"{ClaudeCliUsageParser.SourceId}/weekly_scoped/fable", parsed.FeaturedWeeklyWindow?.SourceSemantics, "featured semantics");
        Program.Equal<string?>(null, parsed.UnknownZone, "a known zone reports no issue");

        // Optional lines.
        var noSession = ClaudeCliUsageParser.Parse(UsageText.Replace("Current session: 37.5% used · resets Feb 3, 1:15pm (America/New_York)\n", ""), ReceivedAt, TimeZoneInfo.Utc);
        Program.Equal<QuotaBucket?>(null, noSession.ShortWindow, "no session line means no active 5-hour window");
        var noFable = ClaudeCliUsageParser.Parse(UsageText.Replace("Current week (Fable): 12.5% used · resets Feb 9, 4:45pm (America/New_York)\n", ""), ReceivedAt, TimeZoneInfo.Utc);
        Program.Equal<QuotaBucket?>(null, noFable.FeaturedWeeklyWindow, "no featured line means no featured bucket");
        Program.Throws<FormatException>(() => ClaudeCliUsageParser.Parse("Current session: 37.5% used · resets Feb 3, 1:15pm (UTC)\n", ReceivedAt, TimeZoneInfo.Utc),
            "no weekly line is a shape change");
        Program.Throws<FormatException>(() => ClaudeCliUsageParser.Parse("Current week (all models): 28% used\n", ReceivedAt, TimeZoneInfo.Utc),
            "a weekly line without a reset is a shape change");

        // Loose matching: spacing, a plain hyphen, a bare "Current week", 12-hour without minutes, lower-case zone words.
        var loose = ClaudeCliUsageParser.Parse("current week:  7 % used - resets oct 1, 6am (UTC)\n", ReceivedAt, NewYork);
        Program.Equal(0.93d, loose.WeeklyWindow.RemainingFraction, "loose weekly line parses");
        Program.Equal(new DateTimeOffset(2027, 10, 1, 6, 0, 0, TimeSpan.Zero), loose.WeeklyWindow.ResetAtUtc, "an hour without minutes parses");

        // Zones: none falls back to the local zone; an unknown one falls back and is reported.
        var noZone = ClaudeCliUsageParser.Parse("Current week (all models): 28% used · resets Feb 10, 9:30am\n", ReceivedAt, NewYork);
        Program.Equal(new DateTimeOffset(2027, 2, 10, 14, 30, 0, TimeSpan.Zero), noZone.WeeklyWindow.ResetAtUtc, "no zone uses the local zone");
        var unknownZone = ClaudeCliUsageParser.Parse("Current week (all models): 28% used · resets Feb 10, 9:30am (Mars/Olympus)\n", ReceivedAt, NewYork);
        Program.Equal("Mars/Olympus", unknownZone.UnknownZone, "an unknown zone is reported");
        Program.Equal(new DateTimeOffset(2027, 2, 10, 14, 30, 0, TimeSpan.Zero), unknownZone.WeeklyWindow.ResetAtUtc, "an unknown zone uses the local zone");

        // Year rule: a reset read late in December that lands in January is next year; one read in January
        // that names December is not last year (it is next December, which cannot happen, but the rule
        // only ever moves forward).
        var december = new DateTimeOffset(2026, 12, 30, 12, 0, 0, TimeSpan.Zero);
        var january = ClaudeCliUsageParser.Parse("Current week (all models): 1% used · resets Jan 2, 9:30am (UTC)\n", december, TimeZoneInfo.Utc);
        Program.Equal(new DateTimeOffset(2027, 1, 2, 9, 30, 0, TimeSpan.Zero), january.WeeklyWindow.ResetAtUtc, "a January reset read in December is next year");
        var sameDay = ClaudeCliUsageParser.Parse("Current session: 1% used · resets Feb 3, 11:00am (America/New_York)\n" +
                                                 "Current week (all models): 1% used · resets Feb 10, 9:30am (America/New_York)\n", ReceivedAt, TimeZoneInfo.Utc);
        Program.Equal(new DateTimeOffset(2027, 2, 3, 16, 0, 0, TimeSpan.Zero), sameDay.ShortWindow?.ResetAtUtc,
            "a reset half an hour before the read stays this year (12-hour slack)");

        // Session lines without an absolute reset are omitted; a weekly line with a relative reset is invalid.
        var morning = ClaudeCliUsageParser.Parse("Current session: 0% used\nCurrent week (all models): 28% used · resets Feb 10, 9:30am (UTC)\n", ReceivedAt, TimeZoneInfo.Utc);
        Program.Equal<QuotaBucket?>(null, morning.ShortWindow, "a session line without a reset means no active 5-hour window");
        Program.Equal(0.72d, morning.WeeklyWindow.RemainingFraction, "the weekly numbers still show");
        var relative = ClaudeCliUsageParser.Parse("Current session: 5% used · resets in 3h\nCurrent week (all models): 28% used · resets Feb 10, 9:30am (UTC)\n", ReceivedAt, TimeZoneInfo.Utc);
        Program.Equal<QuotaBucket?>(null, relative.ShortWindow, "a session line with an unreadable reset is dropped");
        Program.Throws<FormatException>(() => ClaudeCliUsageParser.Parse("Current week (all models): 28% used · resets in 4d\n", ReceivedAt, TimeZoneInfo.Utc),
            "a weekly line with an unreadable reset is a shape change");
        var decimalPercent = ClaudeCliUsageParser.Parse("Current week (all models): 20.5% used · resets Feb 10, 9:30am (UTC)\n", ReceivedAt, TimeZoneInfo.Utc);
        Program.Equal(0.795d, decimalPercent.WeeklyWindow.RemainingFraction, "a decimal percent parses");
        var septAt = ClaudeCliUsageParser.Parse("Current week (all models): 28% used · resets Sept 30 at 8:15pm (UTC)\n", ReceivedAt, TimeZoneInfo.Utc);
        Program.Equal(new DateTimeOffset(2027, 9, 30, 20, 15, 0, TimeSpan.Zero), septAt.WeeklyWindow.ResetAtUtc, "'Sept 30 at 8:15pm' parses");
        var dayFirst = ClaudeCliUsageParser.Parse("Current week (all models): 28% used · resets 30 Sep, 20:15 (UTC)\n", ReceivedAt, TimeZoneInfo.Utc);
        Program.Equal(new DateTimeOffset(2027, 9, 30, 20, 15, 0, TimeSpan.Zero), dayFirst.WeeklyWindow.ResetAtUtc, "a day-first 24-hour reset parses");
        var timeOnly = ClaudeCliUsageParser.Parse("Current session: 5% used · resets 9:45pm (UTC)\nCurrent week (all models): 28% used · resets Feb 10, 9:30am (UTC)\n", ReceivedAt, TimeZoneInfo.Utc);
        Program.Equal(new DateTimeOffset(2027, 2, 3, 21, 45, 0, TimeSpan.Zero), timeOnly.ShortWindow?.ResetAtUtc, "a bare time is the next occurrence today");
        var timeOnlyPast = ClaudeCliUsageParser.Parse("Current session: 5% used · resets 6:00am (UTC)\nCurrent week (all models): 28% used · resets Feb 10, 9:30am (UTC)\n", ReceivedAt, TimeZoneInfo.Utc);
        Program.Equal(new DateTimeOffset(2027, 2, 4, 6, 0, 0, TimeSpan.Zero), timeOnlyPast.ShortWindow?.ResetAtUtc, "a bare time already well past is tomorrow");
        var twoFeatured = ClaudeCliUsageParser.Parse("Current week (all models): 28% used · resets Feb 10, 9:30am (UTC)\nCurrent week (Sonnet only): 10% used · resets Feb 10, 9:30am (UTC)\nCurrent week (Fable): 12.5% used · resets Feb 9, 4:45pm (UTC)\n", ReceivedAt, TimeZoneInfo.Utc);
        Program.Equal("claude.featured.weekly.fable.fable", twoFeatured.FeaturedWeeklyWindow?.Id, "among several featured lines Fable wins");

        // Spend hidden behind a warning line, or cut off by a kill, still latches.
        Program.Equal(ClaudeCliEnvelopeVerdict.SpentTurn, ClaudeCliUsageParser.ReadEnvelope("Update available: 2.2.0\n" + Envelope(UsageText, turns: 1)).Verdict,
            "a spent turn behind a leading warning line is found in the raw text");
        Program.Equal(ClaudeCliEnvelopeVerdict.SpentTurn, ClaudeCliUsageParser.ReadEnvelope("""{"type":"result","num_turns":3,"total_cost_usd":0.02,"res""").Verdict,
            "a spent turn in a truncated envelope is found in the raw text");
        Program.Equal(ClaudeCliEnvelopeVerdict.ZeroSpend, ClaudeCliUsageParser.ReadEnvelope("Update available: 2.2.0\n" + Envelope(UsageText)).Verdict,
            "a warning line before a zero-spend envelope does not cost the reading");

        // token counts and per-model usage are part of the zero-spend proof. This
        // synthetic envelope carries zero input/output tokens and an empty modelUsage object.
        Program.Equal(ClaudeCliEnvelopeVerdict.ZeroSpend, ClaudeCliUsageParser.ReadEnvelope(EnvelopeWith(UsageText,
            """{"input_tokens":0,"output_tokens":0,"cache_read_input_tokens":0}""", "{}")).Verdict, "the synthetic shape is zero spend");
        Program.Equal(ClaudeCliEnvelopeVerdict.SpentTurn, ClaudeCliUsageParser.ReadEnvelope(EnvelopeWith(UsageText,
            """{"input_tokens":12,"output_tokens":0}""", "{}")).Verdict, "input tokens are spend even at zero turns");
        Program.Equal(ClaudeCliEnvelopeVerdict.SpentTurn, ClaudeCliUsageParser.ReadEnvelope(EnvelopeWith(UsageText,
            """{"input_tokens":0,"output_tokens":7}""", "{}")).Verdict, "output tokens are spend");
        Program.Equal(ClaudeCliEnvelopeVerdict.SpentTurn, ClaudeCliUsageParser.ReadEnvelope(EnvelopeWith(UsageText,
            """{"input_tokens":0,"output_tokens":0}""", """{"claude-model":{"inputTokens":3,"costUSD":0}}""")).Verdict, "per-model usage above zero is spend");
        Program.Equal(ClaudeCliEnvelopeVerdict.NotUsage, ClaudeCliUsageParser.ReadEnvelope(EnvelopeWith(UsageText,
            """{"input_tokens":0,"output_tokens":0}""", """{"claude-model":{"inputTokens":0}}""")).Verdict,
            "a non-empty modelUsage with nothing spent is not proven zero spend");
        Program.Equal(ClaudeCliEnvelopeVerdict.SpentTurn, ClaudeCliUsageParser.ReadEnvelope(
            """{"type":"result","num_turns":0,"usage":{"input_tokens":5""").Verdict, "tokens in a truncated envelope are spend");

        // the year among last, this and next; a bare time among yesterday, today, tomorrow.
        var newYear = new DateTimeOffset(2027, 1, 1, 0, 30, 0, TimeSpan.Zero);
        var lastYear = ClaudeCliUsageParser.Parse("Current week (all models): 1% used · resets Dec 31, 11:00pm (UTC)\n", newYear, TimeZoneInfo.Utc);
        Program.Equal(new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero), lastYear.WeeklyWindow.ResetAtUtc,
            "a reset that just passed at New Year stays last year");
        var evening = new DateTimeOffset(2027, 2, 3, 18, 0, 0, TimeSpan.Zero);
        var morningTomorrow = ClaudeCliUsageParser.Parse("Current session: 5% used · resets 8:00am (UTC)\nCurrent week (all models): 28% used · resets Feb 10, 9:30am (UTC)\n",
            evening, TimeZoneInfo.Utc);
        Program.Equal(new DateTimeOffset(2027, 2, 4, 8, 0, 0, TimeSpan.Zero), morningTomorrow.ShortWindow?.ResetAtUtc,
            "a bare time 14 h ahead is tomorrow, not 10 h ago");
        var afterMidnight = new DateTimeOffset(2027, 2, 4, 0, 30, 0, TimeSpan.Zero);
        var justPassed = ClaudeCliUsageParser.Parse("Current session: 5% used · resets 11:45pm (UTC)\nCurrent week (all models): 28% used · resets Feb 10, 9:30am (UTC)\n",
            afterMidnight, TimeZoneInfo.Utc);
        Program.Equal(new DateTimeOffset(2027, 2, 3, 23, 45, 0, TimeSpan.Zero), justPassed.ShortWindow?.ResetAtUtc,
            "a bare time that just passed is yesterday");

        // 1:30am on Nov 1, 2026 happens twice in New York (EDT, then EST).
        const string ambiguous = "Current week (all models): 10% used · resets Nov 1, 1:30am (America/New_York)\n";
        Program.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero),
            ClaudeCliUsageParser.Parse(ambiguous, new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero), NewYork).WeeklyWindow.ResetAtUtc,
            "the earlier instant while it is still ahead");
        Program.Equal(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero),
            ClaudeCliUsageParser.Parse(ambiguous, new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.Zero), NewYork).WeeklyWindow.ResetAtUtc,
            "the later instant once the earlier has passed");
    }

    private static string EnvelopeWith(string result, string usageJson, string modelUsageJson)
    {
        var resultJson = System.Text.Json.JsonSerializer.Serialize(result, RawUnicode);
        return $$"""{"type":"result","subtype":"success","is_error":false,"num_turns":0,"total_cost_usd":0,"result":{{resultJson}},"local_command":"usage","usage":{{usageJson}},"modelUsage":{{modelUsageJson}}}""";
    }

    public static async Task ProviderSpecsAsync()
    {
        var signedIn = new FakeAuth(ClaudeAuthOutcome.Authenticated(new ClaudeAuthContext("synthetic-subject", "max")));
        static AccountScopeResolver Scope() => static (_, _, _, _) => ValueTask.FromResult("opaque-test-scope");

        var transport = new FakeCliTransport(Completed(Envelope(UsageText)));
        var diagnostics = new Program.CapturingDiagnostics();
        var provider = new ClaudeUsageProvider(signedIn, transport, Scope(), diagnostics, localZone: TimeZoneInfo.Utc);
        var ready = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Program.Equal(ProviderStatus.Ready, ready.Status, "a zero-spend usage envelope is Ready");
        Program.Equal(3, ready.Snapshot?.Buckets.Length, "three buckets");
        Program.Equal("max", ready.Snapshot?.Plan, "the plan comes from the CLI's auth status");
        Program.Equal(ClaudeCliUsageParser.SourceId, ready.Snapshot?.SourceId, "the snapshot names the CLI source");
        Program.Equal("opaque-test-scope", ready.Snapshot?.AccountScope, "opaque scope injection");
        Program.Equal<string?>(null, ready.SafeErrorCode, "no error code on Ready");
        if (!ready.Actions.Any(action => action.Kind == ProviderActionKind.Retry)) throw new InvalidOperationException("Ready keeps the Retry action.");

        // Spent turn: latched for the session; the CLI is not launched again.
        var spent = new FakeCliTransport(Completed(Envelope(UsageText, turns: 1)));
        var latchDiagnostics = new Program.CapturingDiagnostics();
        var latching = new ClaudeUsageProvider(signedIn, spent, Scope(), latchDiagnostics, localZone: TimeZoneInfo.Utc);
        var first = await latching.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Program.Equal(ProviderStatus.UnknownError, first.Status, "a spent turn is an error");
        Program.Equal("claude_cli_spent_turn", first.SafeErrorCode, "spent-turn code");
        if (!latching.IsLatched) throw new InvalidOperationException("A spent turn must latch the provider.");
        var second = await latching.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Program.Equal("claude_latched", second.SafeErrorCode, "a latched provider reports latched");
        Program.Equal(1, spent.CallCount, "a latched provider never launches the CLI again");
        if (!latchDiagnostics.Events.Any(e => e.Code == "claude_latched" && e.Status.StartsWith("spent_turn", StringComparison.Ordinal)))
            throw new InvalidOperationException("The latch must be logged as claude_latched with reason spent_turn.");

        // A timed-out run whose partial output already shows a spent turn still latches.
        var partial = new FakeCliTransport(new ClaudeCliResult(ClaudeCliOutcome.TimedOut, -1, Envelope(UsageText, turns: 2), false));
        var partialProvider = new ClaudeUsageProvider(signedIn, partial, Scope(), localZone: TimeZoneInfo.Utc);
        Program.Equal("claude_cli_spent_turn", (await partialProvider.ReadAsync(CancellationToken.None).ConfigureAwait(false)).SafeErrorCode,
            "partial output with a spent turn latches");

        // Outcomes.
        Program.Equal(ProviderStatus.Offline, await StatusOf(signedIn, new ClaudeCliResult(ClaudeCliOutcome.TimedOut, -1, null, false)).ConfigureAwait(false), "timeout is Offline");
        Program.Equal(ProviderStatus.MissingApplication, await StatusOf(signedIn, new ClaudeCliResult(ClaudeCliOutcome.ExecutableNotFound, -1, null, false)).ConfigureAwait(false), "no CLI is MissingApplication");
        Program.Equal(ProviderStatus.UnknownError, await StatusOf(signedIn, new ClaudeCliResult(ClaudeCliOutcome.LaunchFailed, -1, null, false)).ConfigureAwait(false), "launch failure is UnknownError");
        Program.Equal(ProviderStatus.SchemaChanged, await StatusOf(signedIn, new ClaudeCliResult(ClaudeCliOutcome.Completed, 0, Envelope(UsageText), true)).ConfigureAwait(false), "oversized output is SchemaChanged");
        Program.Equal(ProviderStatus.SchemaChanged, await StatusOf(signedIn, Completed("not json at all")).ConfigureAwait(false), "an exit-0 non-envelope is SchemaChanged");
        Program.Equal(ProviderStatus.UnknownError, await StatusOf(signedIn, new ClaudeCliResult(ClaudeCliOutcome.Completed, 1, "error text", false)).ConfigureAwait(false), "a nonzero exit without an envelope is UnknownError");
        Program.Equal(ProviderStatus.SchemaChanged, await StatusOf(signedIn, Completed(Envelope("Current session: 1% used · resets Feb 3, 1pm (UTC)\n"))).ConfigureAwait(false), "a result without the weekly line is SchemaChanged");

        // Auth failures never launch the CLI.
        var untouched = new FakeCliTransport(Completed(Envelope(UsageText)));
        var signedOut = new ClaudeUsageProvider(new FakeAuth(ClaudeAuthOutcome.Failed(ProviderStatus.NeedsSignIn, "claude_signed_out")), untouched, Scope());
        var needsSignIn = await signedOut.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Program.Equal(ProviderStatus.NeedsSignIn, needsSignIn.Status, "signed out passes through");
        Program.Equal(0, untouched.CallCount, "a signed-out CLI is not asked for usage");
        if (!needsSignIn.Actions.Any(action => action.Kind == ProviderActionKind.LaunchOfficialApplication && action.Target == "claude"))
            throw new InvalidOperationException("Signed out must offer launching the official CLI.");

        // Scope store failure keeps the reading with a session scope and the warning code.
        var failingScope = new ClaudeUsageProvider(signedIn, new FakeCliTransport(Completed(Envelope(UsageText))),
            static (_, _, _, _) => ValueTask.FromException<string>(new IOException()), localZone: TimeZoneInfo.Utc);
        var sessionScoped = await failingScope.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Program.Equal(ProviderStatus.Ready, sessionScoped.Status, "a scope-store failure still reports the reading");
        Program.Equal(ClaudeUsageProvider.HistoryScopeUnavailableCode, sessionScoped.SafeErrorCode, "session-scope warning code");
        if (sessionScoped.Snapshot?.AccountScope.StartsWith("session:", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("A scope-store failure must fall back to a session scope.");

        // One unproven timeout is Offline; the second in a row
        // latches; a timed-out run that still printed a complete zero-spend envelope is a usable reading.
        var timedOut = new FakeCliTransport(new ClaudeCliResult(ClaudeCliOutcome.TimedOut, -1, null, false));
        var timeoutProvider = new ClaudeUsageProvider(signedIn, timedOut, Scope(), localZone: TimeZoneInfo.Utc);
        Program.Equal("claude_cli_timeout", (await timeoutProvider.ReadAsync(CancellationToken.None).ConfigureAwait(false)).SafeErrorCode, "one unproven timeout is Offline");
        if (timeoutProvider.IsLatched) throw new InvalidOperationException("One timeout must not latch.");
        // The hang latch reports "not responding" (Offline), not a spent turn.
        var secondTimeout = await timeoutProvider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Program.Equal(ClaudeUsageProvider.UnresponsiveCode, secondTimeout.SafeErrorCode, "the second unproven timeout in a row latches as not responding");
        Program.Equal(ProviderStatus.Offline, secondTimeout.Status, "a hang latch is Offline");
        if (!timeoutProvider.IsLatched) throw new InvalidOperationException("Two unproven timeouts must latch.");
        var lingering = new FakeCliTransport(new ClaudeCliResult(ClaudeCliOutcome.TimedOut, -1, Envelope(UsageText), false));
        var lingeringProvider = new ClaudeUsageProvider(signedIn, lingering, Scope(), localZone: TimeZoneInfo.Utc);
        Program.Equal(ProviderStatus.Ready, (await lingeringProvider.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Status,
            "a run that printed its zero-spend envelope and then lingered is Ready");
        var recovering = new FakeCliTransport(_ => Task.FromResult(new ClaudeCliResult(ClaudeCliOutcome.TimedOut, -1, null, false)));
        var recoveringProvider = new ClaudeUsageProvider(signedIn, recovering, Scope(), localZone: TimeZoneInfo.Utc);
        await recoveringProvider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        var okThenTimeout = new ClaudeUsageProvider(signedIn, new FakeCliTransport(Completed(Envelope(UsageText))), Scope(), localZone: TimeZoneInfo.Utc);
        await okThenTimeout.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        if (recoveringProvider.IsLatched || okThenTimeout.IsLatched) throw new InvalidOperationException("A single timeout, or a good read, must not latch.");

        // A failed run whose text talks about logging in is the sign-in notice, not a generic failure.
        var revoked = new FakeCliTransport(new ClaudeCliResult(ClaudeCliOutcome.Completed, 1,
            """{"type":"result","subtype":"error","is_error":true,"num_turns":0,"result":"Not logged in · Please run /login"}""", false));
        var revokedResult = await new ClaudeUsageProvider(signedIn, revoked, Scope(), localZone: TimeZoneInfo.Utc).ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Program.Equal(ProviderStatus.AuthenticationExpired, revokedResult.Status, "a failed run that mentions logging in is AuthenticationExpired");
        Program.Equal("claude_cli_login_required", revokedResult.SafeErrorCode, "login-required code");
        var otherFailure = new FakeCliTransport(new ClaudeCliResult(ClaudeCliOutcome.Completed, 1, "Segmentation fault", false));
        Program.Equal(ProviderStatus.UnknownError, (await new ClaudeUsageProvider(signedIn, otherFailure, Scope(), localZone: TimeZoneInfo.Utc).ReadAsync(CancellationToken.None).ConfigureAwait(false)).Status,
            "a failed run that does not mention logging in stays UnknownError");

        // Login wording on stderr has the same effect as wording on stdout.
        const string errorEnvelope = """{"type":"result","subtype":"error","is_error":true,"num_turns":0,"result":"Error"}""";
        var stderrLogin = new FakeCliTransport(new ClaudeCliResult(ClaudeCliOutcome.Completed, 1, errorEnvelope, false) { StandardErrorMentionsLogin = true });
        var stderrLoginDiagnostics = new Program.CapturingDiagnostics();
        Program.Equal(ProviderStatus.AuthenticationExpired,
            (await new ClaudeUsageProvider(signedIn, stderrLogin, Scope(), stderrLoginDiagnostics, localZone: TimeZoneInfo.Utc).ReadAsync(CancellationToken.None).ConfigureAwait(false)).Status,
            "a failed run whose stderr mentions logging in is AuthenticationExpired");
        Program.Equal(1, stderrLoginDiagnostics.Events.Count(e => e.Code == "claude_cli_login_required" && e.Status == "stderr"), "the stderr source is logged");
        Program.Equal(ProviderStatus.UnknownError,
            await StatusOf(signedIn, new ClaudeCliResult(ClaudeCliOutcome.Completed, 1, errorEnvelope, false)).ConfigureAwait(false),
            "the same failure without login wording anywhere stays UnknownError");

        // A persisted latch for this CLI build prevents another launch.
        var remembered = new FakeCliTransport(Completed(Envelope(UsageText))) { Persisted = new ClaudeCliPersistedLatch("spent_turn", ReceivedAt) };
        var rememberedDiagnostics = new Program.CapturingDiagnostics();
        var restarted = new ClaudeUsageProvider(signedIn, remembered, Scope(), rememberedDiagnostics, localZone: TimeZoneInfo.Utc);
        Program.Equal("claude_latched", (await restarted.ReadAsync(CancellationToken.None).ConfigureAwait(false)).SafeErrorCode,
            "a persisted latch holds after a restart");
        Program.Equal(0, remembered.CallCount, "a persisted latch never launches the CLI");
        if (!restarted.IsLatched) throw new InvalidOperationException("Specification failed: a persisted latch latches the provider.");
        if (!rememberedDiagnostics.Events.Any(e => e.Code == "claude_latched" && e.Status.StartsWith("persisted,reason=spent_turn", StringComparison.Ordinal)))
            throw new InvalidOperationException("Specification failed: the persisted latch is logged with its reason.");
        Program.Equal("claude_latched", (await restarted.ReadAsync(ProviderReadIntent.Manual, CancellationToken.None).ConfigureAwait(false)).SafeErrorCode,
            "a manual Refresh never clears a spent_turn latch");
        Program.Equal(0, remembered.CallCount, "a spent_turn latch holds against Refresh");
        Program.Equal(0, remembered.ClearCalls.Count, "a spent_turn latch is never cleared");

        var spentWithKey = new FakeCliTransport(Completed(Envelope(UsageText, turns: 1)) with { VersionKey = "build-a" });
        await new ClaudeUsageProvider(signedIn, spentWithKey, Scope(), localZone: TimeZoneInfo.Utc).ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Program.Equal("build-a:spent_turn", spentWithKey.PersistCalls.SingleOrDefault(), "a spent turn is recorded against the build that ran");
        var slowWithKey = new FakeCliTransport(new ClaudeCliResult(ClaudeCliOutcome.TimedOut, -1, null, false) { VersionKey = "build-b" });
        var slowProvider = new ClaudeUsageProvider(signedIn, slowWithKey, Scope(), localZone: TimeZoneInfo.Utc);
        await slowProvider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        await slowProvider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Program.Equal("build-b:unproven_timeout", slowWithKey.PersistCalls.SingleOrDefault(), "two unproven timeouts are recorded as unproven_timeout");

        // A manual Refresh clears an unproven_timeout latch (in memory and on disk) and runs again.
        var clearDiagnostics = new Program.CapturingDiagnostics();
        var recovered = new FakeCliTransport(Completed(Envelope(UsageText)) with { VersionKey = "build-b" });
        var falsePositive = new ClaudeUsageProvider(signedIn, recovered, Scope(), clearDiagnostics, localZone: TimeZoneInfo.Utc);
        recovered.Persisted = new ClaudeCliPersistedLatch("unproven_timeout", ReceivedAt);
        Program.Equal(ClaudeUsageProvider.UnresponsiveCode, (await falsePositive.ReadAsync(CancellationToken.None).ConfigureAwait(false)).SafeErrorCode,
            "a scheduled poll honors a persisted unproven_timeout latch, reported as not responding");
        // Automatic retries preserve the persisted latch; only a manual Refresh clears it.
        Program.Equal(ClaudeUsageProvider.UnresponsiveCode,
            (await falsePositive.ReadAsync(ProviderReadIntent.AutomaticRetry, CancellationToken.None).ConfigureAwait(false)).SafeErrorCode,
            "an automatic retry keeps a latch saved by an earlier launch");
        Program.Equal(0, recovered.CallCount, "the kept latch does not launch the CLI");
        Program.Equal(0, recovered.ClearCalls.Count, "the saved latch is not removed by an automatic retry");
        Program.Equal(1, clearDiagnostics.Events.Count(e => e.Code == "claude_latch_kept"), "keeping it is logged");
        var afterRefresh = await falsePositive.ReadAsync(ProviderReadIntent.Manual, CancellationToken.None).ConfigureAwait(false);
        Program.Equal(ProviderStatus.Ready, afterRefresh.Status, "a manual Refresh clears an unproven_timeout latch and reads");
        Program.Equal("manual_refresh", recovered.ClearCalls.SingleOrDefault(), "the recorded latch is removed");
        if (falsePositive.IsLatched) throw new InvalidOperationException("Specification failed: the cleared latch must not stay set.");
        Program.Equal(1, clearDiagnostics.Events.Count(e => e.Code == "claude_latch_cleared_by_refresh"), "the clearing is logged");

        // A hang latch this launch tripped is cleared by the scheduler's automatic retry, as Refresh
        // would, and the run that follows is judged as any other.
        var hungThenFine = new FakeCliTransport(new ClaudeCliResult(ClaudeCliOutcome.TimedOut, -1, null, false) { VersionKey = "build-c" });
        var thisLaunch = new ClaudeUsageProvider(signedIn, hungThenFine, Scope(), localZone: TimeZoneInfo.Utc);
        await thisLaunch.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        await thisLaunch.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        if (!thisLaunch.IsLatched) throw new InvalidOperationException("Specification failed: two hangs latch this launch.");
        var hangRuns = hungThenFine.CallCount;
        var retried = await thisLaunch.ReadAsync(ProviderReadIntent.AutomaticRetry, CancellationToken.None).ConfigureAwait(false);
        Program.Equal(hangRuns + 1, hungThenFine.CallCount, "an automatic retry clears a latch this launch tripped and runs the CLI once");
        Program.Equal("claude_cli_timeout", retried.SafeErrorCode, "the retried run is judged as any other (one hang is a timeout)");

        // Cancellation propagates.
        using var cancellation = new CancellationTokenSource();
        var waiting = new ClaudeUsageProvider(signedIn, new FakeCliTransport(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            throw new InvalidOperationException("Unreachable after cancellation.");
        }), Scope());
        var pending = waiting.ReadAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        await Program.ThrowsAsync<OperationCanceledException>(() => pending, "Claude cancellation propagates").ConfigureAwait(false);
    }

    /// <summary>The real transport against this spec binary posing as claude.exe (Program.RunFakeClaudeChild).</summary>
    public static async Task TransportProcessSpecsAsync(string tempRoot)
    {
        var fixtureDirectory = Path.Combine(tempRoot, "fake-claude-fixture");
        Program.CopyDirectoryRecursive(AppContext.BaseDirectory, fixtureDirectory);
        File.Copy(Path.Combine(fixtureDirectory, "Glideslope.Providers.Specs.exe"), Path.Combine(fixtureDirectory, "claude.exe"), overwrite: true);
        var expectedArguments = new[]
            { "-p", "--no-session-persistence", "--strict-mcp-config", "--setting-sources", "project,local", "--output-format", "json", "/usage" };
        var stdoutFile = Path.Combine(tempRoot, "fake-claude-stdout.json");
        File.WriteAllText(stdoutFile, Envelope(UsageText));

        var emptyDirectory = Path.Combine(tempRoot, "fake-claude-missing");
        Directory.CreateDirectory(emptyDirectory);
        var missing = new ClaudeCliUsageTransport(emptyDirectory, null, null, true, null, TimeSpan.FromSeconds(5));
        Program.Equal(ClaudeCliOutcome.ExecutableNotFound, (await missing.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Outcome,
            "a missing Claude executable is reported without launching anything");

        var argsMarker = Path.Combine(tempRoot, "fake-claude-args.txt");
        var cwdMarker = Path.Combine(tempRoot, "fake-claude-cwd.txt");
        await RunFakeClaudeScenarioAsync(argsMarker, cwdMarker, stdoutFile, 0, false, async () =>
        {
            var transport = new ClaudeCliUsageTransport(fixtureDirectory, null, null, true, null, TimeSpan.FromSeconds(20));
            var result = await transport.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(ClaudeCliOutcome.Completed, result.Outcome, "a clean exit completes");
            Program.Equal(0, result.ExitCode, "exit code is reported");
            Program.Equal(Envelope(UsageText), result.StandardOutput?.Trim(), "stdout is captured whole");
            if (result.StandardOutputTooLarge) throw new InvalidOperationException("A small envelope must not be flagged too large.");
            // The middle dot must survive UTF-8 decoding on Windows.
            if (result.StandardOutput?.Contains('·') != true)
                throw new InvalidOperationException("The transport must read the CLI's UTF-8 output (the middle dot did not survive).");
        }).ConfigureAwait(false);
        var capturedArguments = File.ReadAllText(argsMarker).Split('\u001f');
        if (!capturedArguments.SequenceEqual(expectedArguments, StringComparer.Ordinal))
            throw new InvalidOperationException($"Specification failed: exact usage argument list (got {string.Join(' ', capturedArguments)}).");
        Program.Equal("1", File.ReadAllText(argsMarker + ".autoupdate"), "the usage read runs with the CLI's auto-updater off (2.3.0)");
        var capturedWorkingDirectory = File.ReadAllText(cwdMarker);
        if (!capturedWorkingDirectory.StartsWith(Path.Combine(Path.GetTempPath(), "Glideslope-ClaudeUsage-"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Specification failed: usage working directory naming.");
        if (Directory.Exists(capturedWorkingDirectory))
            throw new InvalidOperationException("Specification failed: usage working directory was not removed afterward.");

        await RunFakeClaudeScenarioAsync(null, null, stdoutFile, 7, false, async () =>
        {
            var transport = new ClaudeCliUsageTransport(fixtureDirectory, null, null, true, null, TimeSpan.FromSeconds(20));
            var result = await transport.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(ClaudeCliOutcome.Completed, result.Outcome, "a nonzero exit still completes");
            Program.Equal(7, result.ExitCode, "the nonzero exit code is reported");
        }).ConfigureAwait(false);

        var hangCwdMarker = Path.Combine(tempRoot, "fake-claude-hang-cwd.txt");
        await RunFakeClaudeScenarioAsync(null, hangCwdMarker, stdoutFile, 0, true, async () =>
        {
            // 5 s: the freshly copied apphost must start and print before the kill (Defender scans it on
            // first launch); the child then sleeps forever, so the timeout is what ends the run.
            var transport = new ClaudeCliUsageTransport(fixtureDirectory, null, null, true, null, TimeSpan.FromSeconds(5));
            var stopwatch = Stopwatch.StartNew();
            var result = await transport.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            stopwatch.Stop();
            Program.Equal(ClaudeCliOutcome.TimedOut, result.Outcome, "a wedged child is reported as timed out");
            Program.Equal(Envelope(UsageText), result.StandardOutput?.Trim(), "what the child printed before the kill is kept");
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(20))
                throw new InvalidOperationException("Specification failed: the timed-out run did not honor the injected short timeout.");
        }).ConfigureAwait(false);
        if (Directory.Exists(File.ReadAllText(hangCwdMarker)))
            throw new InvalidOperationException("Specification failed: killed usage working directory was not removed afterward.");

        // every run uses the one stable directory, emptied before the CLI starts.
        var stableDirectory = Path.Combine(tempRoot, "claude-stable", "cli-workdir", "claude");
        Directory.CreateDirectory(Path.Combine(stableDirectory, ".claude"));
        File.WriteAllText(Path.Combine(stableDirectory, ".claude", "settings.json"), "{}");
        File.WriteAllText(Path.Combine(stableDirectory, "CLAUDE.md"), "stale fixture");
        var stableDiagnostics = new Program.CapturingDiagnostics();
        for (var run = 1; run <= 2; run++)
        {
            var stableCwd = Path.Combine(tempRoot, $"claude-stable-cwd-{run}.txt");
            var stableEntries = Path.Combine(tempRoot, $"claude-stable-entries-{run}.txt");
            await RunFakeClaudeScenarioAsync(null, stableCwd, stdoutFile, 0, false, async () =>
            {
                var transport = new ClaudeCliUsageTransport(fixtureDirectory, null, null, true, stableDiagnostics, TimeSpan.FromSeconds(20),
                    stableDirectory, null, null);
                Program.Equal(ClaudeCliOutcome.Completed, (await transport.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Outcome,
                    "a run in the stable directory completes");
            }, entriesMarker: stableEntries).ConfigureAwait(false);
            Program.Equal(Path.GetFullPath(stableDirectory), Path.GetFullPath(File.ReadAllText(stableCwd)), $"run {run} uses the one stable directory");
            Program.Equal("0", File.ReadAllText(stableEntries), $"run {run} starts in an empty directory");
        }
        Program.Equal(true, Directory.Exists(stableDirectory), "the stable directory is kept between runs");

        // stderr login wording reaches the provider as a boolean.
        var loginFile = Path.Combine(tempRoot, "fake-claude-login-error.json");
        File.WriteAllText(loginFile, """{"type":"result","subtype":"error","is_error":true,"num_turns":0,"result":"Error"}""");
        await RunFakeClaudeScenarioAsync(null, null, loginFile, 1, false, async () =>
        {
            var transport = new ClaudeCliUsageTransport(fixtureDirectory, null, null, true, null, TimeSpan.FromSeconds(20));
            var result = await transport.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal(true, result.StandardErrorMentionsLogin, "stderr asking to log in is reported");
        }, stderrText: "OAuth token expired. Please run /login").ConfigureAwait(false);
        await RunFakeClaudeScenarioAsync(null, null, loginFile, 1, false, async () =>
        {
            var transport = new ClaudeCliUsageTransport(fixtureDirectory, null, null, true, null, TimeSpan.FromSeconds(20));
            Program.Equal(false, (await transport.ReadAsync(CancellationToken.None).ConfigureAwait(false)).StandardErrorMentionsLogin,
                "ordinary stderr is not login wording");
        }).ConfigureAwait(false);

        // A helper that inherits the CLI's pipes must not delay completion after the CLI exits.
        var holderMarker = Path.Combine(tempRoot, "fake-claude-holder-pid.txt");
        var heldDiagnostics = new Program.CapturingDiagnostics();
        try
        {
            await RunFakeClaudeScenarioAsync(null, null, stdoutFile, 0, false, async () =>
            {
                var transport = new ClaudeCliUsageTransport(fixtureDirectory, null, null, true, heldDiagnostics, TimeSpan.FromSeconds(30));
                var stopwatch = Stopwatch.StartNew();
                var result = await transport.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                stopwatch.Stop();
                Program.Equal(ClaudeCliOutcome.Completed, result.Outcome, "a CLI whose helper still holds its pipes completes when the CLI exits");
                Program.Equal(Envelope(UsageText), result.StandardOutput?.Trim(), "what the CLI printed before exiting is kept");
                if (stopwatch.Elapsed > TimeSpan.FromSeconds(20))
                    throw new InvalidOperationException("Specification failed: the pipe drain after exit was not bounded.");
            }, holderMarker: holderMarker).ConfigureAwait(false);
            Program.Equal(1, heldDiagnostics.Events.Count(e => e.Code == "claude_cli_pipe_held_after_exit"), "the held pipe is logged");
        }
        finally
        {
            Program.StopPipeHolder(holderMarker, "claude.exe");
        }

        // an npm install's claude.cmd runs as node with the package's entry script, and the
        // fixed arguments arrive unchanged after it (no cmd.exe to re-parse "project,local" or "/usage").
        var npmPrefix = Path.Combine(tempRoot, "fake-npm-prefix");
        Program.CopyDirectoryRecursive(AppContext.BaseDirectory, npmPrefix);
        File.Copy(Path.Combine(npmPrefix, "Glideslope.Providers.Specs.exe"), Path.Combine(npmPrefix, "node.exe"), overwrite: true);
        File.WriteAllText(Path.Combine(npmPrefix, "claude.cmd"), "metadata-only fixture: never executed");
        var package = Path.Combine(npmPrefix, "node_modules", "@anthropic-ai", "claude-code");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "package.json"), """{"name":"@anthropic-ai/claude-code","version":"9.9.9","bin":{"claude":"cli.js"}}""");
        File.WriteAllText(Path.Combine(package, "cli.js"), "// metadata-only fixture: the fake node never reads it\n");
        var npmArgs = Path.Combine(tempRoot, "fake-npm-args.txt");
        await RunFakeClaudeScenarioAsync(npmArgs, null, stdoutFile, 0, false, async () =>
        {
            var transport = new ClaudeCliUsageTransport(npmPrefix, null, null, true, null, TimeSpan.FromSeconds(20));
            Program.Equal(ClaudeCliOutcome.Completed, (await transport.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Outcome,
                "an npm install runs through node");
        }).ConfigureAwait(false);
        var npmExpected = new[] { Path.GetFullPath(Path.Combine(package, "cli.js")) }.Concat(expectedArguments).ToArray();
        if (!File.ReadAllText(npmArgs).Split('\u001f').SequenceEqual(npmExpected, StringComparer.Ordinal))
            throw new InvalidOperationException("Specification failed: node must get the entry script, then the exact usage arguments.");

        // the latch file survives a restart for the same CLI build and is cleared by an update.
        var latchFile = Path.Combine(tempRoot, "claude-latch", "claude-cli-latch.json");
        var latchDiagnostics = new Program.CapturingDiagnostics();
        var beforeRestart = new ClaudeCliUsageTransport(fixtureDirectory, null, null, true, latchDiagnostics, TimeSpan.FromSeconds(20), null, latchFile, null);
        string? builtKey = null;
        await RunFakeClaudeScenarioAsync(null, null, stdoutFile, 0, false, async () =>
            builtKey = (await beforeRestart.ReadAsync(CancellationToken.None).ConfigureAwait(false)).VersionKey).ConfigureAwait(false);
        if (string.IsNullOrEmpty(builtKey)) throw new InvalidOperationException("Specification failed: a run reports its CLI build.");
        Program.Equal<ClaudeCliPersistedLatch?>(null, beforeRestart.ReadPersistedLatch(), "no latch before one is recorded");
        beforeRestart.PersistLatch(builtKey, "spent_turn");
        var afterRestart = new ClaudeCliUsageTransport(fixtureDirectory, null, null, true, latchDiagnostics, TimeSpan.FromSeconds(20), null, latchFile, null);
        Program.Equal("spent_turn", afterRestart.ReadPersistedLatch()?.Reason, "the latch survives a restart for the same CLI build");
        File.WriteAllText(latchFile, "not json");
        Program.Equal<ClaudeCliPersistedLatch?>(null, afterRestart.ReadPersistedLatch(), "an unreadable latch file is no latch");
        Program.Equal(1, latchDiagnostics.Events.Count(e => e.Code == "claude_latch_file_unreadable"), "the unreadable file is logged");
        afterRestart.PersistLatch(builtKey, "unproven_timeout");
        afterRestart.ClearPersistedLatch("manual_refresh");
        Program.Equal(false, File.Exists(latchFile), "clearing removes the latch file");
        afterRestart.PersistLatch(builtKey, "spent_turn");
        File.SetLastWriteTimeUtc(Path.Combine(fixtureDirectory, "claude.exe"), DateTime.UtcNow.AddMinutes(5));   // a CLI update
        Program.Equal<ClaudeCliPersistedLatch?>(null, afterRestart.ReadPersistedLatch(), "a different CLI build clears the latch");
        Program.Equal(false, File.Exists(latchFile), "the cleared latch file is removed");
        Program.Equal(1, latchDiagnostics.Events.Count(e => e.Code == "claude_latch_cleared" && e.Status.StartsWith("version_changed", StringComparison.Ordinal)),
            "the version change is logged");

        // When the CLI build is unidentified, preserve the recorded latch because a build change cannot be proven.
        var unknownBuildFile = Path.Combine(tempRoot, "claude-latch-unknown-build", "claude-cli-latch.json");
        var unknownBuildStore = new ClaudeCliLatchStore(unknownBuildFile, latchDiagnostics);
        unknownBuildStore.Write("recorded-build-key", "spent_turn");
        Program.Equal("spent_turn", unknownBuildStore.Read(null)?.Reason, "an unidentified build honors the recorded latch");
        Program.Equal(true, File.Exists(unknownBuildFile), "an unidentified build never deletes the latch file");
        Program.Equal(1, latchDiagnostics.Events.Count(e => e.Code == "claude_latch_honored" && e.Status == "version_key_unavailable"),
            "honoring a latch for an unidentified build is logged");

        // Auth status tolerates a banner line and runs from the stable directory.
        var authFile = Path.Combine(tempRoot, "fake-claude-auth.txt");
        File.WriteAllText(authFile, "Update available! Run: claude update\n" +
            """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":"synthetic@example.invalid","orgId":"synthetic-org","subscriptionType":"max"}""");
        var authArgs = Path.Combine(tempRoot, "fake-claude-auth-args.txt");
        var authCwd = Path.Combine(tempRoot, "fake-claude-auth-cwd.txt");
        await RunFakeClaudeScenarioAsync(authArgs, authCwd, authFile, 0, false, async () =>
        {
            var reader = new ClaudeAuthContextReader(null, null, fixtureDirectory, true, null, null, stableDirectory);
            var outcome = await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Program.Equal<ProviderStatus?>(null, outcome.FailureStatus, "a banner line before the auth JSON is tolerated");
            Program.Equal("max", outcome.Context?.Plan, "the plan is read");
        }).ConfigureAwait(false);
        Program.Equal("auth\u001fstatus\u001f--json", File.ReadAllText(authArgs), "exact auth-status arguments");
        Program.Equal("1", File.ReadAllText(authArgs + ".autoupdate"), "auth status runs with the CLI's auto-updater off (2.3.0)");
        Program.Equal(Path.GetFullPath(stableDirectory), Path.GetFullPath(File.ReadAllText(authCwd)), "auth status runs from the stable directory");
    }

    /// <summary>Read-only live probe: runs the production Claude provider once against the real CLI and
    /// prints only shapes and numbers, never the account email.</summary>
    public static async Task<int> RunLiveClaudeAsync(string tempRoot)
    {
        var scopes = new AccountScopeKeyStore(Path.Combine(tempRoot, "live-data"));
        var diagnostics = new Program.CapturingDiagnostics();
        // Production constructors use stable CLI working-directory and latch paths under
        // %LOCALAPPDATA%\Glideslope. This probe keeps production discovery settings but owns both paths, avoiding
        // interference with the running app's CLI or card state.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var configDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var path = Environment.GetEnvironmentVariable("PATH");
        var workingDirectory = Path.Combine(tempRoot, "live-cli-workdir", "claude");
        var auth = new ClaudeAuthContextReader(home, configDirectory, path, OperatingSystem.IsWindows(), roaming, diagnostics, workingDirectory);
        var transport = new ClaudeCliUsageTransport(path, home, configDirectory, OperatingSystem.IsWindows(), diagnostics,
            ClaudeCliUsageTransport.DefaultTimeout, workingDirectory,
            Path.Combine(tempRoot, "live-provider-state", "claude-cli-latch.json"), roaming);
        var provider = new ClaudeUsageProvider(auth, transport, scopes.ResolveAsync, diagnostics);
        var result = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"Claude live-read status: {result.Status} ({result.SafeErrorCode ?? "ok"})");
        foreach (var diagnostic in diagnostics.Events)
            Console.WriteLine($"  {diagnostic.Code}: {diagnostic.Status} ({diagnostic.DurationMilliseconds} ms)");
        if (result.Snapshot is { } snapshot)
        {
            Console.WriteLine($"Plan: {snapshot.Plan ?? "(none)"}; source: {snapshot.SourceId}");
            foreach (var bucket in snapshot.Buckets)
                Console.WriteLine($"Bucket {bucket.Id} ({bucket.Role}): remaining {bucket.RemainingFraction:P0}; resets {bucket.ResetAtUtc:u}; duration {bucket.Duration}");
        }
        return result.Status == ProviderStatus.Ready ? 0 : 1;
    }

    private static async Task<ProviderStatus> StatusOf(FakeAuth auth, ClaudeCliResult result)
    {
        var provider = new ClaudeUsageProvider(auth, new FakeCliTransport(result), static (_, _, _, _) => ValueTask.FromResult("opaque-test-scope"),
            localZone: TimeZoneInfo.Utc);
        return (await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Status;
    }

    private static ClaudeCliResult Completed(string stdout) => new(ClaudeCliOutcome.Completed, 0, stdout, false);

    private static async Task RunFakeClaudeScenarioAsync(string? argsMarker, string? cwdMarker, string stdoutFile, int exitCode, bool hang, Func<Task> action,
        string? entriesMarker = null, string? stderrText = null, string? holderMarker = null)
    {
        var names = new[] { "GLIDESLOPE_FAKE_CLAUDE_CHILD", "GLIDESLOPE_FAKE_CLAUDE_ARGS_MARKER", "GLIDESLOPE_FAKE_CLAUDE_CWD_MARKER",
            "GLIDESLOPE_FAKE_CLAUDE_STDOUT_FILE", "GLIDESLOPE_FAKE_CLAUDE_EXIT_CODE", "GLIDESLOPE_FAKE_CLAUDE_HANG",
            "GLIDESLOPE_FAKE_CLAUDE_CWD_ENTRIES_MARKER", "GLIDESLOPE_FAKE_CLAUDE_STDERR_TEXT", "GLIDESLOPE_FAKE_CLAUDE_HOLDER_PID_MARKER",
            "DISABLE_AUTOUPDATER" };
        var previous = names.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            // A Claude Code shell may already set DISABLE_AUTOUPDATER=1. "0" here means only the
            // transport's own override can produce the "1" the specs assert.
            Environment.SetEnvironmentVariable(names[9], "0");
            Environment.SetEnvironmentVariable(names[0], "1");
            Environment.SetEnvironmentVariable(names[1], argsMarker);
            Environment.SetEnvironmentVariable(names[2], cwdMarker);
            Environment.SetEnvironmentVariable(names[3], stdoutFile);
            Environment.SetEnvironmentVariable(names[4], exitCode.ToString());
            Environment.SetEnvironmentVariable(names[5], hang ? "1" : null);
            Environment.SetEnvironmentVariable(names[6], entriesMarker);
            Environment.SetEnvironmentVariable(names[7], stderrText);
            Environment.SetEnvironmentVariable(names[8], holderMarker);
            await action().ConfigureAwait(false);
        }
        finally
        {
            for (var index = 0; index < names.Length; index++)
                Environment.SetEnvironmentVariable(names[index], previous[index]);
        }
    }

    private static TimeZoneInfo FindZone(string iana, string windows)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(iana); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById(windows); }
    }

    private sealed class FakeAuth(ClaudeAuthOutcome outcome) : IClaudeAuthContextReader
    {
        public ValueTask<ClaudeAuthOutcome> ReadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(outcome);
    }

    private sealed class FakeCliTransport : IClaudeUsageTransport
    {
        private readonly Func<CancellationToken, Task<ClaudeCliResult>> _read;
        public int CallCount { get; private set; }
        /// <summary>Models a persisted latch for the current build.</summary>
        public ClaudeCliPersistedLatch? Persisted { get; set; }
        public List<string> PersistCalls { get; } = [];
        public List<string> ClearCalls { get; } = [];
        public FakeCliTransport(ClaudeCliResult result) : this(_ => Task.FromResult(result)) { }
        public FakeCliTransport(Func<CancellationToken, Task<ClaudeCliResult>> read) => _read = read;

        public async ValueTask<ClaudeCliResult> ReadAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return await _read(cancellationToken).ConfigureAwait(false);
        }

        public ClaudeCliPersistedLatch? ReadPersistedLatch() => Persisted;

        public void PersistLatch(string versionKey, string reason)
        {
            PersistCalls.Add($"{versionKey}:{reason}");
            Persisted = new ClaudeCliPersistedLatch(reason, ReceivedAt);
        }

        public void ClearPersistedLatch(string reason)
        {
            ClearCalls.Add(reason);
            Persisted = null;
        }
    }
}
