using System.Collections.Immutable;

namespace Glideslope.Domain;

/// <summary>Stable provider identifiers used by persisted and in-memory contracts.</summary>
public static class ProviderIds
{
    public const string Codex = "codex";
    public const string Claude = "claude";
    public const string Gemini = "gemini";

    public static bool IsKnown(string providerId) => providerId is Codex or Claude or Gemini;
}

public enum QuotaBucketRole
{
    Weekly,
    Short,
    FeaturedWeekly,
    Other
}

public enum ProviderStatus
{
    Ready,
    MissingApplication,
    NeedsSignIn,
    AuthenticationExpired,
    RateLimited,
    Offline,
    UnsupportedAccount,
    SchemaChanged,
    UnknownError
}

public enum ProviderActionKind
{
    LaunchOfficialApplication,
    OpenOfficialInstructions,
    Retry
}

/// <summary>A provider-approved action without credentials or provider response data.</summary>
public sealed record ProviderAction
{
    public ProviderAction(ProviderActionKind kind, string? target = null)
    {
        if (kind == ProviderActionKind.LaunchOfficialApplication &&
            (string.IsNullOrWhiteSpace(target) || target.Length > 80 ||
             target.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))))
            throw new ArgumentException("A safe installed application identifier is required.", nameof(target));
        if (kind == ProviderActionKind.OpenOfficialInstructions &&
            (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
             !string.IsNullOrEmpty(uri.UserInfo)))
            throw new ArgumentException("A verified HTTPS instructions URI is required.", nameof(target));
        if (kind == ProviderActionKind.Retry && target is not null)
            throw new ArgumentException("Retry does not accept a target.", nameof(target));

        Kind = kind;
        Target = target;
    }

    public ProviderActionKind Kind { get; }
    public string? Target { get; }
}

/// <summary>One normalized provider quota bucket. Fractions are remaining quota in [0, 1].</summary>
public sealed record QuotaBucket
{
    public QuotaBucket(
        string id,
        QuotaBucketRole role,
        double remainingFraction,
        TimeSpan? duration,
        DateTimeOffset? resetAtUtc,
        string sourceSemantics,
        bool windowStarted = true)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Bucket ID is required.", nameof(id));
        if (!double.IsFinite(remainingFraction) || remainingFraction is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(remainingFraction), "Remaining fraction must be finite and between zero and one.");
        if (duration is { } value && value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "A known duration must be positive.");
        if (string.IsNullOrWhiteSpace(sourceSemantics))
            throw new ArgumentException("Stable source semantics are required.", nameof(sourceSemantics));

        Id = id;
        Role = role;
        RemainingFraction = remainingFraction;
        Duration = duration;
        ResetAtUtc = resetAtUtc?.ToUniversalTime();
        SourceSemantics = sourceSemantics;
        WindowStarted = windowStarted;
    }

    public string Id { get; }
    public QuotaBucketRole Role { get; }
    public double RemainingFraction { get; }
    public TimeSpan? Duration { get; }
    public DateTimeOffset? ResetAtUtc { get; }
    public string SourceSemantics { get; }

    /// <summary>Window-interaction design §17 rule 3: false when the source is describing a window that has
    /// not started (100 % remaining, nominal start at the observation). Such a bucket keeps its reset for
    /// display but has no window identity, so nothing is recorded for it. Set by
    /// <see cref="WindowIdentityPolicy"/>; providers leave it true.</summary>
    public bool WindowStarted { get; }
}

public sealed record ResetCreditDetail(string Id, DateTimeOffset? ExpiresAtUtc);

/// <summary>Authoritative available-credit count and normalized known detail rows.</summary>
public sealed record ResetCreditInventory
{
    internal ResetCreditInventory(int availableCount, ImmutableArray<ResetCreditDetail> details, int undisclosedCount)
    {
        AvailableCount = availableCount;
        Details = details;
        UndisclosedCount = undisclosedCount;
    }

    public int AvailableCount { get; }
    public ImmutableArray<ResetCreditDetail> Details { get; }
    public ImmutableArray<ResetCreditDetail> AdditionalDetails => Details.Length <= 1 ? [] : Details.RemoveAt(0);
    public int UndisclosedCount { get; }
    public bool HasKnownZero => AvailableCount == 0;
}

