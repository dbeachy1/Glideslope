using System.Text;

namespace Glideslope.NativeProof;

/// <summary>
/// Finds the three card windows on the hidden desktop by process ID and window title
/// ("Glideslope — Codex" / "Glideslope — Claude" / "Glideslope — Gemini", from
/// LocalizedText.CardTitle / Resources/Strings.resx Card_Title), using EnumDesktopWindows so it can
/// enumerate a desktop other than the caller's own current one.
/// </summary>
internal static class CardWindowLocator
{
    private static readonly (string ProviderId, string TitleSuffix)[] Cards =
    [
        ("codex", "Codex"),
        ("claude", "Claude"),
        ("gemini", "Gemini"),
    ];

    /// <summary>Enumerates the hidden desktop once for all windows in the process. Session startup waits
    /// for layout restoration, which occurs after card creation. The caller checks the count against
    /// <see cref="ExpectedProviderIds"/> and reports any missing cards.</summary>
    public static Dictionary<string, nint> FindCardWindows(nint hDesktop, uint processId, Action<string> log)
    {
        var found = EnumerateOnce(hDesktop, processId);
        if (found.Count != Cards.Length)
            log($"card_window_enumeration_incomplete found={found.Count} expected={Cards.Length} providers=[{string.Join(",", found.Keys)}]");
        return found;
    }

    private static Dictionary<string, nint> EnumerateOnce(nint hDesktop, uint processId)
    {
        var result = new Dictionary<string, nint>(StringComparer.Ordinal);
        var titleBuffer = new StringBuilder(256);
        bool Callback(nint hwnd, nint _)
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var ownerProcessId);
            if (ownerProcessId != processId) return true;
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;

            titleBuffer.Clear();
            var length = NativeMethods.GetWindowTextW(hwnd, titleBuffer, titleBuffer.Capacity);
            if (length <= 0) return true;
            var title = titleBuffer.ToString(0, length);

            foreach (var (providerId, suffix) in Cards)
            {
                if (title.EndsWith(suffix, StringComparison.Ordinal) && !result.ContainsKey(providerId))
                    result[providerId] = hwnd;
            }

            return true;
        }

        NativeMethods.EnumDesktopWindows(hDesktop, Callback, 0);
        return result;
    }

    /// <summary>The full ordered provider list this locator looks for, for callers that need to report
    /// which providers are still missing.</summary>
    public static IReadOnlyList<string> ExpectedProviderIds { get; } = Cards.Select(card => card.ProviderId).ToArray();
}
