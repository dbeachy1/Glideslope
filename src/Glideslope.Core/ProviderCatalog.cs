namespace Glideslope.Core;

public static class ProviderCatalog
{
    public const string Codex = "codex";
    public const string Claude = "claude";
    public const string Gemini = "gemini";
    public static readonly IReadOnlyList<string> DefaultEnabledProviderIds = [Codex, Claude, Gemini];
    public static bool IsKnown(string providerId) => providerId is Codex or Claude or Gemini;
    public static string DisplayName(string providerId) => providerId switch { Codex => "Codex", Claude => "Claude", Gemini => "Gemini", _ => providerId };
    public static string SourceSubtitle(string providerId) => "Usage source unavailable";
}
