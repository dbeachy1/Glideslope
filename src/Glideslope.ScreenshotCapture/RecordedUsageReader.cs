using Glideslope.Core;
using Glideslope.Domain;
using Glideslope.Monitoring;
using Microsoft.Data.Sqlite;

namespace Glideslope.ScreenshotCapture;

internal sealed class RecordedUsageReader
{
    private static readonly string[] Providers = [ProviderIds.Codex, ProviderIds.Claude, ProviderIds.Gemini];

    public IReadOnlyDictionary<string, CaptureProviderData> ReadAll()
    {
        var dataDirectory = new DefaultUserPathProvider().Get().DataDirectory;
        var databasePath = Path.Combine(dataDirectory, UserDataMigration.HistoryDatabaseFileName);
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("No usage-history database exists. Run Glideslope and refresh the providers first.");

        var rows = ReadLatestRows(databasePath);
        var result = new Dictionary<string, CaptureProviderData>(StringComparer.Ordinal);
        foreach (var providerId in Providers)
        {
            var providerRows = rows.Where(row => row.ProviderId == providerId).ToArray();
            var weekly = providerRows.Where(row => RoleFor(row.BucketId) == QuotaBucketRole.Weekly)
                .OrderByDescending(row => row.ResetTicks > DateTimeOffset.UtcNow.UtcTicks)
                .ThenByDescending(row => row.ResetTicks)
                .ThenByDescending(row => row.ObservedTicks)
                .FirstOrDefault();
            if (weekly is null)
                throw new InvalidDataException($"No recorded weekly usage is available for {DisplayName(providerId)}. Run Glideslope and refresh that provider, then retry.");

            var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            var selected = providerRows.Where(row => row.AccountScope == weekly.AccountScope &&
                    row.ResetTicks > nowTicks && row.StartTicks <= nowTicks && RoleFor(row.BucketId) != QuotaBucketRole.Other)
                .GroupBy(row => RoleFor(row.BucketId))
                .Select(group => group.OrderByDescending(row => row.ObservedTicks).First())
                .ToDictionary(row => RoleFor(row.BucketId));
            selected[QuotaBucketRole.Weekly] = weekly;

            var observedAt = new DateTimeOffset(selected.Values.Max(row => row.ObservedTicks), TimeSpan.Zero);
            var buckets = selected.Values.Select(row => new QuotaBucket(
                row.BucketId,
                RoleFor(row.BucketId),
                row.RemainingFraction,
                DurationFor(RoleFor(row.BucketId)),
                new DateTimeOffset(row.ResetTicks, TimeSpan.Zero),
                row.SourceSemantics,
                windowStarted: row.StartTicks <= nowTicks)).ToArray();
            var snapshot = new AccountSnapshot(providerId, weekly.AccountScope, plan: null,
                observedAt, observedAt, "recorded-history", buckets);
            var state = new ProviderDisplayState(providerId, 1, true, false, ProviderStatus.Ready, snapshot,
                SnapshotFreshness.RestoredHistorical, null, [], null, null, observedAt);
            var weeklyBucket = snapshot.Buckets.Single(bucket => bucket.Role == QuotaBucketRole.Weekly);
            var identity = UsageWindowIdentityFactory.From(snapshot, weeklyBucket);
            var samples = identity is null ? [] : ReadSamples(databasePath, identity);
            result.Add(providerId, new CaptureProviderData(state, identity, samples, SamplingInterval(samples)));
        }
        return result;
    }

