using System.Collections.Immutable;

namespace Glideslope.Domain;

/// <summary>Provider-neutral candidate for one available reset credit.</summary>
public sealed record ResetCreditCandidate(
    string? OpaqueId,
    string? ResetTypeId,
    DateTimeOffset? ExpiresAtUtc);

public enum CreditSchemaWarning
{
    InvalidAvailableCount,
    InvalidDetail,
    ConflictingDetailId
}

public sealed record CreditInventoryNormalizationResult(
    ResetCreditInventory? Inventory,
    ImmutableArray<CreditSchemaWarning> Warnings);

/// <summary>Normalizes only the authoritative total and explicitly supported reset-credit details.</summary>
public static class ResetCreditNormalizer
{
    public static CreditInventoryNormalizationResult Normalize(
        decimal? authoritativeCount,
        IEnumerable<ResetCreditCandidate>? details,
        string supportedResetTypeId,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(supportedResetTypeId))
            throw new ArgumentException("Supported reset type is required.", nameof(supportedResetTypeId));

        if (authoritativeCount is not { } count || count < 0 || count > int.MaxValue || decimal.Truncate(count) != count)
            return new CreditInventoryNormalizationResult(null, [CreditSchemaWarning.InvalidAvailableCount]);

        var availableCount = (int)count;
        if (availableCount == 0)
            return new CreditInventoryNormalizationResult(new ResetCreditInventory(0, [], 0), []);

        var warnings = ImmutableHashSet.CreateBuilder<CreditSchemaWarning>();
        var now = nowUtc.ToUniversalTime();
        var sourceDetails = (details ?? []).ToArray();
        var validById = new Dictionary<string, List<ResetCreditCandidate>>(StringComparer.Ordinal);
        foreach (var candidate in sourceDetails)
        {
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.OpaqueId) || string.IsNullOrWhiteSpace(candidate.ResetTypeId))
            {
                warnings.Add(CreditSchemaWarning.InvalidDetail);
                continue;
            }

            if (!validById.TryGetValue(candidate.OpaqueId, out var matches))
                validById.Add(candidate.OpaqueId, matches = []);
            matches.Add(candidate);
        }

        var normalized = new List<ResetCreditDetail>();
        foreach (var (id, matches) in validById)
        {
            var first = matches[0];
            var firstExpiration = first.ExpiresAtUtc?.ToUniversalTime();
            if (matches.Any(candidate =>
                    !StringComparer.Ordinal.Equals(candidate.ResetTypeId, first.ResetTypeId) ||
                    candidate.ExpiresAtUtc?.ToUniversalTime() != firstExpiration))
            {
                warnings.Add(CreditSchemaWarning.ConflictingDetailId);
                continue;
            }

            if (!StringComparer.Ordinal.Equals(first.ResetTypeId, supportedResetTypeId))
                continue;
            if (firstExpiration is { } expiry && expiry <= now)
                continue;

            normalized.Add(new ResetCreditDetail(id, firstExpiration));
        }

        var sorted = normalized
            .OrderBy(detail => detail.ExpiresAtUtc is null ? 1 : 0)
            .ThenBy(detail => detail.ExpiresAtUtc)
            .ThenBy(detail => detail.Id, StringComparer.Ordinal)
            .Take(availableCount)
            .ToImmutableArray();
        var inventory = new ResetCreditInventory(availableCount, sorted, availableCount - sorted.Length);
        return new CreditInventoryNormalizationResult(inventory, warnings.ToImmutableArray());
    }
}
