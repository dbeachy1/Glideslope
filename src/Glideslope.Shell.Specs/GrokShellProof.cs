using Glideslope.Core;

namespace Glideslope.Shell.Specs;

/// <summary>Focused catalog and persistence checks for Grok's additive provider ID.</summary>
internal static class GrokShellProof
{
    public static async Task RunSettingsAsync(string root)
    {
        var defaults = AppSettings.CreateDefault();
        Assert(defaults.EnabledSet.SetEquals(ProviderCatalog.DefaultEnabledProviderIds) &&
               defaults.EnabledSet.Contains(ProviderCatalog.Grok),
            "new settings enable Grok alongside the existing providers");
        Assert(ProviderCatalog.IsKnown(ProviderCatalog.Grok) && ProviderCatalog.DisplayName(ProviderCatalog.Grok) == "Grok",
            "the catalog recognizes Grok and its provider name");

        var paths = new FixedPaths(new UserPaths(
            Path.Combine(root, "grok-settings", "settings.json"),
            Path.Combine(root, "grok-settings", "data"),
            Path.Combine(root, "grok-settings", "runtime"),
            Path.Combine(root, "grok-settings", "cache"),
            Path.Combine(root, "grok-settings", "logs")));
        var store = new SettingsStore(paths);
        var loaded = await store.LoadAsync().ConfigureAwait(false);
        Assert(loaded.WasCreated && loaded.Settings.EnabledSet.Contains(ProviderCatalog.Grok),
            "a newly created settings file includes the Grok default");

        var selected = loaded.Settings.Clone();
        selected.EnabledProviderIds = [ProviderCatalog.Codex, ProviderCatalog.Grok];
        Assert((await store.SaveAsync(selected, loaded.Settings.Revision).ConfigureAwait(false)).Succeeded,
            "a selected Grok provider saves through the existing settings contract");
        var roundTrip = await store.LoadAsync().ConfigureAwait(false);
        Assert(roundTrip.Settings.EnabledSet.SetEquals([ProviderCatalog.Codex, ProviderCatalog.Grok]),
            "the Grok provider selection round-trips without a schema migration");
    }

    private sealed class FixedPaths(UserPaths paths) : IUserPathProvider
    {
        public UserPaths Get() => paths;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {message}");
    }
}
