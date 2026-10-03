using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Glideslope.Domain;

namespace Glideslope.Providers;

/// <summary>What the JSON envelope of `claude -p --output-format json /usage` proves about spend.</summary>
public enum ClaudeCliEnvelopeVerdict
{
    /// <summary>A completed local `usage` command with zero turns and zero cost; the result text is usable.</summary>
    ZeroSpend,
    /// <summary>The run spent a model turn or money. The provider latches off for the session.</summary>
    SpentTurn,
    /// <summary>Not a usage result (not JSON, an error result, no result text, unknown command).</summary>
    NotUsage
}

public sealed record ClaudeCliEnvelope(ClaudeCliEnvelopeVerdict Verdict, string? ResultText, string Reason);

public sealed record ParsedClaudeCliUsage(
    QuotaBucket? ShortWindow,
    QuotaBucket WeeklyWindow,
    QuotaBucket? FeaturedWeeklyWindow,
    string? UnknownZone);

/// <summary>
/// Claude CLI source design §2 rules 2–4. Reads the JSON envelope the CLI prints in JSON output mode and then the
/// plain usage lines inside its result text:
/// <code>
/// Current session: 37.5% used · resets Feb 3, 1:15pm (America/New_York)
/// Current week (all models): 28% used · resets Feb 10, 9:30am (America/New_York)
/// Current week (Fable): 12.5% used · resets Feb 9, 4:45pm (America/New_York)
/// </code>
/// Each line is matched loosely: label, percentage, "resets", a month/day/time, and a zone in
/// parentheses. Everything else in the text (the "What's contributing" section) is ignored.
/// </summary>
public static partial class ClaudeCliUsageParser
{
    public const string SourceId = "anthropic.claude-cli.usage";
    public const string ShortBucketId = "claude.short.five_hour";
    public const string WeeklyBucketId = "claude.weekly.seven_day";
    private const string FeaturedBucketPrefix = "claude.featured.weekly.";
    private static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);
    private static readonly TimeSpan SevenDays = TimeSpan.FromDays(7);
    private static readonly TimeSpan YearRuleSlack = TimeSpan.FromHours(12);
    private static readonly string[] ResetFormats =
    [
        "MMM d, h:mmtt", "MMM d, htt", "MMMM d, h:mmtt", "MMMM d, htt", "MMM d h:mmtt", "MMM d, H:mm", "MMMM d, H:mm",
        "d MMM, h:mmtt", "d MMM, htt", "d MMMM, h:mmtt", "d MMM h:mmtt", "d MMM, H:mm", "d MMMM, H:mm"
    ];

    /// <summary>Design §2 rule 2. ZeroSpend only when the envelope is a `result` for the local `usage`
    /// command with `num_turns` 0 and `total_cost_usd` 0 (a missing cost counts as 0; missing turns does
    /// not). SpentTurn whenever turns or cost are above zero, whatever else the envelope says.</summary>
    public static ClaudeCliEnvelope ReadEnvelope(string? standardOutput)
    {
        if (string.IsNullOrWhiteSpace(standardOutput))
            return new ClaudeCliEnvelope(ClaudeCliEnvelopeVerdict.NotUsage, null, "empty_output");
        var document = TryParseDocument(standardOutput);
        if (document is null)
        {
            // A run killed mid-print may not contain a complete JSON document. Detect spend in the raw text
            // before rejecting it as an incomplete envelope.
            if (SpentTurnInRawText().IsMatch(standardOutput))
                return new ClaudeCliEnvelope(ClaudeCliEnvelopeVerdict.SpentTurn, null, "raw_text_spend");
            return new ClaudeCliEnvelope(ClaudeCliEnvelopeVerdict.NotUsage, null, "not_json");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new ClaudeCliEnvelope(ClaudeCliEnvelopeVerdict.NotUsage, null, "not_object");

            var turns = TryNumber(root, "num_turns");
            var cost = TryNumber(root, "total_cost_usd");
            if (turns is > 0 || cost is > 0)
                return new ClaudeCliEnvelope(ClaudeCliEnvelopeVerdict.SpentTurn, null,
                    $"turns={turns?.ToString(CultureInfo.InvariantCulture) ?? "?"},cost={cost?.ToString(CultureInfo.InvariantCulture) ?? "?"}");

            // Token counts and per-model usage also indicate a model call. A non-empty modelUsage object with
            // no positive values does not prove zero spend and is rejected as NotUsage below.
            var inputTokens = root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                ? TryNumber(usage, "input_tokens")
                : null;
            var outputTokens = usage.ValueKind == JsonValueKind.Object ? TryNumber(usage, "output_tokens") : null;
            if (inputTokens is > 0 || outputTokens is > 0)
                return new ClaudeCliEnvelope(ClaudeCliEnvelopeVerdict.SpentTurn, null,
                    $"tokens_in={inputTokens?.ToString(CultureInfo.InvariantCulture) ?? "?"},tokens_out={outputTokens?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
            var modelUsagePresent = false;
            if (root.TryGetProperty("modelUsage", out var modelUsage) && modelUsage.ValueKind != JsonValueKind.Null)
            {
                if (ContainsPositiveNumber(modelUsage))
                    return new ClaudeCliEnvelope(ClaudeCliEnvelopeVerdict.SpentTurn, null, "model_usage_spent");
                modelUsagePresent = modelUsage.ValueKind != JsonValueKind.Object || modelUsage.EnumerateObject().Any();
            }

            if (TryBoolean(root, "is_error") == true)
                return new ClaudeCliEnvelope(ClaudeCliEnvelopeVerdict.NotUsage, null, "is_error");
            if (!string.Equals(TryString(root, "type"), "result", StringComparison.Ordinal))
                return new ClaudeCliEnvelope(ClaudeCliEnvelopeVerdict.NotUsage, null, "type_not_result");
            if (!string.Equals(TryString(root, "local_command"), "usage", StringComparison.OrdinalIgnoreCase))
                return new ClaudeCliEnvelope(ClaudeCliEnvelopeVerdict.NotUsage, null, "local_command_not_usage");
            if (turns is null)
                return new ClaudeCliEnvelope(ClaudeCliEnvelopeVerdict.NotUsage, null, "num_turns_missing");
            var result = TryString(root, "result");
            if (string.IsNullOrWhiteSpace(result))
                return new ClaudeCliEnvelope(ClaudeCliEnvelopeVerdict.NotUsage, null, "result_missing");
            if (modelUsagePresent)
                return new ClaudeCliEnvelope(ClaudeCliEnvelopeVerdict.NotUsage, null, "model_usage_present");
            return new ClaudeCliEnvelope(ClaudeCliEnvelopeVerdict.ZeroSpend, result, "zero_spend");
        }
    }

    /// <summary>True when any number anywhere inside <paramref name="element"/> is above zero.</summary>
    private static bool ContainsPositiveNumber(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.TryGetDouble(out var value) && value > 0,
        JsonValueKind.Object => element.EnumerateObject().Any(property => ContainsPositiveNumber(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(ContainsPositiveNumber),
        _ => false
    };

    // Nonzero input or output token counts in a truncated envelope also indicate spend.
    [GeneratedRegex(@"""num_turns""\s*:\s*[1-9]|""total_cost_usd""\s*:\s*(?:0*\.0*[1-9]|[1-9])|""(?:input|output)_tokens""\s*:\s*[1-9]", RegexOptions.CultureInvariant)]
    private static partial Regex SpentTurnInRawText();

    [GeneratedRegex(
        // Between "used" and "resets" any non-alphanumeric run is accepted (a middle dot, a dash, or the
        // two junk characters a mis-decoded UTF-8 dot turns into).
        @"^\s*(?<label>Current\s+session|Current\s+week(?:\s*\((?<scope>[^)]*)\))?)\s*:\s*(?<pct>\d{1,3}(?:[.,]\d+)?)\s*%\s*used\b(?:[^A-Za-z0-9\r\n]*resets\s+(?<when>[^(]+?))?\s*(?:\((?<zone>[^)]+)\))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsageLine();

    /// <summary>Throws <see cref="FormatException"/> only
    /// when the weekly line is missing or its reset is missing or unreadable. A session or featured line
    /// without a readable reset is dropped so the weekly numbers still show. Among several
    /// featured lines the Fable one wins, otherwise the first.</summary>
    public static ParsedClaudeCliUsage Parse(string resultText, DateTimeOffset receivedAtUtc, TimeZoneInfo localZone)
    {
        ArgumentNullException.ThrowIfNull(resultText);
        ArgumentNullException.ThrowIfNull(localZone);
        QuotaBucket? shortWindow = null, weekly = null;
        var featuredCandidates = new List<(string Slug, QuotaBucket Bucket)>();
        string? unknownZone = null;

        foreach (var rawLine in resultText.Split('\n'))
        {
            var match = UsageLine().Match(rawLine.TrimEnd('\r'));
            if (!match.Success) continue;
            var label = match.Groups["label"].Value;
            var scope = match.Groups["scope"].Success ? match.Groups["scope"].Value.Trim() : null;
            var isSession = label.StartsWith("Current session", StringComparison.OrdinalIgnoreCase);
            var isWeekly = !isSession && (scope is null || scope.Equals("all models", StringComparison.OrdinalIgnoreCase));
            var percentText = match.Groups["pct"].Value.Replace(',', '.');
            var percent = Math.Clamp(double.Parse(percentText, CultureInfo.InvariantCulture), 0, 100);
            var remaining = Math.Round((100 - percent) / 100d, 4);   // exact to four decimals for integer percentages

            DateTimeOffset? reset = null;
            if (match.Groups["when"].Success)
            {
                try
                {
                    reset = ParseReset(match.Groups["when"].Value.Trim(),
                        match.Groups["zone"].Success ? match.Groups["zone"].Value.Trim() : null,
                        receivedAtUtc, localZone, out var zoneIssue);
                    unknownZone ??= zoneIssue;
                }
                catch (FormatException) when (!isWeekly)
                {
                    reset = null;
                }
            }
            if (reset is null)
            {
                if (isWeekly) throw new FormatException($"Usage line '{label}' has no readable reset time.");
                continue;
            }

            if (isSession)
            {
                shortWindow ??= new QuotaBucket(ShortBucketId, QuotaBucketRole.Short, remaining, FiveHours, reset, $"{SourceId}/five_hour");
            }
            else if (isWeekly)
            {
                weekly ??= new QuotaBucket(WeeklyBucketId, QuotaBucketRole.Weekly, remaining, SevenDays, reset, $"{SourceId}/seven_day");
            }
            else
            {
                var slug = Slug(scope!);
                if (slug.Length == 0) continue;
                featuredCandidates.Add((slug, new QuotaBucket($"{FeaturedBucketPrefix}{slug}.{slug}", QuotaBucketRole.FeaturedWeekly, remaining,
                    SevenDays, reset, $"{SourceId}/weekly_scoped/{slug}")));
            }
        }

        if (weekly is null)
            throw new FormatException("The usage text has no 'Current week' line.");
        var featured = featuredCandidates.FirstOrDefault(candidate => candidate.Slug == "fable").Bucket
                       ?? featuredCandidates.FirstOrDefault().Bucket;
        return new ParsedClaudeCliUsage(shortWindow, weekly, featured, unknownZone);
    }

    /// <summary>Design §2 rule 4: "Feb 3, 1:15pm" in the named zone. The year is the one that puts the
    /// reset at or after receivedAt − 12 h. An unknown or missing zone uses the local zone and reports it.
    /// Choose the year among the previous, current, and next year, using the earliest candidate no more than
    /// 12 hours before the read. Bare times use yesterday, today, or tomorrow and allow at most one hour of
    /// past slack. During a repeated daylight-saving hour, choose the earlier instant if it is not before the
    /// read; otherwise choose the later instant.</summary>
    internal static DateTimeOffset ParseReset(string when, string? zoneId, DateTimeOffset receivedAtUtc, TimeZoneInfo localZone,
        out string? unknownZone)
    {
        unknownZone = null;
        var zone = localZone;
        if (!string.IsNullOrWhiteSpace(zoneId))
        {
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
            }
            catch (TimeZoneNotFoundException)
            {
                unknownZone = zoneId;
            }
            catch (InvalidTimeZoneException)
            {
                unknownZone = zoneId;
            }
        }

        // "Feb 3 at 1:15pm" and "3 Feb, 1:15pm" are accepted alongside "Feb 3, 1:15pm".
        var normalized = Regex.Replace(when, @"\s+", " ").Trim();
        normalized = Regex.Replace(normalized, @"\bSept\b", "Sep", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s+at\s+", ", ", RegexOptions.IgnoreCase);
        var receivedLocal = TimeZoneInfo.ConvertTime(receivedAtUtc, zone);

        if (DateTime.TryParseExact(normalized, TimeOnlyFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var timeOnly))
        {
            // Choose the nearest occurrence on yesterday, today, or tomorrow, allowing BareTimeSlack of past time.
            var timeOfDay = new TimeSpan(timeOnly.Hour, timeOnly.Minute, 0);
            return Earliest(Enumerable.Range(-1, 3).Select(offset => receivedLocal.Date.AddDays(offset).Add(timeOfDay)),
                zone, receivedAtUtc, receivedAtUtc - BareTimeSlack, normalized);
        }

        if (!DateTime.TryParseExact(normalized, ResetFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var monthDayTime))
            throw new FormatException($"Unrecognized reset time '{normalized}'.");

        var candidates = new List<DateTime>(3);
        for (var year = receivedLocal.Year - 1; year <= receivedLocal.Year + 1; year++)
        {
            if (monthDayTime.Day > DateTime.DaysInMonth(year, monthDayTime.Month)) continue;   // February 29
            candidates.Add(new DateTime(year, monthDayTime.Month, monthDayTime.Day, monthDayTime.Hour, monthDayTime.Minute, 0));
        }
        return Earliest(candidates, zone, receivedAtUtc, receivedAtUtc - YearRuleSlack, normalized);
    }

    private static readonly string[] TimeOnlyFormats = ["h:mmtt", "htt", "H:mm"];

    /// <summary>A bare time's candidates may start this little before the read (a
    /// reset that has only just passed).</summary>
    private static readonly TimeSpan BareTimeSlack = TimeSpan.FromHours(1);

    /// <summary>The earliest candidate not before <paramref name="notBefore"/>.</summary>
    private static DateTimeOffset Earliest(IEnumerable<DateTime> localCandidates, TimeZoneInfo zone,
        DateTimeOffset receivedAtUtc, DateTimeOffset notBefore, string text)
    {
        DateTimeOffset? best = null;
        foreach (var local in localCandidates)
        {
            var instant = InZone(local, zone, receivedAtUtc);
            if (instant < notBefore) continue;
            if (best is null || instant < best) best = instant;
        }
        return best ?? throw new FormatException($"Reset time '{text}' has no candidate at or after the read.");
    }

    private static DateTimeOffset InZone(DateTime unspecifiedLocal, TimeZoneInfo zone, DateTimeOffset preferNotBefore)
    {
        var local = DateTime.SpecifyKind(unspecifiedLocal, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        if (zone.IsAmbiguousTime(local))
        {
            // During a repeated daylight-saving hour, prefer the earlier instant if it is still in the future.
            var instants = zone.GetAmbiguousTimeOffsets(local)
                .Select(offset => new DateTimeOffset(local, offset).ToUniversalTime())
                .Order()
                .ToArray();
            return instants.FirstOrDefault(instant => instant >= preferNotBefore, instants[^1]);
        }
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static string Slug(string scope)
    {
        var builder = new StringBuilder(scope.Length);
        foreach (var character in scope.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character)) builder.Append(character);
        }
        return builder.ToString();
    }

    /// <summary>Returns the whole text as JSON or extracts the envelope between the first and last braces,
    /// allowing update notices or warnings before it. Returns null when no envelope can be found.</summary>
    internal static JsonDocument? TryParseDocument(string text)
    {
        try { return JsonDocument.Parse(text); }
        catch (JsonException) { }
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonDocument.Parse(text.AsMemory(start, end - start + 1)); }
        catch (JsonException) { return null; }
    }

    private static double? TryNumber(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;

    private static bool? TryBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static string? TryString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
