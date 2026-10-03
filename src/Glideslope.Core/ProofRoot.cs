using System.Security.Cryptography;
using System.Text;

namespace Glideslope.Core;

public static class ProofRoot
{
    public const string ArgumentName = "--proof-root";
    private static string? _current;
    public static string? Current => _current;

    public static (bool Succeeded, string? IssueCode) Configure(IReadOnlyList<string> args)
    {
        var index = -1;
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], ArgumentName, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
        }
        if (index < 0) return (true, null);
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal)) return (false, "proof_root_missing");
        var value = args[index + 1];
        if (!Path.IsPathRooted(value)) return (false, "proof_root_must_be_absolute");
        _current = Path.GetFullPath(value);
        return (true, null);
    }

    /// <summary>
    /// Name of the
    /// named, session-local auto-reset event that <c>FileDiagnosticSink</c>'s <c>afterWrite</c> callback
    /// sets once per log line, so a harness waiting on glideslope.log under this proof root can wait on a
    /// real signal instead of polling the file on a timer. Both sides (Glideslope.App's Program.cs and the
    /// native proof harness) compute this name from nothing but the proof root's own full path, so a
    /// harness that creates or opens the event before launching the app is guaranteed to have named it
    /// exactly what the app will create or open when it configures the same --proof-root. The name is
    /// "Local\" (not "Global\") because every process here - the harness and its own child app - runs in
    /// the same login session, and Local is the default, unprivileged kernel-object namespace.
    /// </summary>
    public static string LogEventName(string proofRootFullPath)
    {
        var normalized = Path.GetFullPath(proofRootFullPath);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var hex = Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
        return $"Local\\Glideslope-Proof-{hex}";
    }
}
