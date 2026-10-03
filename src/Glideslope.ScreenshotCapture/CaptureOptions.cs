using Glideslope.App;

namespace Glideslope.ScreenshotCapture;

internal sealed record CaptureOptions(string OutputDirectory, bool Overwrite, bool IsChild, bool UseLiveReads,
    bool UseRecordedHistory, bool AllowExistingRuntimeDesktops, string? SessionRoot = null)
{
    public static CaptureOptions Parse(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--child")
        {
            var childUseLiveReads = args.Contains("--live", StringComparer.Ordinal);
            var childUseRecordedHistory = args.Contains("--use-recorded-history", StringComparer.Ordinal);
            if (childUseRecordedHistory && !childUseLiveReads)
                throw new ArgumentException("--use-recorded-history requires --live.");
            return new CaptureOptions(Path.GetFullPath(args[1]), args.Contains("--overwrite", StringComparer.Ordinal), true,
                childUseLiveReads, childUseRecordedHistory, false, ReadValue(args, "--session-root"));
        }
        if (args.Length < 2 || args[0] != "--output" || args.Skip(2).Any(arg => arg is not ("--overwrite" or "--live" or "--use-recorded-history" or "--allow-existing-runtime-desktops")))
            throw new ArgumentException("Usage: Capture-ReadmeScreenshots.ps1 -OutputDirectory <project-path> [-Live] [-UseRecordedHistory] [-AllowExistingRuntimeDesktops] [-Overwrite].");

        var useLiveReads = args.Contains("--live", StringComparer.Ordinal);
        var useRecordedHistory = args.Contains("--use-recorded-history", StringComparer.Ordinal);
        if (useRecordedHistory && !useLiveReads)
            throw new ArgumentException("--use-recorded-history requires --live.");

        return new CaptureOptions(Path.GetFullPath(args[1]), args.Contains("--overwrite", StringComparer.Ordinal), false,
            useLiveReads, useRecordedHistory, args.Contains("--allow-existing-runtime-desktops", StringComparer.Ordinal));
    }

    public string AppVersion => typeof(ProviderUsageCardWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static string? ReadValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;
    }

    public string[] ExpectedFileNames =>
    [
        $"Codex-Full-Dark-v{AppVersion}.png",
        $"Claude-Mini-Dark-v{AppVersion}.png",
        $"Claude-Full-Light-v{AppVersion}.png",
        $"Gemini-Full-Dark-v{AppVersion}.png",
    ];

    public string[] ExistingOutputs() => ExpectedFileNames
        .Select(name => Path.Combine(OutputDirectory, name))
        .Where(File.Exists)
        .ToArray();

    public void EnsureNoUnapprovedOverwrite()
    {
        var existing = ExistingOutputs();
        if (!Overwrite && existing.Length > 0)
            throw new InvalidOperationException($"Capture would replace existing files: {string.Join(", ", existing.Select(Path.GetFileName))}. Pass --overwrite to replace only these four named images.");
    }
}
