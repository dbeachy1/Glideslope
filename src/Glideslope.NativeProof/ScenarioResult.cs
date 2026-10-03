namespace Glideslope.NativeProof;

/// <summary>Outcome of one native window scenario:
/// its name, whether it passed, a one-line reason, and the exact log lines/settings values that prove
/// it either way. The report never says "timing" or "flaky" without quoting one of these lines.</summary>
internal sealed record ScenarioResult(string Name, bool Passed, string Reason, IReadOnlyList<string> Evidence);

/// <summary>Shared handles and diagnostics needed by every scenario.</summary>
internal sealed record ScenarioContext(
    IReadOnlyDictionary<string, nint> CardWindows,
    SeededLayout SeededLayout,
    string SettingsFile,
    ProofLog Log,
    Action<string> Diagnostics);
