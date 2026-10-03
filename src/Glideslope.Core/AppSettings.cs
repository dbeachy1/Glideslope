using System.Text.Json.Serialization;

namespace Glideslope.Core;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 2;
    public const int MinimumRefreshMinutes = 5;
    public const int MaximumRefreshMinutes = 30;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public long Revision { get; set; }
    public List<string> EnabledProviderIds { get; set; } = ProviderCatalog.DefaultEnabledProviderIds.ToList();
    public string ThemeMode { get; set; } = "system";
    /// <summary>"auto" follows the operating-system display language; otherwise this is a supported locale ID.</summary>
    public string LanguageChoice { get; set; } = "auto";
    public bool AlwaysOnTop { get; set; }
    public bool SnapToScreenEdge { get; set; }
    public bool StartAtSignIn { get; set; } = true;
    public bool StartupChoiceCompleted { get; set; }
    public int RefreshMinutes { get; set; } = 5;
    public int RetentionDays { get; set; } = 60;
    // Scales full-card content like a browser zoom.
    // A settings file without the field reads as 100. The 2.0 mini mode gets its own separate setting.
    public const int MinimumCardScalePercent = 70;
    public const int MaximumCardScalePercent = 150;
    public const int CardScaleStepPercent = 10;
    public int FullCardScalePercent { get; set; } = 100;
    // The mini card's own scale, with the same range, steps and normalization rule
    // as FullCardScalePercent. A settings file without the field reads as 100, same as the full scale.
    public int MiniCardScalePercent { get; set; } = 100;
    // Read at launch and when a new card is created. Saving it does not
    // switch cards that are already open (WindowCoordinator owns that rule).
    public bool StartInMiniMode { get; set; }
    // Enabled by default so Ctrl-peek works immediately. A saved value still wins, including false; a missing field
    // reads as true.
    public bool CtrlPeekFullCard { get; set; } = true;
    public LayoutSettings Layout { get; set; } = new();
    [JsonIgnore]
    public IReadOnlySet<string> EnabledSet => EnabledProviderIds.ToHashSet(StringComparer.Ordinal);

    public static AppSettings CreateDefault() => new();

    public static IReadOnlyList<string> SupportedLanguageChoices { get; } =
    ["auto", "en-US", "es", "fr", "de", "it", "pt-BR", "ja", "zh-Hans", "ko", "ru", "id", "zh-Hant"];

    public bool TryNormalizeLanguageChoice(out string? original)
    {
        original = LanguageChoice;
        if (string.IsNullOrWhiteSpace(LanguageChoice) || !SupportedLanguageChoices.Contains(LanguageChoice, StringComparer.Ordinal))
        {
            LanguageChoice = "auto";
            return true;
        }
        return false;
    }

    /// <summary>Clamps to 70–150 and rounds to the nearest step of 10, with midpoint values rounded up.</summary>
    public static int NormalizeCardScalePercent(int value)
    {
        var clamped = Math.Clamp(value, MinimumCardScalePercent, MaximumCardScalePercent);
        return (int)(Math.Round(clamped / (double)CardScaleStepPercent, MidpointRounding.AwayFromZero) * CardScaleStepPercent);
    }

    /// <summary>Normalizes <see cref="FullCardScalePercent"/> in place. Returns true and outputs the original
    /// value when normalization changes it.</summary>
    public bool TryNormalizeFullCardScale(out int originalPercent)
    {
        originalPercent = FullCardScalePercent;
        var normalized = NormalizeCardScalePercent(FullCardScalePercent);
        if (normalized == FullCardScalePercent)
            return false;

        FullCardScalePercent = normalized;
        return true;
    }

    /// <summary>Normalizes <see cref="MiniCardScalePercent"/> in place, the
    /// same way <see cref="TryNormalizeFullCardScale"/> does for the full scale.</summary>
    public bool TryNormalizeMiniCardScale(out int originalPercent)
    {
        originalPercent = MiniCardScalePercent;
        var normalized = NormalizeCardScalePercent(MiniCardScalePercent);
        if (normalized == MiniCardScalePercent)
            return false;

        MiniCardScalePercent = normalized;
        return true;
    }

    public AppSettings Clone()
    {
        return new AppSettings
        {
            SchemaVersion = SchemaVersion,
            Revision = Revision,
            EnabledProviderIds = EnabledProviderIds.ToList(),
            ThemeMode = ThemeMode,
            LanguageChoice = LanguageChoice,
            AlwaysOnTop = AlwaysOnTop,
            SnapToScreenEdge = SnapToScreenEdge,
            StartAtSignIn = StartAtSignIn,
            StartupChoiceCompleted = StartupChoiceCompleted,
            RefreshMinutes = RefreshMinutes,
            RetentionDays = RetentionDays,
            FullCardScalePercent = FullCardScalePercent,
            MiniCardScalePercent = MiniCardScalePercent,
            StartInMiniMode = StartInMiniMode,
            CtrlPeekFullCard = CtrlPeekFullCard,
            Layout = Layout.Clone(),
        };
    }

    public string? Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            return $"unsupported_schema_{SchemaVersion}";

        // A null provider list is invalid; the store preserves the file and uses defaults.
        if (EnabledProviderIds is null)
            return "invalid_enabled_providers";

        var distinct = EnabledProviderIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (distinct.Count == 0)
            return "at_least_one_provider_required";
        if (distinct.Any(id => !ProviderCatalog.IsKnown(id)))
            return "unknown_provider";
        if (ThemeMode is not ("system" or "dark" or "light"))
            return "invalid_theme";
        if (LanguageChoice is null || !SupportedLanguageChoices.Contains(LanguageChoice, StringComparer.Ordinal))
            return "invalid_language_choice";
        if (RefreshMinutes is < 2 or > MaximumRefreshMinutes)
            return "invalid_refresh_minutes";
        // Keep polling at least five minutes apart to limit provider load and CLI process churn. Normalize
        // older values instead of rejecting the settings file and losing its layout.
        if (RefreshMinutes < MinimumRefreshMinutes)
            RefreshMinutes = MinimumRefreshMinutes;
        if (RetentionDays is < 7 or > 365)
            return "invalid_retention_days";
        // Normalize out-of-range or off-step values so validation does not discard the rest of the settings.
        // SettingsStore performs and logs the normalization before calling Validate.
        FullCardScalePercent = NormalizeCardScalePercent(FullCardScalePercent);
        MiniCardScalePercent = NormalizeCardScalePercent(MiniCardScalePercent);
        if (Layout is null)
            return "invalid_layout_graph";
        var layoutIssue = Layout.Validate();
        if (layoutIssue is not null)
            return layoutIssue;
        var enabledIds = distinct.ToHashSet(StringComparer.Ordinal);
        if (Layout.Cards.Any(card => !enabledIds.Contains(card.ProviderId)) ||
            Layout.Edges.Any(edge => !enabledIds.Contains(edge.FirstProviderId) || !enabledIds.Contains(edge.SecondProviderId)))
            return "invalid_layout_disabled_provider";

        EnabledProviderIds = distinct;
        return null;
    }
}

