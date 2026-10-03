namespace Glideslope.App;

/// <summary>The stored-history key for a card's weekly bucket, available even before that window starts so it
/// can still be browsed.</summary>
internal sealed record HistoryBucketKey(string AccountScope, string ProviderId, string BucketId);
