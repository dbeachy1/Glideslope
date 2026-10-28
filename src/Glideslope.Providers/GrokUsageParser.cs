using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Glideslope.Domain;

namespace Glideslope.Providers;

public sealed record ParsedGrokUsage(string Subject, string? Plan, QuotaBucket WeeklyWindow);

/// <summary>Normalizes the authenticated ACP principal and the reported weekly subscription pool.</summary>
public static partial class GrokUsageParser
{
    public const string SourceId = "xai.grok";
    public const string WeeklyBucketId = "grok.weekly";
    private static readonly TimeSpan WeeklyWindowTolerance = TimeSpan.FromHours(1);

    public static ParsedGrokUsage Parse(string authJson, string billingJson, DateTimeOffset? receivedAtUtc = null)
    {
        using var authDoc = JsonDocument.Parse(authJson, new JsonDocumentOptions { MaxDepth = 24 });
        using var billingDoc = JsonDocument.Parse(billingJson, new JsonDocumentOptions { MaxDepth = 24 });
        var auth = authDoc.RootElement;
        var billing = billingDoc.RootElement;
        if (auth.ValueKind != JsonValueKind.Object || billing.ValueKind != JsonValueKind.Object)
            throw new GrokUsageSchemaException("grok_response_not_object");

        var subject = ReadSubject(auth);

        if (!billing.TryGetProperty("config", out var config) || config.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new GrokUsageAccountException(ProviderStatus.UnsupportedAccount, "grok_billing_config_unavailable");
        if (config.ValueKind != JsonValueKind.Object)
            throw new GrokUsageSchemaException("grok_billing_config_invalid");
        if (!config.TryGetProperty("creditUsagePercent", out var usedElement) ||
            usedElement.ValueKind != JsonValueKind.Number || !usedElement.TryGetDouble(out var used) || !double.IsFinite(used) || used < 0)
            throw new GrokUsageSchemaException("grok_usage_percent_invalid");
        if (!config.TryGetProperty("currentPeriod", out var period) || period.ValueKind != JsonValueKind.Object)
            throw new GrokUsageSchemaException("grok_period_unavailable");

        var periodType = String(period, "type");
        if (periodType != "USAGE_PERIOD_TYPE_WEEKLY")
            throw new GrokUsageAccountException(ProviderStatus.UnsupportedAccount, "grok_weekly_pool_unsupported");
        var start = ReadInstant(period, "start");
        var end = ReadInstant(period, "end");
        var duration = end - start;
        if (duration <= TimeSpan.Zero || (duration - TimeSpan.FromDays(7)).Duration() > WeeklyWindowTolerance)
            throw new GrokUsageSchemaException("grok_period_duration_invalid");
        if (end <= (receivedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime())
            throw new GrokUsageSchemaException("grok_period_not_current");

        var remaining = 1d - Math.Min(used, 100d) / 100d;
        var bucket = new QuotaBucket(WeeklyBucketId, QuotaBucketRole.Weekly, remaining, duration, end,
            $"{SourceId}.weekly");
        return new ParsedGrokUsage(subject, ReadPlan(billing), bucket);
    }

    /// <summary>Validates the selected account before the transport requests its billing data.</summary>
    internal static string ReadSubject(string authJson)
    {
        using var document = JsonDocument.Parse(authJson, new JsonDocumentOptions { MaxDepth = 24 });
        return ReadSubject(document.RootElement);
    }

    private static string ReadSubject(JsonElement auth)
    {
        if (auth.ValueKind != JsonValueKind.Object || !auth.TryGetProperty("methodId", out var methodElement) ||
            methodElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new GrokUsageSchemaException("grok_auth_info_invalid");
        var method = methodElement.GetString();
        if (method != "cached_token")
            throw new GrokUsageAccountException(method is null ? ProviderStatus.NeedsSignIn : ProviderStatus.UnsupportedAccount,
                method is null ? "grok_sign_in_required" : "grok_auth_method_unsupported");

        var principalId = String(auth, "principalId");
        string? subject = null;
        if (!string.IsNullOrWhiteSpace(principalId))
        {
            var principalType = String(auth, "principalType");
            var teamId = String(auth, "teamId");
            if (IsSafeIdentityPart(principalId) && IsSafeIdentityPart(principalType ?? "user") &&
                (teamId is null || IsSafeIdentityPart(teamId)))
                subject = JsonSerializer.Serialize(new[] { "principal", principalType ?? "user", principalId, teamId ?? "" });
        }
        if (subject is null && String(auth, "email") is { } email &&
            email.Length <= 320 && email.Contains('@') && !email.Any(char.IsControl))
            subject = $"email:{email.Trim().ToLowerInvariant()}";
        if (subject is null) throw new GrokUsageAccountException(ProviderStatus.NeedsSignIn, "grok_principal_unavailable");

        return subject;
    }

    private static DateTimeOffset ReadInstant(JsonElement element, string name)
    {
        var value = String(element, name);
        if (value is null || !HasExplicitOffset().IsMatch(value) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result))
            throw new GrokUsageSchemaException("grok_period_timestamp_invalid");
        return result.ToUniversalTime();
    }

    private static string? ReadPlan(JsonElement billing)
    {
        var tier = String(billing, "subscription_tier") ?? String(billing, "subscriptionTier");
        return tier is "Basic" or "Premium" or "Premium+" or "SuperGrok" or "SuperGrok Heavy" or "X" or "Free"
            ? tier : null;
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsSafeIdentityPart(string value) => value.Length is > 0 and <= 320 && !value.Any(char.IsControl);

    [GeneratedRegex("(?:Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HasExplicitOffset();
}

public sealed class GrokUsageSchemaException(string safeErrorCode) : Exception
{
    public string SafeErrorCode { get; } = safeErrorCode;
}

public sealed class GrokUsageAccountException(ProviderStatus status, string safeErrorCode) : Exception
{
    public ProviderStatus Status { get; } = status;
    public string SafeErrorCode { get; } = safeErrorCode;
}