public sealed class LayoutSettings
{
    /// <summary>Schema-v1 defaults retained for settings files that predate per-card layout data.</summary>
    public double CardWidth { get; set; } = 940;
    public double CardHeight { get; set; } = 680;   // Schema-v1 default; see window-interaction design §4.4.

    /// <summary>Per-provider sizes and normalized anchors. Missing legacy entries use CardWidth/CardHeight.</summary>
    public List<CardLayoutSettings> Cards { get; set; } = [];

    /// <summary>Undirected docking graph stored as oriented side relations.</summary>
    public List<DockingEdgeSettings> Edges { get; set; } = [];

    public LayoutSettings Clone()
    {
        return new LayoutSettings
        {
            CardWidth = CardWidth,
            CardHeight = CardHeight,
            Cards = Cards.Select(static card => card.Clone()).ToList(),
            Edges = Edges.Select(static edge => edge.Clone()).ToList(),
        };
    }

    public string? Validate()
    {
        if (!double.IsFinite(CardWidth) || CardWidth <= 0 || !double.IsFinite(CardHeight) || CardHeight <= 0)
            return "invalid_layout_default_size";
        if (Cards is null || Edges is null)
            return "invalid_layout_graph";

        var cardIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in Cards)
        {
            if (card is null || !ProviderCatalog.IsKnown(card.ProviderId) || !cardIds.Add(card.ProviderId))
                return "invalid_layout_card";
            if (!double.IsFinite(card.Width) || card.Width <= 0 || !double.IsFinite(card.Height) || card.Height <= 0 ||
                !double.IsFinite(card.AnchorX) || card.AnchorX is < 0 or > 1 ||
                !double.IsFinite(card.AnchorY) || card.AnchorY is < 0 or > 1 || card.GroupOrder < 0 ||
                card.MonitorId is { } monitorId &&
                (string.IsNullOrWhiteSpace(monitorId) || monitorId.Length > 256 || monitorId.Any(char.IsControl)))
                return "invalid_layout_card";
            // FullWidth/FullHeight is the full size a mini card returns to.
            // 0 on every full card, and 0 on a mini card created with no full size yet. Negative or
            // non-finite is invalid; one of the pair zero while the other is not is invalid (it can only be
            // both-zero or both-set).
            if (!double.IsFinite(card.FullWidth) || card.FullWidth < 0 || !double.IsFinite(card.FullHeight) || card.FullHeight < 0 ||
                card.FullWidth == 0 != (card.FullHeight == 0))
                return "invalid_layout_card";
            // MiniWidth/MiniHeight is the size a full card returns to when it becomes mini.
            // It is zero on every mini card (its live Width/Height already holds its mini size), and zero on a
            // full card that has never been mini. Same rule: negative or non-finite is
            // invalid, and one of the pair zero while the other is not is invalid. No cross-field rule against
            // FullWidth/FullHeight: a validation failure drops the whole layout, so this must never reject
            // something the app itself could write.
            if (!double.IsFinite(card.MiniWidth) || card.MiniWidth < 0 || !double.IsFinite(card.MiniHeight) || card.MiniHeight < 0 ||
                card.MiniWidth == 0 != (card.MiniHeight == 0))
                return "invalid_layout_card";
        }

