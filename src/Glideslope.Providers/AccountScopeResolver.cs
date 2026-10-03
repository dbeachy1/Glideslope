namespace Glideslope.Providers;

/// <summary>Derives an app-local opaque account partition from a provider-owned subject.</summary>
public delegate ValueTask<string> AccountScopeResolver(
    string providerId,
    string issuer,
    string subject,
    CancellationToken cancellationToken);
