using System.Globalization;
using System.Text.Json;
using Glideslope.Domain;

namespace Glideslope.Providers;

public sealed record ParsedAntigravityUsage(QuotaBucket Weekly, QuotaBucket Short);

/// <summary>
/// Thrown when `agy`'s `/usage` response does not look like the safe, zero-token usage probe this
/// provider relies on. An unsupported slash command in print mode may be sent to the model as a prompt,
/// spending quota and creating a conversation. The provider latches after this exception so it never
/// launches `agy` again for the rest of the process lifetime; see AntigravityUsageProvider.
/// </summary>
public sealed class AntigravityUsageCommandUnsupportedException(string message) : Exception(message);

/// <summary>Thrown when `agy` itself reports a non-"SUCCESS" status. This never latches: a transient
/// failure (offline, a service-side error) must not permanently disable the provider instance.</summary>
public sealed class AntigravityUsageFailedException(string message) : Exception(message);

/// <summary>Thrown when `agy` reports that it has no usable sign-in.</summary>
public sealed class AntigravitySignInRequiredException(string message) : Exception(message);

public static class AntigravityUsageParser
{
    private static readonly TimeSpan WeeklyDuration = TimeSpan.FromDays(7);
    private static readonly TimeSpan ShortDuration = TimeSpan.FromHours(5);

    public static ParsedAntigravityUsage Parse(string standardOutput)
    {
        var jsonLine = FindLastJsonObjectLine(standardOutput);
        using var document = JsonDocument.Parse(jsonLine);
        var root = document.RootElement;

        // A reported failure that consumed nothing is an ordinary error, not the quota-spend hazard below;
        // classifying it first keeps a sign-in or service error from latching the provider off.
        if (TryString(root, "status", out var reportedStatus) && !reportedStatus.Equals("SUCCESS", StringComparison.Ordinal)
            && SpentNothing(root))
        {
            if (TryString(root, "error", out var error) && error.Contains("authentication", StringComparison.OrdinalIgnoreCase))
                throw new AntigravitySignInRequiredException("The Antigravity CLI has no usable sign-in.");
            throw new AntigravityUsageFailedException("The Antigravity CLI reported a non-SUCCESS usage status.");
        }

        // Checked before the command's own reported status: a response that looks like a real model turn
        // (nonzero tokens or turns, or a command name other than "usage") must latch regardless of what
        // status field it carries, because that is the exact quota-spend failure mode this guards against.
        if (!IsSafeUsageCommand(root))
            throw new AntigravityUsageCommandUnsupportedException(
                "The Antigravity CLI did not answer /usage with its expected zero-token usage response.");

        if (!TryString(root, "status", out var status) || !status.Equals("SUCCESS", StringComparison.Ordinal))
            throw new AntigravityUsageFailedException("The Antigravity CLI reported a non-SUCCESS usage status.");

        var (weekly, shortWindow) = SelectGeminiBuckets(root);
        if (weekly is null || shortWindow is null)
            throw new JsonException("Antigravity usage response is missing an expected Gemini quota bucket.");

        return new ParsedAntigravityUsage(weekly, shortWindow);
    }

    /// <summary>
    /// Only a `command.name == "usage"` response whose own reported usage is exactly zero tokens and zero
    /// turns is trustworthy as the real, no-model-request usage probe. Anything else means `/usage` was,
    /// for whatever reason, answered as a live model turn instead of the expected print-mode command.
    /// </summary>
    private static bool IsSafeUsageCommand(JsonElement root)
    {
        if (!root.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.Object) return false;
        if (!TryString(command, "name", out var name) || !name.Equals("usage", StringComparison.Ordinal)) return false;
        if (!root.TryGetProperty("num_turns", out var numTurns) || numTurns.ValueKind != JsonValueKind.Number ||
            !numTurns.TryGetInt64(out var turns) || turns != 0) return false;
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return false;
        if (!usage.TryGetProperty("total_tokens", out var totalTokens) || totalTokens.ValueKind != JsonValueKind.Number ||
            !totalTokens.TryGetInt64(out var tokens) || tokens != 0) return false;
        return true;
    }