        var edgeKeys = new HashSet<(string, string)>();
        foreach (var edge in Edges)
        {
            if (edge is null || !ProviderCatalog.IsKnown(edge.FirstProviderId) ||
                !ProviderCatalog.IsKnown(edge.SecondProviderId) ||
                string.Equals(edge.FirstProviderId, edge.SecondProviderId, StringComparison.Ordinal) ||
                !Enum.IsDefined(edge.FirstSide) || !double.IsFinite(edge.Gap) || edge.Gap < 0)
                return "invalid_layout_edge";

            var key = StringComparer.Ordinal.Compare(edge.FirstProviderId, edge.SecondProviderId) < 0
                ? (edge.FirstProviderId, edge.SecondProviderId)
                : (edge.SecondProviderId, edge.FirstProviderId);
            if (!edgeKeys.Add(key)) return "invalid_layout_edge";
        }

        return null;
    }
}

public enum CardDockSide
{
    Top,
    Right,
    Bottom,
    Left
}

/// <summary>Persisted per-card logical size and its normalized work-area top-left anchor.</summary>
public sealed class CardLayoutSettings
{
    public string ProviderId { get; set; } = string.Empty;
    public double Width { get; set; } = 940;
    public double Height { get; set; } = 680;   // Per-card default; see window-interaction design §4.4.
    public string? MonitorId { get; set; }
    public double AnchorX { get; set; } = 0.5;
    public double AnchorY { get; set; } = 0.5;
    public int GroupOrder { get; set; }
    // The full size a mini card returns to when it goes full again. Zero on
    // every full card (Width/Height already hold its live size). A mini card may have 0 too (created mini
    // with no full size yet); it then returns to the full default size at the current full scale.
    public double FullWidth { get; set; }
    public double FullHeight { get; set; }
    // The mini size a full card returns to when it goes mini again. Zero on every mini card
    // (Width/Height already hold its live mini
    // size). A full card may have 0 too (has never been mini yet); it then goes to the mini default size at
    // the current mini scale, exactly as a fresh mini card falls back to the full default above.
    public double MiniWidth { get; set; }
    public double MiniHeight { get; set; }

    public CardLayoutSettings Clone() => new()
    {
        ProviderId = ProviderId,
        Width = Width,
        Height = Height,
        MonitorId = MonitorId,
        AnchorX = AnchorX,
        AnchorY = AnchorY,
        GroupOrder = GroupOrder,
        FullWidth = FullWidth,
        FullHeight = FullHeight,
        MiniWidth = MiniWidth,
        MiniHeight = MiniHeight,
    };
}

/// <summary>The second provider is attached to the first provider's named side with the given logical gap.</summary>
public sealed class DockingEdgeSettings
{
    public string FirstProviderId { get; set; } = string.Empty;
    public string SecondProviderId { get; set; } = string.Empty;
    public CardDockSide FirstSide { get; set; }
    public double Gap { get; set; }

    public DockingEdgeSettings Clone() => new()
    {
        FirstProviderId = FirstProviderId,
        SecondProviderId = SecondProviderId,
        FirstSide = FirstSide,
        Gap = Gap,
    };
}
