namespace Glideslope.Core;

internal sealed record SettingsProviderIdMigrationResult(
    bool WasMigrated,
    bool GeminiIdMigrated,
    bool GeminiLayoutDetached,
    string? IssueCode);

/// <summary>Maps the legacy Antigravity settings identifier to the stable Gemini ID.</summary>
internal static class SettingsProviderIdMigration
{
    private const string LegacyId = "antigravity";

    public static SettingsProviderIdMigrationResult Apply(AppSettings settings, int sourceSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var hasLegacyId = ContainsLegacyId(settings);
        if (sourceSchemaVersion != 1)
            return new(false, false, false, null);
        if (sourceSchemaVersion >= AppSettings.CurrentSchemaVersion)
            return new(false, false, false, null);

        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        if (!hasLegacyId)
        {
            var schemaIssue = ValidateMigratedSettings(settings);
            return new(true, false, false, schemaIssue);
        }

        var enabled = settings.EnabledProviderIds ?? [];
        var layout = settings.Layout ?? new LayoutSettings();
        var cards = layout.Cards ?? [];
        var edges = layout.Edges ?? [];
        var hasGemini = enabled.Contains(ProviderCatalog.Gemini, StringComparer.Ordinal) ||
                        cards.Any(static card => card?.ProviderId == ProviderCatalog.Gemini) ||
                        edges.Any(static edge => edge is not null &&
                            (edge.FirstProviderId == ProviderCatalog.Gemini || edge.SecondProviderId == ProviderCatalog.Gemini));

        settings.EnabledProviderIds = MigrateEnabledIds(enabled);
        settings.Layout = layout;
        layout.Cards = MigrateCards(cards);
        layout.Edges = MigrateEdges(edges, hasGemini);

        var issue = ValidateMigratedSettings(settings);
        if (issue is null) return new(true, true, false, null);
        if (!IsLayoutIssue(issue)) return new(true, true, false, issue);

        // Keep all unaffected placements. Invalid Gemini geometry falls back to a fresh
        // placement; otherwise its geometry remains while only its docking is removed.
        layout.Edges.RemoveAll(IsGeminiEdge);
        if (layout.Cards.Any(static card => card?.ProviderId == ProviderCatalog.Gemini && !IsValidCard(card)))
            layout.Cards.RemoveAll(static card => card?.ProviderId == ProviderCatalog.Gemini);

        issue = ValidateMigratedSettings(settings);
        return issue is null
            ? new(true, true, true, null)
            : new(true, true, false, issue);
    }

    private static bool ContainsLegacyId(AppSettings settings)
    {
        if (settings.EnabledProviderIds?.Contains(LegacyId, StringComparer.Ordinal) == true) return true;
        if (settings.Layout?.Cards?.Any(static card => card?.ProviderId == LegacyId) == true) return true;
        return settings.Layout?.Edges?.Any(static edge => edge is not null &&
            (edge.FirstProviderId == LegacyId || edge.SecondProviderId == LegacyId)) == true;
    }

    private static List<string> MigrateEnabledIds(IReadOnlyList<string> ids)
    {
        var newIdAlreadyPresent = ids.Contains(ProviderCatalog.Gemini, StringComparer.Ordinal);
        var result = new List<string>(ids.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (id == LegacyId)
            {
                if (!newIdAlreadyPresent && seen.Add(ProviderCatalog.Gemini)) result.Add(ProviderCatalog.Gemini);
                continue;
            }

            if (seen.Add(id)) result.Add(id);
        }

        return result;
    }

    private static List<CardLayoutSettings> MigrateCards(IReadOnlyList<CardLayoutSettings> cards)
    {
        var hasGeminiCard = cards.Any(static card => card?.ProviderId == ProviderCatalog.Gemini);
        var result = new List<CardLayoutSettings>();
        CardLayoutSettings? selectedGemini = null;
        foreach (var card in cards)
        {
            if (card is null) continue;
            if (card?.ProviderId == ProviderCatalog.Gemini)
            {
                selectedGemini ??= card.Clone();
                continue;
            }

            if (card?.ProviderId == LegacyId)
            {
                if (!hasGeminiCard && selectedGemini is null)
                    selectedGemini = CloneWithProviderId(card, ProviderCatalog.Gemini);
                continue;
            }

            result.Add(card!);
        }

        if (selectedGemini is not null) result.Add(selectedGemini);
        return result;
    }

    private static List<DockingEdgeSettings> MigrateEdges(IReadOnlyList<DockingEdgeSettings> edges, bool hasGemini)
    {
        var result = new List<DockingEdgeSettings>();
        var seen = new HashSet<(string First, string Second)>();
        foreach (var edge in edges)
        {
            if (edge is null) continue;
            if (hasGemini && (edge.FirstProviderId == LegacyId || edge.SecondProviderId == LegacyId)) continue;

            var first = edge.FirstProviderId == LegacyId ? ProviderCatalog.Gemini : edge.FirstProviderId;
            var second = edge.SecondProviderId == LegacyId ? ProviderCatalog.Gemini : edge.SecondProviderId;
            var key = StringComparer.Ordinal.Compare(first, second) < 0 ? (first, second) : (second, first);
            if (!seen.Add(key)) continue;
            result.Add(new DockingEdgeSettings
            {
                FirstProviderId = first,
                SecondProviderId = second,
                FirstSide = edge.FirstSide,
                Gap = edge.Gap,
            });
        }

        return result;
    }

    private static string? ValidateMigratedSettings(AppSettings settings)
    {
        var issue = settings.Validate();
        if (issue is not null) return issue;
        var graph = LayoutPolicy.ReconcileEnabledCards(settings.Layout, settings.EnabledProviderIds);
        return graph.SafeErrorCode;
    }

    private static bool IsLayoutIssue(string issue) =>
        issue.StartsWith("invalid_layout", StringComparison.Ordinal) ||
        issue.StartsWith("layout_", StringComparison.Ordinal);

    private static bool IsGeminiEdge(DockingEdgeSettings edge) =>
        edge is not null &&
        (edge.FirstProviderId == ProviderCatalog.Gemini || edge.SecondProviderId == ProviderCatalog.Gemini);

    private static bool IsValidCard(CardLayoutSettings? card) =>
        card is not null &&
        double.IsFinite(card.Width) && card.Width > 0 &&
        double.IsFinite(card.Height) && card.Height > 0 &&
        double.IsFinite(card.AnchorX) && card.AnchorX is >= 0 and <= 1 &&
        double.IsFinite(card.AnchorY) && card.AnchorY is >= 0 and <= 1 &&
        card.GroupOrder >= 0 &&
        (card.MonitorId is null ||
         !string.IsNullOrWhiteSpace(card.MonitorId) && card.MonitorId.Length <= 256 && !card.MonitorId.Any(char.IsControl));

    private static CardLayoutSettings CloneWithProviderId(CardLayoutSettings card, string providerId) => new()
    {
        ProviderId = providerId,
        Width = card.Width,
        Height = card.Height,
        MonitorId = card.MonitorId,
        AnchorX = card.AnchorX,
        AnchorY = card.AnchorY,
        GroupOrder = card.GroupOrder,
    };
}