/// <summary>Immutable, normalized account data from one successful or last-good provider read.</summary>
public sealed record AccountSnapshot
{
    public AccountSnapshot(
        string providerId,
        string accountScope,
        string? plan,
        DateTimeOffset observedAtUtc,
        DateTimeOffset receivedAtUtc,
        string sourceId,
        IEnumerable<QuotaBucket> buckets,
        ResetCreditInventory? resetCredits = null)
    {
        if (!ProviderIds.IsKnown(providerId)) throw new ArgumentException("Unknown provider ID.", nameof(providerId));
        if (string.IsNullOrWhiteSpace(accountScope)) throw new ArgumentException("Opaque account scope is required.", nameof(accountScope));
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("Sanitized source ID is required.", nameof(sourceId));
        ArgumentNullException.ThrowIfNull(buckets);

        var normalizedBuckets = buckets.ToImmutableArray();
        if (normalizedBuckets.Any(bucket => bucket is null))
            throw new ArgumentException("Buckets cannot contain null entries.", nameof(buckets));
        if (normalizedBuckets.Select(bucket => bucket.Id).Distinct(StringComparer.Ordinal).Count() != normalizedBuckets.Length)
            throw new ArgumentException("Bucket IDs must be unique within a snapshot.", nameof(buckets));

        ProviderId = providerId;
        AccountScope = accountScope;
        Plan = plan;
        ObservedAtUtc = observedAtUtc.ToUniversalTime();
        ReceivedAtUtc = receivedAtUtc.ToUniversalTime();
        SourceId = sourceId;
        Buckets = normalizedBuckets;
        ResetCredits = resetCredits;
    }

    public string ProviderId { get; }
    /// <summary>An opaque, app-local history partition. Never place a source subject here.</summary>
    public string AccountScope { get; }
    public string? Plan { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public DateTimeOffset ReceivedAtUtc { get; }
    public string SourceId { get; }
    public ImmutableArray<QuotaBucket> Buckets { get; }
    public ResetCreditInventory? ResetCredits { get; }
}

/// <summary>Provider boundary owned by an adapter; failures remain scoped to that provider.</summary>
public interface IUsageProvider
{
    ValueTask<ProviderReadResult> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Why a read runs. Manual is a user-initiated refresh; Scheduled is a poll; AutomaticRetry is the
/// scheduler's single retry of a scheduled read that returned no answer.</summary>
public enum ProviderReadIntent { Scheduled, Manual, AutomaticRetry }

/// <summary>
/// Optional adapter contract for a provider that treats a user-initiated read
/// differently from a scheduled poll (Antigravity runs agy once despite its sign-in hold). The scheduler
/// calls this overload when a provider implements it, and the plain ReadAsync otherwise.
/// </summary>
public interface IIntentAwareUsageProvider : IUsageProvider
{
    ValueTask<ProviderReadResult> ReadAsync(ProviderReadIntent intent, CancellationToken cancellationToken);
}

/// <summary>Typed outcome for one provider read; a snapshot can accompany a failure as last-good data.</summary>
public sealed record ProviderReadResult
{
    public ProviderReadResult(
        ProviderStatus status,
        AccountSnapshot? snapshot = null,
        DateTimeOffset? retryAfterUtc = null,
        IEnumerable<ProviderAction>? actions = null,
        string? safeErrorCode = null)
    {
        if (status == ProviderStatus.Ready && snapshot is null)
            throw new ArgumentException("A ready result requires a snapshot.", nameof(snapshot));
        if (safeErrorCode is { Length: > 80 } || (safeErrorCode is not null && safeErrorCode.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))))
            throw new ArgumentException("Error code must be a short sanitized code.", nameof(safeErrorCode));

        Status = status;
        Snapshot = snapshot;
        RetryAfterUtc = retryAfterUtc?.ToUniversalTime();
        Actions = (actions ?? []).ToImmutableArray();
        SafeErrorCode = safeErrorCode;
    }

    public ProviderStatus Status { get; }
    public AccountSnapshot? Snapshot { get; }
    public DateTimeOffset? RetryAfterUtc { get; }
    public ImmutableArray<ProviderAction> Actions { get; }
    public string? SafeErrorCode { get; }
}
