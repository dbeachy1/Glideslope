using System.IO;
using System.Text.Json;
using Glideslope.Core;

namespace Glideslope.NativeProof;

/// <summary>One card's seeded logical rectangle, kept alongside the persisted settings so scenario code
/// can plan drag deltas without re-deriving anchor math (src/Glideslope.Core/LayoutPolicy.cs
/// FitGroupToWorkArea: desiredX = workArea.X + (workArea.Width - width) * anchorX).</summary>
internal readonly record struct SeededCard(string ProviderId, int X, int Y, int Width, int Height);

/// <summary>Result of seeding the proof folder's settings.json: the file path written and the three
/// cards' planned real-pixel rectangles on the primary monitor's work area.</summary>
internal sealed record SeededLayout(string SettingsFile, IReadOnlyList<SeededCard> Cards);

/// <summary>Seeds settings with Snap enabled, all providers enabled, and known card positions based on
/// the primary monitor work area. Cards meet current minimum sizes and fit in one row; if the work area
/// is too small, seeding fails with native_desktop_unavailable.</summary>
internal static class ProofSettingsSeeder
{
    private const int MarginFromWorkAreaEdge = 60;
    private const int GapBetweenCards = 220;

    public static SeededLayout Seed(string proofRoot, Action<string> log)
    {
        var paths = ProofPaths.Resolve(proofRoot);
        var workArea = ReadPrimaryWorkArea();
        var cards = PlanCards(workArea, log);
        log($"proof_settings_plan workArea=[{workArea.Left},{workArea.Top},{workArea.Right},{workArea.Bottom}] " +
            string.Join(" ", cards.Select(card => $"{card.ProviderId}=[{card.X},{card.Y},{card.Width},{card.Height}]")));

        var settings = AppSettings.CreateDefault();
        settings.EnabledProviderIds = [ProviderCatalog.Claude, ProviderCatalog.Codex, ProviderCatalog.Gemini];
        settings.SnapToScreenEdge = true;
        settings.StartupChoiceCompleted = true;
        settings.StartAtSignIn = false;
        settings.Layout = new LayoutSettings
        {
            Cards = cards.Select(card => new CardLayoutSettings
            {
                ProviderId = card.ProviderId,
                Width = card.Width,
                Height = card.Height,
                // Left null deliberately: LayoutPolicy.RecoverMissingMonitor (called on every restore,
                // not only on error recovery; see WindowCoordinator.RestoreCardLayout) falls back to the
                // live primary monitor whenever a saved MonitorId isn't one of the currently connected
                // monitors, so there is no need to guess the runtime's own monitor id string here.
                MonitorId = null,
                AnchorX = AnchorFor(card.X, workArea.Left, workArea.Right - workArea.Left, card.Width),
                AnchorY = AnchorFor(card.Y, workArea.Top, workArea.Bottom - workArea.Top, card.Height),
                GroupOrder = 0,
            }).ToList(),
            Edges = [],
        };

        var issue = settings.Validate();
        if (issue is not null)
            throw new InvalidOperationException($"Seeded settings failed AppSettings.Validate(): {issue}");

        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        var directory = Path.GetDirectoryName(paths.SettingsFile) ?? throw new InvalidOperationException("Settings path has no directory.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(paths.SettingsFile, JsonSerializer.Serialize(settings, json));
        log($"proof_settings_seeded path={paths.SettingsFile} snapToScreenEdge={settings.SnapToScreenEdge} providers={settings.EnabledProviderIds.Count}");

        return new SeededLayout(paths.SettingsFile, cards);
    }

    private static double AnchorFor(int position, int workAreaOrigin, int workAreaExtent, int cardExtent)
    {
        var free = workAreaExtent - cardExtent;
        if (free <= 0) return 0;
        return Math.Clamp((position - workAreaOrigin) / (double)free, 0, 1);
    }

    private static IReadOnlyList<SeededCard> PlanCards(NativeMethods.RECT workArea, Action<string> log)
    {
        // Claude and Gemini use the current minimum size. Codex starts larger so docking it to Claude
        // visibly resizes it; all three fit in one row, with room to drag Claude past the left edge.
        var claudeWidth = 790;
        var claudeHeight = 680;
        var codexWidth = 900;
        var codexHeight = 760;
        var geminiWidth = 790;
        var geminiHeight = 680;

        var claudeX = workArea.Left + MarginFromWorkAreaEdge;
        var claudeY = workArea.Top + MarginFromWorkAreaEdge;
        var codexX = claudeX + claudeWidth + GapBetweenCards;
        var codexY = claudeY;
        var geminiX = codexX + codexWidth + GapBetweenCards;
        var geminiY = claudeY;

        var workAreaWidth = workArea.Right - workArea.Left;
        var workAreaHeight = workArea.Bottom - workArea.Top;
        var neededRight = geminiX + geminiWidth + MarginFromWorkAreaEdge;
        var neededBottom = Math.Max(Math.Max(claudeY + claudeHeight, codexY + codexHeight), geminiY + geminiHeight) + MarginFromWorkAreaEdge;
        log($"proof_settings_fit_check workArea={workAreaWidth}x{workAreaHeight} neededRight={neededRight} " +
            $"workAreaRight={workArea.Right} neededBottom={neededBottom} workAreaBottom={workArea.Bottom}");
        if (neededRight > workArea.Right || neededBottom > workArea.Bottom)
        {
            // Program.Main turns the exception below into exit code 3 (native_desktop_unavailable);
            // this line names the reason with the measured work area so the log says why.
            log($"native_desktop_unavailable reason=work_area_too_small width={workAreaWidth} height={workAreaHeight}");
            throw new InvalidOperationException(
                $"Primary work area {workAreaWidth}x{workAreaHeight} is too small for the native window proof's planned card layout; " +
                "widen the display or lower the proof's margins/gaps in ProofSettingsSeeder.PlanCards.");
        }

        return
        [
            new SeededCard(ProviderCatalog.Claude, claudeX, claudeY, claudeWidth, claudeHeight),
            new SeededCard(ProviderCatalog.Codex, codexX, codexY, codexWidth, codexHeight),
            new SeededCard(ProviderCatalog.Gemini, geminiX, geminiY, geminiWidth, geminiHeight),
        ];
    }

    /// <summary>Seeds an independent starting layout for each card-mode scenario. Claude and Codex start
    /// docked full-size, while Gemini starts detached to their right. The edge is stored directly so each
    /// session begins in the same state without relying on a preceding gesture.</summary>
    public static SeededLayout SeedModeScenario(string proofRoot, Action<string> log)
    {
        var paths = ProofPaths.Resolve(proofRoot);
        var workArea = ReadPrimaryWorkArea();
        var cards = PlanModeScenarioCards(workArea, log);
        log($"proof_settings_mode_plan workArea=[{workArea.Left},{workArea.Top},{workArea.Right},{workArea.Bottom}] " +
            string.Join(" ", cards.Select(card => $"{card.ProviderId}=[{card.X},{card.Y},{card.Width},{card.Height}]")));

        var settings = AppSettings.CreateDefault();
        settings.EnabledProviderIds = [ProviderCatalog.Claude, ProviderCatalog.Codex, ProviderCatalog.Gemini];
        settings.SnapToScreenEdge = true;
        settings.StartupChoiceCompleted = true;
        settings.StartAtSignIn = false;
        settings.Layout = new LayoutSettings
        {
            Cards = cards.Select(card => new CardLayoutSettings
            {
                ProviderId = card.ProviderId,
                Width = card.Width,
                Height = card.Height,
                MonitorId = null,
                AnchorX = AnchorFor(card.X, workArea.Left, workArea.Right - workArea.Left, card.Width),
                AnchorY = AnchorFor(card.Y, workArea.Top, workArea.Bottom - workArea.Top, card.Height),
                GroupOrder = 0,
                // Both zero: every card here starts full (2.0 design §2.1's rule for a full card).
                FullWidth = 0,
                FullHeight = 0,
            }).ToList(),
            Edges =
            [
                new DockingEdgeSettings
                {
                    FirstProviderId = ProviderCatalog.Claude,
                    SecondProviderId = ProviderCatalog.Codex,
                    FirstSide = CardDockSide.Right,
                    Gap = LayoutPolicy.DefaultInterCardGap,
                },
            ],
        };

        var issue = settings.Validate();
        if (issue is not null)
            throw new InvalidOperationException($"Seeded mode-scenario settings failed AppSettings.Validate(): {issue}");

        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        var directory = Path.GetDirectoryName(paths.SettingsFile) ?? throw new InvalidOperationException("Settings path has no directory.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(paths.SettingsFile, JsonSerializer.Serialize(settings, json));
        log($"proof_settings_mode_seeded path={paths.SettingsFile} snapToScreenEdge={settings.SnapToScreenEdge} providers={settings.EnabledProviderIds.Count} edges={settings.Layout.Edges.Count}");

        return new SeededLayout(paths.SettingsFile, cards);
    }

    private static IReadOnlyList<SeededCard> PlanModeScenarioCards(NativeMethods.RECT workArea, Action<string> log)
    {
        // Claude and Codex: 1000x720, Codex docked LayoutPolicy.DefaultInterCardGap to
        // Claude's right. Gemini: 960x700, detached, GapBetweenCards further right so it is nowhere
        // near Codex's docked-edge hover zone (not that hovering matters any more - no scenario undocks -
        // but a clear, unambiguous gap keeps the layout easy to reason about from the log alone).
        // the sizes are neither a default nor a minimum, and the group's differs
        // from Gemini's. At 940x680 (the full default, which is also the toggle's fallback and the size
        // EnsureReturnSizes fills in) "back at the exact start rects" and "return size equals Codex's" would have
        // passed even if the remembered size were never used or PreviewDock never adopted the target's.
        const int width = 1000;
        const int height = 720;
        const int geminiWidth = 960;
        const int geminiHeight = 700;
        var dockGap = (int)LayoutPolicy.DefaultInterCardGap;

        var claudeX = workArea.Left + MarginFromWorkAreaEdge;
        var claudeY = workArea.Top + MarginFromWorkAreaEdge;
        var codexX = claudeX + width + dockGap;
        var codexY = claudeY;
        var geminiX = codexX + width + GapBetweenCards;
        var geminiY = claudeY;

        var workAreaWidth = workArea.Right - workArea.Left;
        var workAreaHeight = workArea.Bottom - workArea.Top;
        var neededRight = geminiX + geminiWidth + MarginFromWorkAreaEdge;
        var neededBottom = claudeY + height + MarginFromWorkAreaEdge;
        log($"proof_settings_mode_fit_check workArea={workAreaWidth}x{workAreaHeight} neededRight={neededRight} " +
            $"workAreaRight={workArea.Right} neededBottom={neededBottom} workAreaBottom={workArea.Bottom}");
        if (neededRight > workArea.Right || neededBottom > workArea.Bottom)
        {
            // Program.RunSession turns the exception below into exit code 3 (native_desktop_unavailable);
            // this line names the reason with the measured work area so the log says why.
            log($"native_desktop_unavailable reason=work_area_too_small width={workAreaWidth} height={workAreaHeight}");
            throw new InvalidOperationException(
                $"Primary work area {workAreaWidth}x{workAreaHeight} is too small for the native proof's mode scenarios' planned " +
                "card layout; widen the display or lower the proof's margins/gaps in ProofSettingsSeeder.PlanModeScenarioCards.");
        }

        return
        [
            new SeededCard(ProviderCatalog.Claude, claudeX, claudeY, width, height),
            new SeededCard(ProviderCatalog.Codex, codexX, codexY, width, height),
            new SeededCard(ProviderCatalog.Gemini, geminiX, geminiY, geminiWidth, geminiHeight),
        ];
    }

    /// <summary>Exposed for ScreenEdgeSnapScenario, which needs the same primary work-area rectangle
    /// to plan a drag that ends past its left edge.</summary>
    internal static NativeMethods.RECT ReadPrimaryWorkArea()
    {
        var rect = new NativeMethods.RECT();
        if (!NativeMethods.SystemParametersInfoW(NativeMethods.SPI_GETWORKAREA, 0, ref rect, 0))
            throw new InvalidOperationException($"SystemParametersInfoW(SPI_GETWORKAREA) failed, win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}.");
        return rect;
    }
}