    /// <summary>True only when the response reports zero turns and zero total tokens.</summary>
    private static bool SpentNothing(JsonElement root) =>
        IsZero(root, "num_turns") &&
        root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object && IsZero(usage, "total_tokens");

    private static bool IsZero(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt64(out var number) && number == 0,
            JsonValueKind.String => value.GetString() == "0",
            _ => false
        };
    }

    /// <summary>
    /// Selects the Gemini group's weekly and five-hour buckets by their stable ids, falling back to the
    /// window field only when ids are absent. The "Claude and GPT models" group (ids 3p-weekly/3p-5h) is
    /// Google's own allowance for those models within Antigravity, not the user's Anthropic or OpenAI
    /// plan; it must never be read here or feed the Claude or Codex cards.
    /// </summary>
    private static (QuotaBucket? Weekly, QuotaBucket? Short) SelectGeminiBuckets(JsonElement root)
    {
        if (!root.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.Object ||
            !command.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            return (null, null);

        QuotaBucket? weekly = null;
        QuotaBucket? shortWindow = null;
        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object) continue;
            var isGeminiGroup = TryString(group, "name", out var groupName) &&
                                groupName.StartsWith("Gemini", StringComparison.Ordinal);
            if (!group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array) continue;

            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object) continue;
                var hasId = TryString(bucket, "id", out var id);
                var hasWindow = TryString(bucket, "window", out var window);
                var isWeeklyById = hasId && id.Equals("gemini-weekly", StringComparison.Ordinal);
                var isShortById = hasId && id.Equals("gemini-5h", StringComparison.Ordinal);
                // The window-based fallback only applies within a Gemini-named group and only when no id
                // is present at all, so a 3p-* bucket (which always carries its own id) can never match it.
                var isWeeklyByFallback = !hasId && isGeminiGroup && hasWindow && window.Equals("weekly", StringComparison.Ordinal);
                var isShortByFallback = !hasId && isGeminiGroup && hasWindow && window.Equals("5h", StringComparison.Ordinal);

                if (isWeeklyById || isWeeklyByFallback)
                    weekly ??= ToBucket(bucket, "gemini.weekly", QuotaBucketRole.Weekly, WeeklyDuration, "google.antigravity.cli/gemini-weekly");
                else if (isShortById || isShortByFallback)
                    shortWindow ??= ToBucket(bucket, "gemini.short", QuotaBucketRole.Short, ShortDuration, "google.antigravity.cli/gemini-5h");
            }
        }
        return (weekly, shortWindow);
    }

    private static QuotaBucket? ToBucket(JsonElement bucket, string id, QuotaBucketRole role, TimeSpan duration, string sourceSemantics)
    {
        if (!bucket.TryGetProperty("remaining_fraction", out var fractionElement) ||
            fractionElement.ValueKind != JsonValueKind.Number || !fractionElement.TryGetDouble(out var fraction) ||
            !double.IsFinite(fraction))
            return null;
        var clamped = Math.Clamp(fraction, 0d, 1d);

        DateTimeOffset? resetAt = null;
        if (TryString(bucket, "reset_time", out var rawReset) &&
            DateTimeOffset.TryParse(rawReset, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedReset))
            resetAt = parsedReset.ToUniversalTime();

        return new QuotaBucket(id, role, clamped, duration, resetAt, sourceSemantics);
    }

    /// <summary>Antigravity CLI (`agy`) output may include non-JSON banners or warnings; only the last line that
    /// parses as a JSON object is treated as the command result.</summary>
    private static string FindLastJsonObjectLine(string standardOutput)
    {
        var lines = standardOutput.Split('\n');
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var candidate = lines[index].Trim();
            if (candidate.Length == 0) continue;
            try
            {
                using var probe = JsonDocument.Parse(candidate);
                if (probe.RootElement.ValueKind == JsonValueKind.Object)
                    return candidate;
            }
            catch (JsonException) { }
        }
        throw new JsonException("Antigravity usage output did not contain a parsable JSON object line.");
    }

    private static bool TryString(JsonElement parent, string name, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }
}
