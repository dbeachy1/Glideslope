using Glideslope.App;
using Glideslope.Core;
using Glideslope.Domain;
using Glideslope.Monitoring;
using Glideslope.Storage;
using Microsoft.Data.Sqlite;

namespace Glideslope.ScreenshotCapture;

internal sealed class LiveUsageReader
{
    private static readonly string[] ProviderIds = [
        Glideslope.Core.ProviderCatalog.Codex,
        Glideslope.Core.ProviderCatalog.Claude,
        Glideslope.Core.ProviderCatalog.Gemini,
    ];

    public async Task<IReadOnlyDictionary<string, CaptureProviderData>> ReadAllAsync(bool useRecordedHistory = false,
        string? ownedSessionRoot = null)
    {
        var sessionRoot = ownedSessionRoot ?? Path.Combine(AppContext.BaseDirectory, $".capture-session-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(sessionRoot, "data");
        var paths = new UserPaths(
            Path.Combine(sessionRoot, "settings.json"), dataDirectory,
            Path.Combine(sessionRoot, "runtime"), Path.Combine(sessionRoot, "cache"), Path.Combine(sessionRoot, "logs"));
        if (Directory.Exists(sessionRoot) || File.Exists(sessionRoot))
            throw new IOException("The unique capture session path already exists.");
        ProviderMonitoringSession? session = null;
        try
        {
            if (useRecordedHistory)
                CopyRecordedHistory(paths.DataDirectory);
            session = await ProviderMonitoringSession.CreateAsync(paths, AppSettings.CreateDefault(), new NullDiagnosticSink());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(105));
            using var stateChanged = new SemaphoreSlim(0, 16);
            void OnStateChanged(object? _, ProviderStateChangedEventArgs __)
            {
                try { stateChanged.Release(); }
                catch (SemaphoreFullException) { }
            }
            session.Scheduler.StateChanged += OnStateChanged;
            Dictionary<string, ProviderDisplayState> byProvider;
            try
            {
                await session.StartAsync(AppSettings.CreateDefault(), timeout.Token);
                while (true)
                {
                    byProvider = session.Scheduler.GetStates().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                    if (ProviderIds.All(id => byProvider.TryGetValue(id, out var state) && !state.IsReading && state.Status is not null))
                        break;
                    await stateChanged.WaitAsync(timeout.Token);
                }
            }
            finally { session.Scheduler.StateChanged -= OnStateChanged; }
            var notReady = ProviderIds.Where(id => !byProvider.TryGetValue(id, out var state) ||
                state.Status != ProviderStatus.Ready || state.Snapshot is null || state.Freshness != SnapshotFreshness.Fresh).ToArray();
            if (notReady.Length > 0)
                throw new InvalidDataException($"Fresh reads were unavailable for: {string.Join(", ", notReady)}. Check those provider connections and retry.");

            if (session.History is null)
                throw new IOException("The task-local capture history could not be opened.");
            await session.History.FlushAsync(timeout.Token);
            var result = new Dictionary<string, CaptureProviderData>(StringComparer.Ordinal);
            foreach (var providerId in ProviderIds)
            {
                var state = byProvider[providerId];
                var snapshot = state.Snapshot!;
                var weekly = snapshot.Buckets.FirstOrDefault(bucket => bucket.Role == QuotaBucketRole.Weekly)
                    ?? throw new InvalidDataException($"Fresh weekly usage was unavailable for {providerId}.");
                var identity = UsageWindowIdentityFactory.From(snapshot, weekly)
                    ?? throw new InvalidDataException($"A current weekly window was unavailable for {providerId}.");
                var query = await session.History.QueryWindowAsync(identity, identity.NominalStartUtc,
                    DateTimeOffset.UtcNow, SqliteUsageHistoryStore.MaximumQuerySamples, timeout.Token);
                if (!query.IsAvailable)
                    throw new IOException("The task-local capture history could not be read.");
                var samples = query.Samples;
                if (useRecordedHistory)
                    Console.WriteLine($"History samples: {providerId}={samples.Length}");
                var interval = SamplingInterval(samples);
                result.Add(providerId, new CaptureProviderData(state, identity, samples, interval));
            }
            return result;
        }
        finally
        {
            if (session is not null)
                await session.DisposeAsync();
            DeleteOwnedSessionDirectory(sessionRoot);
        }
    }

    private static void CopyRecordedHistory(string destinationDataDirectory)
    {
        var sourceDataDirectory = new DefaultUserPathProvider().Get().DataDirectory;
        var sourceDatabase = Path.Combine(sourceDataDirectory, UserDataMigration.HistoryDatabaseFileName);
        var sourceKey = Path.Combine(sourceDataDirectory, "identity", "account-scope.key");
        if (!File.Exists(sourceDatabase))
            throw new InvalidDataException("Recorded usage history was requested, but the user's usage-history database is missing.");
        if (!File.Exists(sourceKey))
            throw new InvalidDataException("Recorded usage history was requested, but the user's account-scope key is missing.");

        var keyBytes = File.ReadAllBytes(sourceKey);
        if (keyBytes.Length != 32)
            throw new InvalidDataException("Recorded usage history was requested, but the user's account-scope key is invalid.");

        Directory.CreateDirectory(destinationDataDirectory);
        var destinationDatabase = Path.Combine(destinationDataDirectory, UserDataMigration.HistoryDatabaseFileName);
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourceDatabase,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString()))
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationDatabase,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString()))
        {
            source.Open();
            destination.Open();
            // SQLite's backup API takes a consistent snapshot, including committed WAL content,
            // while the normal application may continue writing to its database.
            source.BackupDatabase(destination);
        }

        var destinationIdentityDirectory = Path.Combine(destinationDataDirectory, "identity");
        Directory.CreateDirectory(destinationIdentityDirectory);
        File.WriteAllBytes(Path.Combine(destinationIdentityDirectory, "account-scope.key"), keyBytes);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(keyBytes);
    }

    internal static void DeleteOwnedSessionDirectory(string path)
    {
        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(baseDirectory, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullPath).StartsWith(".capture-session-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove a session directory outside the capture output.");
        if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
    }

    private static TimeSpan SamplingInterval(IReadOnlyList<UsageObservation> samples)
    {
        var intervals = samples.Zip(samples.Skip(1), (first, second) => second.ObservedAtUtc - first.ObservedAtUtc)
            .Where(interval => interval > TimeSpan.Zero).OrderBy(interval => interval).ToArray();
        return intervals.Length == 0 ? TimeSpan.FromMinutes(15) : intervals[intervals.Length / 2];
    }
}
