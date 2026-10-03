using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Glideslope.Domain;

namespace Glideslope.Providers;

public sealed record ParsedCodexUsage(
    string Subject,
    string? Plan,
    ImmutableArray<QuotaBucket> Buckets,
    ResetCreditInventory? ResetCredits,
    ImmutableArray<CreditSchemaWarning> CreditWarnings);

public sealed class CodexAccountStateException(ProviderStatus status, string safeErrorCode) : Exception
{
    public ProviderStatus Status { get; } = status;
    public string SafeErrorCode { get; } = safeErrorCode;
}

public static class CodexUsageParser
{
    private const int ShortWindowMinutes = 300;
    private const int WeeklyWindowMinutes = 10080;
    private const string SupportedResetTypeId = "codexRateLimits";

    public static ParsedCodexUsage Parse(string accountJson, string rateLimitsJson, DateTimeOffset nowUtc)
    {
        using var accountDocument = JsonDocument.Parse(accountJson);
        using var limitsDocument = JsonDocument.Parse(rateLimitsJson);
        var account = ParseAccount(accountDocument.RootElement);
        var result = limitsDocument.RootElement;
        if (result.ValueKind != JsonValueKind.Object)
            throw new JsonException("Codex rate-limit result must be an object.");

        JsonElement codexLimits;
        if (result.TryGetProperty("rateLimitsByLimitId", out var byLimitId))
        {
            if (byLimitId.ValueKind != JsonValueKind.Object ||
                !byLimitId.TryGetProperty("codex", out codexLimits) || codexLimits.ValueKind != JsonValueKind.Object)
                throw new JsonException("Codex rate-limit map does not contain the Codex meter.");
        }
        else if (!result.TryGetProperty("rateLimits", out codexLimits) || codexLimits.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Codex legacy rate-limit result is missing.");
        }

        var sourceWindows = new List<(string SourceId, QuotaBucket Bucket)>();
        AddWindow(codexLimits, "primary", sourceWindows);
        AddWindow(codexLimits, "secondary", sourceWindows);

        var weekly = sourceWindows.FirstOrDefault(item => item.Bucket.Duration == TimeSpan.FromMinutes(WeeklyWindowMinutes));
        if (weekly.Bucket is null)
            throw new JsonException("Codex weekly window is missing or invalid.");

        var buckets = ImmutableArray.CreateBuilder<QuotaBucket>();
        buckets.Add(new QuotaBucket("codex.weekly", QuotaBucketRole.Weekly, weekly.Bucket.RemainingFraction,
            weekly.Bucket.Duration, weekly.Bucket.ResetAtUtc, weekly.SourceId));
        var shortWindow = sourceWindows.FirstOrDefault(item => item.Bucket.Duration == TimeSpan.FromMinutes(ShortWindowMinutes));
        if (shortWindow.Bucket is not null)
            buckets.Add(new QuotaBucket("codex.short", QuotaBucketRole.Short, shortWindow.Bucket.RemainingFraction,
                shortWindow.Bucket.Duration, shortWindow.Bucket.ResetAtUtc, shortWindow.SourceId));

        var otherIndex = 0;
        foreach (var source in sourceWindows.Where(item => item.Bucket.Duration is not null &&
                     item.Bucket.Duration != TimeSpan.FromMinutes(WeeklyWindowMinutes) &&
                     item.Bucket.Duration != TimeSpan.FromMinutes(ShortWindowMinutes)))
        {
            otherIndex++;
            var durationMinutes = (long)source.Bucket.Duration!.Value.TotalMinutes;
            buckets.Add(new QuotaBucket($"codex.other.{durationMinutes}m.{otherIndex}", QuotaBucketRole.Other,
                source.Bucket.RemainingFraction, source.Bucket.Duration, source.Bucket.ResetAtUtc, source.SourceId));
        }

        var (inventory, warnings) = ParseCredits(result, nowUtc);
        return new ParsedCodexUsage(account.Subject, account.Plan, buckets.ToImmutable(), inventory, warnings);
    }