    private static List<WindowSample> ReadLatestRows(string databasePath)
    {
        using var connection = OpenReadOnly(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT w.account_scope, w.provider_id, w.bucket_id, w.nominal_start_utc_ticks,
                   w.reset_utc_ticks, w.source_semantics, latest.observed_utc_ticks, latest.remaining_fraction
            FROM usage_windows AS w
            INNER JOIN (
                SELECT window_id, MAX(observed_utc_ticks) AS latest_ticks
                FROM usage_observations GROUP BY window_id
            ) AS last_sample ON last_sample.window_id = w.id
            INNER JOIN usage_observations AS latest
                ON latest.window_id = last_sample.window_id AND latest.observed_utc_ticks = last_sample.latest_ticks
            WHERE w.provider_id IN ($codex, $claude, $gemini)
            ORDER BY w.provider_id, w.reset_utc_ticks DESC, latest.observed_utc_ticks DESC;
            """;
        command.Parameters.AddWithValue("$codex", ProviderIds.Codex);
        command.Parameters.AddWithValue("$claude", ProviderIds.Claude);
        command.Parameters.AddWithValue("$gemini", ProviderIds.Gemini);

        var rows = new List<WindowSample>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add(new WindowSample(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetString(5), reader.GetInt64(6), reader.GetDouble(7)));
        return rows;
    }

    private static IReadOnlyList<UsageObservation> ReadSamples(string databasePath, UsageWindowIdentity identity)
    {
        using var connection = OpenReadOnly(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT observed_utc_ticks, remaining_fraction
            FROM (
                SELECT o.observed_utc_ticks, o.remaining_fraction
                FROM usage_observations AS o
                INNER JOIN usage_windows AS w ON w.id = o.window_id
                WHERE w.account_scope = $scope AND w.provider_id = $provider AND w.bucket_id = $bucket
                  AND w.nominal_start_utc_ticks = $start AND w.reset_utc_ticks = $reset
                  AND w.source_semantics = $semantics
                ORDER BY o.observed_utc_ticks DESC LIMIT 200
            ) AS newest
            ORDER BY observed_utc_ticks ASC;
            """;
        command.Parameters.AddWithValue("$scope", identity.AccountScope);
        command.Parameters.AddWithValue("$provider", identity.ProviderId);
        command.Parameters.AddWithValue("$bucket", identity.BucketId);
        command.Parameters.AddWithValue("$start", identity.NominalStartUtc.UtcTicks);
        command.Parameters.AddWithValue("$reset", identity.ResetAtUtc.UtcTicks);
        command.Parameters.AddWithValue("$semantics", identity.SourceSemantics);
        using var reader = command.ExecuteReader();
        var samples = new List<UsageObservation>();
        while (reader.Read())
            samples.Add(new UsageObservation(identity, new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero), reader.GetDouble(1)));
        return samples;
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 2,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static QuotaBucketRole RoleFor(string id)
    {
        var value = id.ToLowerInvariant();
        if (value.Contains("fable", StringComparison.Ordinal) || value.Contains("featured", StringComparison.Ordinal))
            return QuotaBucketRole.FeaturedWeekly;
        if (value.Contains("short", StringComparison.Ordinal) || value.Contains("5h", StringComparison.Ordinal) ||
            value.Contains("five_hour", StringComparison.Ordinal) || value.Contains("five-hour", StringComparison.Ordinal))
            return QuotaBucketRole.Short;
        if (value.Contains("weekly", StringComparison.Ordinal)) return QuotaBucketRole.Weekly;
        return QuotaBucketRole.Other;
    }

    private static TimeSpan? DurationFor(QuotaBucketRole role) => role switch
    {
        QuotaBucketRole.Weekly or QuotaBucketRole.FeaturedWeekly => TimeSpan.FromDays(7),
        QuotaBucketRole.Short => TimeSpan.FromHours(5),
        _ => null,
    };

    private static TimeSpan SamplingInterval(IReadOnlyList<UsageObservation> samples)
    {
        var intervals = samples.Zip(samples.Skip(1), (first, second) => second.ObservedAtUtc - first.ObservedAtUtc)
            .Where(interval => interval > TimeSpan.Zero).OrderBy(interval => interval).ToArray();
        return intervals.Length == 0 ? TimeSpan.FromMinutes(15) : intervals[intervals.Length / 2];
    }

    private static string DisplayName(string providerId) => providerId switch
    {
        ProviderIds.Codex => "Codex",
        ProviderIds.Claude => "Claude",
        ProviderIds.Gemini => "Gemini",
        _ => "provider",
    };

    private sealed record WindowSample(string AccountScope, string ProviderId, string BucketId, long StartTicks,
        long ResetTicks, string SourceSemantics, long ObservedTicks, double RemainingFraction);
}

internal sealed record CaptureProviderData(ProviderDisplayState State, UsageWindowIdentity? WeeklyWindow,
    IReadOnlyList<UsageObservation> WeeklySamples, TimeSpan SamplingInterval);
