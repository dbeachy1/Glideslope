using System.Collections.Immutable;
using Glideslope.Domain;

namespace Glideslope.Monitoring;

/// <summary>Lists the stored windows of one bucket (account scope, provider, bucket id), oldest first.
/// The app binds this to the history store; the scheduler uses it once per bucket per launch to continue
/// a stored window whose reset differs from the source's by no more than the jitter tolerance.</summary>
public delegate Task<ImmutableArray<UsageWindowIdentity>> StoredWindowLookup(
    string accountScope, string providerId, string bucketId, CancellationToken cancellationToken);

/// <summary>A scheduler decision worth a log line (see <see cref="ProviderScheduler.DiagnosticRaised"/>).
/// Never carries paths, credentials or account identifiers.</summary>
public sealed class SchedulerDiagnosticEventArgs(string code, string providerId, string status) : EventArgs
{
    public string Code { get; } = code;
    public string ProviderId { get; } = providerId;
    public string Status { get; } = status;
}
