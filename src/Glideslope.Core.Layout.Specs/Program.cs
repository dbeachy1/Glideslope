using System.Text.Json;
using Glideslope.Core;

namespace Glideslope.Core.Layout.Specs;

internal static partial class Program
{
    private static readonly LogicalSize Minimum = new(300, 200);

    private static async Task<int> Main()
    {
        var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"Glideslope.Core.Layout.Specs-{Guid.NewGuid():N}"));
        var marker = Guid.NewGuid().ToString("N");
        var markerPath = Path.Combine(tempRoot, ".owner");
        try
        {
            Directory.CreateDirectory(tempRoot);
            await File.WriteAllTextAsync(markerPath, marker);
            DockPreviewUsesDestinationSizeAndIsAtomic();
            IndependentCardsMayOverlapButDockGroupsMayNot();
            ScreenSnapUsesLogicalGroupBoundsAndThreshold();
            ScreenSnapPullsBackOnlyANearOvershoot();
            RejectsImpossibleLayoutsAndDetachSplitsOnlySelectedNode();
            GroupMoveAndResizeAreDeterministic();
            DisabledProviderRemovalReconcilesGraph();
            MissingMonitorRecoveryClampsAndDoesNotTeleport();
            ResetRecoveryPreservesValidGroupsAndSizes();
            LayoutSchemaRoundTripsAndReadsLegacyV1();
            DpiTransformHandlesNegativeOriginsAndFractionalBoundaries();
            DougsWorkflowAsPolicyCalls();
            CaptureKeepsAPartlyOutsideCardAndRestoresItInside();
            RecoverySplitsAGroupAndPinsACardThatCannotFit();
            ScaleGroupsKeepsGroupsDockedAtTheirTopLeft();
            EnsureMinimumSizesRaisesOnlySmallerSizes();
            ResizeAndDetachAcceptACardPartlyOffScreen();
            ReachablePlacementRule();
            // 2.0 mini mode (2.0 design §8.1).
            SwitchGroupModeTogglesSizeAndReturnsSize();
            // 2.0 mini mode (2.0 design §3.7, §8.1: "full -> mini -> resize mini -> full -> mini returns the
            // resized mini size; a card never mini gets the default").
            SwitchGroupModeRemembersEachModesSize();
            PreviewDockAdoptsDestinationsReturnSize();
            PrepareForModePreparesStartupSizes();
            ScalingOneModeLeavesTheOtherUntouched();
            PerCardMinimumsHandleMixedModeLayouts();
            EnsureReturnSizesFillsZerosOnlyForMiniCards();
            // Linux group repair and containment.
            RealignGroupPutsADriftedRowBackInLine();
            ContainGroupPullsAGroupInsideItsWorkArea();
            RealignGroupRejectsAnOutOfRangeLeadRectangle();
            await SettingsStoreRoundTripsLayoutAsync(tempRoot);
            Console.WriteLine("Glideslope layout specs passed.");
            return 0;
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                var tempBase = Path.GetFullPath(Path.GetTempPath());
                if (!tempRoot.StartsWith(tempBase, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(tempRoot).StartsWith("Glideslope.Core.Layout.Specs-", StringComparison.Ordinal) ||
                    !File.Exists(markerPath) || await File.ReadAllTextAsync(markerPath) != marker)
                    throw new IOException("Refusing to remove a layout spec directory without its ownership marker.");
                Directory.Delete(tempRoot, recursive: true);
                if (Directory.Exists(tempRoot)) throw new IOException("Layout spec temp directory was not removed.");
            }
        }
    }

    /// <summary>Three-card snapped row with 10 px gaps.</summary>
    private static LayoutSettings ThreeCardRowLayout() => new()
    {
        Cards =
        [
            new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 940, Height = 680, MonitorId = "monitor-a", GroupOrder = 0 },
            new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 940, Height = 680, MonitorId = "monitor-a", GroupOrder = 1 },
            new CardLayoutSettings { ProviderId = ProviderCatalog.Gemini, Width = 940, Height = 680, MonitorId = "monitor-a", GroupOrder = 2 },
        ],
        Edges =
        [
            new DockingEdgeSettings { FirstProviderId = ProviderCatalog.Claude, SecondProviderId = ProviderCatalog.Codex, FirstSide = CardDockSide.Right, Gap = 10 },
            new DockingEdgeSettings { FirstProviderId = ProviderCatalog.Codex, SecondProviderId = ProviderCatalog.Gemini, FirstSide = CardDockSide.Right, Gap = 10 },
        ],
    };

    private static void AssertRect(LogicalRect actual, double x, double y, double width, double height, string message) =>
        Assert(Math.Abs(actual.X - x) < 1e-6 && Math.Abs(actual.Y - y) < 1e-6 &&
               Math.Abs(actual.Width - width) < 1e-6 && Math.Abs(actual.Height - height) < 1e-6,
            $"{message} (expected {x},{y} {width}x{height}; got {actual.X},{actual.Y} {actual.Width}x{actual.Height})");

    private static LayoutSettings ThreeCardLayout() => new()
    {
        CardWidth = 940,
        CardHeight = 663,
        Cards =
        [
            new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 800, Height = 600, MonitorId = "monitor-a", AnchorX = 0.05, AnchorY = 0.05, GroupOrder = 0 },
            new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 400, Height = 300, MonitorId = "monitor-a", AnchorX = 0.6, AnchorY = 0.1, GroupOrder = 1 },
            new CardLayoutSettings { ProviderId = ProviderCatalog.Gemini, Width = 400, Height = 300, MonitorId = "monitor-a", AnchorX = 0.6, AnchorY = 0.5, GroupOrder = 2 },
        ],
        Edges =
        [
            new DockingEdgeSettings { FirstProviderId = ProviderCatalog.Claude, SecondProviderId = ProviderCatalog.Gemini, FirstSide = CardDockSide.Bottom, Gap = 20 },
        ],
    };

    private static LayoutSettings ChainLayout()
    {
        var layout = ThreeCardLayout();
        layout.Edges.Insert(0, new DockingEdgeSettings
        {
            FirstProviderId = ProviderCatalog.Codex,
            SecondProviderId = ProviderCatalog.Claude,
            FirstSide = CardDockSide.Right,
            Gap = 10,
        });
        return layout;
    }

    private static Dictionary<string, LogicalRect> CurrentThreeCardRects() => new(StringComparer.Ordinal)
    {
        [ProviderCatalog.Codex] = new(100, 100, 800, 600),
        [ProviderCatalog.Claude] = new(1200, 100, 400, 300),
        [ProviderCatalog.Gemini] = new(1200, 420, 400, 300),
    };

    private static LogicalWorkArea Area(string id, double width, double height) =>
        new(id, new LogicalRect(0, 0, width, height), isPrimary: true);

    private static string Serialize(LayoutSettings settings) =>
        JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static readonly string[] Enabled = [ProviderCatalog.Codex, ProviderCatalog.Claude, ProviderCatalog.Gemini];

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Layout spec failed: {message}.");
    }
}
