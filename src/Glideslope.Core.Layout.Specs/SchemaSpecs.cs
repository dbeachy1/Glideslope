using System.Text.Json;
using Glideslope.Core;

namespace Glideslope.Core.Layout.Specs;

// Specs for persisted layout schema, settings-store round trips, DPI transforms, and card-to-card policy behavior.
internal static partial class Program
{
    private static void LayoutSchemaRoundTripsAndReadsLegacyV1()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        const string legacy = """{"schemaVersion":1,"enabledProviderIds":["codex"],"layout":{"cardWidth":800,"cardHeight":600}}""";
        var oldSettings = JsonSerializer.Deserialize<AppSettings>(legacy, options)!;
        Assert(oldSettings.SchemaVersion == 1 && oldSettings.Layout.CardWidth == 800 && oldSettings.Layout.CardHeight == 600 &&
               oldSettings.Layout.Cards.Count == 0 && oldSettings.Layout.Edges.Count == 0,
            "schema-v1 card width/height files deserialize with empty graph defaults before store migration");
        Assert(oldSettings.Validate() == "unsupported_schema_1",
            "schema-v1 data must pass through the atomic SettingsStore migration before current validation");

        var settings = new AppSettings { Layout = ChainLayout() };
        Assert(settings.Validate() is null, "complete layout graph validates with its enabled provider set");
        var json = JsonSerializer.Serialize(settings, options);
        var reloaded = JsonSerializer.Deserialize<AppSettings>(json, options)!;
        Assert(reloaded.Validate() is null && reloaded.Layout.Edges.Count == 2 &&
               reloaded.Layout.Cards[1].MonitorId == "monitor-a" && reloaded.Layout.Cards[2].GroupOrder == 2,
            "the complete card sizes, anchors, monitor identity, edges, and order survive settings serialization");

