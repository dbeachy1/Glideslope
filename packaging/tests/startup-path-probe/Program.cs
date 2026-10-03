using Glideslope.Core;

if (!ProofRoot.Configure(args).Succeeded || ProofRoot.Current is not { } proofRoot)
    return 2;

var artifactPath = new PlatformStartupRegistration().ResolveArtifactPath();
Console.WriteLine(artifactPath);
return 0;