    private static (string Subject, string? Plan) ParseAccount(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
            throw new JsonException("Codex account result must be an object.");
        if (!result.TryGetProperty("account", out var account) || account.ValueKind == JsonValueKind.Null)
        {
            if (result.TryGetProperty("requiresOpenaiAuth", out var requiresAuth) && requiresAuth.ValueKind == JsonValueKind.True)
                throw new CodexAccountStateException(ProviderStatus.NeedsSignIn, "codex_signed_out");
            throw new CodexAccountStateException(ProviderStatus.NeedsSignIn, "codex_account_unavailable");
        }
        if (account.ValueKind != JsonValueKind.Object ||
            !TryString(account, "type", out var type) ||
            !type.Equals("chatgpt", StringComparison.OrdinalIgnoreCase))
            throw new CodexAccountStateException(ProviderStatus.UnsupportedAccount, "codex_account_type_unsupported");
        if (!TryString(account, "email", out var email))
            throw new JsonException("Codex account subject is unavailable.");

        var plan = TryString(account, "planType", out var planValue) && IsSafePlan(planValue) ? planValue : null;
        return (email.Trim().ToLowerInvariant(), plan);
    }

    private static void AddWindow(JsonElement limits, string sourceId, ICollection<(string SourceId, QuotaBucket Bucket)> windows)
    {
        if (!limits.TryGetProperty(sourceId, out var window) || window.ValueKind == JsonValueKind.Null) return;
        if (window.ValueKind != JsonValueKind.Object ||
            !TryNumber(window, "usedPercent", out var usedPercent) ||
            !TryInt(window, "windowDurationMins", out var durationMinutes) || durationMinutes <= 0 || durationMinutes > 5256000)
            return;
        // Clamp provider overages to the supported percentage range. TryNumber has already rejected non-finite values.
        usedPercent = Math.Clamp(usedPercent, 0, 100);

        DateTimeOffset? resetAt = null;
        if (window.TryGetProperty("resetsAt", out var reset) && reset.ValueKind == JsonValueKind.Number && reset.TryGetInt64(out var unixSeconds))
        {
            try { resetAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds); }
            catch (ArgumentOutOfRangeException) { return; }
        }

        var duration = TimeSpan.FromMinutes(durationMinutes);
        var sourceSemantics = sourceId;
        if (durationMinutes == WeeklyWindowMinutes) sourceSemantics = "codex.weekly";
        else if (durationMinutes == ShortWindowMinutes) sourceSemantics = "codex.short";
        windows.Add((sourceId, new QuotaBucket($"codex.window.{sourceId}", QuotaBucketRole.Other,
            1 - (usedPercent / 100d), duration, resetAt, sourceSemantics)));
    }

    private static (ResetCreditInventory? Inventory, ImmutableArray<CreditSchemaWarning> Warnings) ParseCredits(
        JsonElement result, DateTimeOffset nowUtc)
    {
        if (!result.TryGetProperty("rateLimitResetCredits", out var credits))
            return (null, []);
        if (credits.ValueKind != JsonValueKind.Object)
        {
            var invalid = ResetCreditNormalizer.Normalize(null, null, SupportedResetTypeId, nowUtc);
            return (invalid.Inventory, invalid.Warnings);
        }

        decimal? count = credits.TryGetProperty("availableCount", out var countElement) &&
                         countElement.ValueKind == JsonValueKind.Number && countElement.TryGetDecimal(out var countValue)
            ? countValue
            : null;
        var details = new List<ResetCreditCandidate>();
        if (credits.TryGetProperty("credits", out var detailArray) && detailArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var detail in detailArray.EnumerateArray())
            {
                if (detail.ValueKind != JsonValueKind.Object) { details.Add(new(null, null, null)); continue; }

                // The app-server reports all reset-credit states using its public enum.
                // Only an explicitly available credit is a candidate for the snapshot.
                if (!TryString(detail, "status", out var status))
                {
                    details.Add(new(null, null, null));
                    continue;
                }
                if (!status.Equals("available", StringComparison.Ordinal))
                    continue;

                var id = TryString(detail, "id", out var idValue) ? idValue : null;
                var typeId = TryString(detail, "resetType", out var resetType) ? resetType : null;
                DateTimeOffset? expires = null;
                if (detail.TryGetProperty("expiresAt", out var expiration))
                    expires = ParseDateTime(expiration);
                details.Add(new ResetCreditCandidate(id, typeId, expires));
            }
        }
        var normalized = ResetCreditNormalizer.Normalize(count, details, SupportedResetTypeId, nowUtc);
        return (normalized.Inventory, normalized.Warnings);
    }

    private static DateTimeOffset? ParseDateTime(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds))
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed.ToUniversalTime();
        return null;
    }

    private static bool TryString(JsonElement parent, string name, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryNumber(JsonElement parent, string name, out double value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value) && double.IsFinite(value);
    }

    private static bool TryInt(JsonElement parent, string name, out int value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value);
    }

    private static bool IsSafePlan(string value) => value.Length <= 40 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}