        var future = JsonSerializer.Deserialize<AppSettings>("""{"schemaVersion":99}""", options)!;
        Assert(future.Validate() == "unsupported_schema_99", "unknown newer settings schema behavior stays unchanged");
    }

    private static async Task SettingsStoreRoundTripsLayoutAsync(string tempRoot)
    {
        var store = new SettingsStore(new LayoutTestPaths(tempRoot));
        var settings = new AppSettings { Layout = ChainLayout() };
        var save = await store.SaveAsync(settings);
        Assert(save.Succeeded, "existing SettingsStore accepts a valid persisted layout");
        var loaded = await store.LoadAsync();
        Assert(loaded.IssueCode is null && loaded.Settings.Layout.Edges.Count == 2 &&
               loaded.Settings.Layout.Cards[1].Width == 400 &&
               loaded.Settings.Layout.Cards[2].MonitorId == "monitor-a" &&
               loaded.Settings.Layout.Cards[2].AnchorY == 0.5,
            "the sole settings store reloads card sizes, graph edges, monitor identity, and normalized anchors");
    }

    /// <summary>
    /// Design doc §2.1 card-to-card workflow, exercised as direct LayoutPolicy calls (design doc §10): size one card,
    /// dock a larger card onto it, and verify the incoming card resizes while the destination never
    /// changes; dock a third, even bigger card onto either one and it matches too, so all three end
    /// up the same size; resizing any card in the group resizes every card in the group to match,
    /// keeping the group's arrangement and gaps.
    /// </summary>
    private static void DougsWorkflowAsPolicyCalls()
    {
        var layout = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 620, Height = 440, MonitorId = "monitor-a" },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 940, Height = 663, MonitorId = "monitor-a" },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Gemini, Width = 1200, Height = 800, MonitorId = "monitor-a" },
            ],
        };
        var rects = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(100, 100, 620, 440),
            [ProviderCatalog.Claude] = new(2000, 100, 940, 663),
            [ProviderCatalog.Gemini] = new(2000, 900, 1200, 800),
        };
        var area = Area("monitor-a", 4000, 3000);
        var minimum = new LogicalSize(300, 200);

        // 1. Dock a bigger Claude onto Codex; Claude takes Codex's size, and Codex itself never changes.
        var dockClaude = LayoutPolicy.PreviewDock(layout, Enabled, rects, ProviderCatalog.Codex,
            ProviderCatalog.Claude, CardDockSide.Right, LayoutPolicy.DefaultInterCardGap, area, minimum);
        Assert(dockClaude.Succeeded, "docking a bigger card onto a smaller one should succeed");
        Assert(dockClaude.ProposedRects[ProviderCatalog.Codex] == rects[ProviderCatalog.Codex],
            "the destination card never changes size or position");
        Assert(dockClaude.ProposedRects[ProviderCatalog.Claude].Size == new LogicalSize(620, 440),
            "the incoming card resizes to match the destination, not the other way around");

        // 2. Dock an even bigger Gemini onto Claude; Gemini matches Claude's now-620x440 size, so
        // all three cards end up the same size.
        var dockGemini = LayoutPolicy.PreviewDock(dockClaude.ProposedSettings!, Enabled, dockClaude.ProposedRects,
            ProviderCatalog.Claude, ProviderCatalog.Gemini, CardDockSide.Bottom, LayoutPolicy.DefaultInterCardGap, area, minimum);
        Assert(dockGemini.Succeeded, "docking a third, even bigger card onto the group should succeed");
        Assert(dockGemini.ProposedRects[ProviderCatalog.Gemini].Size == new LogicalSize(620, 440) &&
               dockGemini.ProposedRects[ProviderCatalog.Claude].Size == new LogicalSize(620, 440) &&
               dockGemini.ProposedRects[ProviderCatalog.Codex].Size == new LogicalSize(620, 440),
            "all three cards end up the same size after the second dock");

        // 3. Resizing Codex - any member of the group - resizes every card to match and keeps the
        // group's arrangement and gaps.
        var resized = LayoutPolicy.ResizeGroup(dockGemini.ProposedSettings!, Enabled, dockGemini.ProposedRects,
            ProviderCatalog.Codex, new LogicalSize(700, 500), area, minimum);
        Assert(resized.Succeeded, "resizing one member of a three-card snapped group should succeed");
        Assert(resized.ProposedRects.Values.All(rect => rect.Size == new LogicalSize(700, 500)),
            "resizing any card in the group resizes every card in the group to match");
        Assert(resized.ProposedRects[ProviderCatalog.Claude].X == resized.ProposedRects[ProviderCatalog.Codex].Right + LayoutPolicy.DefaultInterCardGap,
            "the group keeps its Codex-Claude arrangement and gap after the resize");
        Assert(resized.ProposedRects[ProviderCatalog.Gemini].Y == resized.ProposedRects[ProviderCatalog.Claude].Bottom + LayoutPolicy.DefaultInterCardGap,
            "the group keeps its Claude-Gemini arrangement and gap after the resize");
    }

    private static void DpiTransformHandlesNegativeOriginsAndFractionalBoundaries()
    {
        var pixels = new PhysicalRect(-1920, 0, 1920, 1080);
        var logical = LayoutDpiTransform.ToLogical(pixels, 2);
        Assert(logical == new LogicalRect(-960, 0, 960, 540), "DPI conversion preserves negative monitor origins");
        Assert(LayoutDpiTransform.ToPhysical(logical, 2) == pixels, "physical-to-logical round trip preserves exact integer edges");

        var secondaryPixels = new PhysicalRect(2070, 120, 1500, 900);
        var secondaryOrigin = new PhysicalPoint(1920, 0);
        var secondaryLogicalOrigin = new LogicalPoint(960, 0);
        var secondaryLogical = LayoutDpiTransform.ToLogical(secondaryPixels, secondaryOrigin, secondaryLogicalOrigin, 1.5);
        Assert(secondaryLogical == new LogicalRect(1060, 80, 1000, 600) &&
               LayoutDpiTransform.ToPhysical(secondaryLogical, secondaryOrigin, secondaryLogicalOrigin, 1.5) == secondaryPixels,
            "monitor-local origins allow a second display to use a different DPI scale");

        var fractional = new LogicalRect(-0.25, 0.25, 10.5, 5.5);
        var fractionalPixels = LayoutDpiTransform.ToPhysical(fractional, 1.5);
        Assert(fractionalPixels.X == 0 && fractionalPixels.Y == 0 && fractionalPixels.Width == 15 && fractionalPixels.Height == 9,
            "fractional scale rounds edges independently, including negative half-boundaries");
    }

    private sealed class LayoutTestPaths(string root) : IUserPathProvider
    {
        public UserPaths Get() => new(
            Path.Combine(root, "config", "Glideslope", "settings.json"),
            Path.Combine(root, "data", "Glideslope"),
            Path.Combine(root, "runtime", "Glideslope"),
            Path.Combine(root, "cache", "Glideslope"),
            Path.Combine(root, "logs", "Glideslope"));
    }
}
