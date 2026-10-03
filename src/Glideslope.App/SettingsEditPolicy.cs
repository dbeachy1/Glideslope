using Glideslope.Core;
using Glideslope.Storage;

namespace Glideslope.App;

/// <summary>Builds settings candidates from the persisted authority, preserving fields not edited by this control.</summary>
internal static class SettingsEditPolicy
{
    public static AppSettings ApplyProviderSelection(AppSettings current, IEnumerable<string> providerIds)
    {
        var candidate = current.Clone();
        candidate.EnabledProviderIds = providerIds.Distinct(StringComparer.Ordinal).ToList();
        var reconciledLayout = LayoutPolicy.ReconcileEnabledCards(candidate.Layout, candidate.EnabledProviderIds);
        if (reconciledLayout.Succeeded && reconciledLayout.ProposedSettings is not null)
            candidate.Layout = reconciledLayout.ProposedSettings;
        return candidate;
    }

    public static AppSettings ApplyPreferences(AppSettings current, AppSettings requested, bool startupChoicePending)
    {
        var candidate = current.Clone();
        candidate.StartAtSignIn = requested.StartAtSignIn;
        candidate.ThemeMode = requested.ThemeMode;
        candidate.LanguageChoice = requested.LanguageChoice;
        candidate.AlwaysOnTop = requested.AlwaysOnTop;
        candidate.SnapToScreenEdge = requested.SnapToScreenEdge;
        candidate.RefreshMinutes = requested.RefreshMinutes;
        candidate.RetentionDays = requested.RetentionDays;
        // WindowCoordinator applies scale changes by resizing every card, so it reads this value separately.
        candidate.StartupChoiceCompleted = startupChoicePending ? false : current.StartupChoiceCompleted;
        return candidate;
    }

    /// <summary>
    /// Combines provider selection and preferences into one candidate for the Settings save.
    /// </summary>
    public static AppSettings ApplyProviderSelectionAndPreferences(
        AppSettings current, IEnumerable<string> providerIds, AppSettings requested, bool startupChoicePending)
    {
        var withProviders = ApplyProviderSelection(current, providerIds);
        return ApplyPreferences(withProviders, requested, startupChoicePending);
    }

    /// <summary>Retention pruning runs only for an explicit retention edit, never as a side effect of scheduler changes.</summary>
    public static async Task<string?> ApplyRetentionIntentAsync(
        IUsageHistoryStore? history,
        int retentionDays,
        bool explicitChange,
        CancellationToken cancellationToken = default)
    {
        if (!explicitChange) return null;
        if (history is null) return "history_unavailable";
        try
        {
            await history.SetRetentionDaysAsync(retentionDays, cancellationToken).ConfigureAwait(false);
            return history.Health.IsAvailable ? null : history.Health.SafeErrorCode ?? "history_prune_failed";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return "history_prune_failed";
        }
    }
}
