using System.IO;
using Glideslope.Core;

namespace Glideslope.NativeProof;

/// <summary>
/// Resolves the same UserPaths layout the real app resolves under a proof root
/// (src/Glideslope.Core/UserPaths.cs: config/data/runtime/cache/logs under the root, keyed by the
/// "Glideslope" product directory name), by setting the GLIDESLOPE_PROOF_ROOT environment variable
/// DefaultUserPathProvider.BuildPaths already reads and calling the real provider. Reusing the real
/// type instead of re-deriving the folder layout by hand is deliberate: it cannot drift from what
/// Program.cs / ProofRoot.cs / UserPaths.cs actually do.
/// </summary>
internal static class ProofPaths
{
    private const string ProofRootEnvironmentVariable = "GLIDESLOPE_PROOF_ROOT";

    public static UserPaths Resolve(string proofRoot)
    {
        Environment.SetEnvironmentVariable(ProofRootEnvironmentVariable, proofRoot);
        try
        {
            return new DefaultUserPathProvider().Get();
        }
        finally
        {
            // Leave no trace on this process's environment beyond the single call: nothing else in
            // this proof relies on the variable staying set, and ProofRoot.Configure(args) inside the
            // spawned Glideslope.App process reads its own copy of the environment block anyway.
            Environment.SetEnvironmentVariable(ProofRootEnvironmentVariable, null);
        }
    }

    public static string LogFile(UserPaths paths) => Path.Combine(paths.LogsDirectory, "glideslope.log");
}
