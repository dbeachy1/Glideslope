using System.Security.Cryptography;
using System.Text;

namespace Glideslope.Storage;

/// <summary>Creates stable, app-local account scopes without persisting provider subjects.</summary>
public sealed class AccountScopeKeyStore
{
    private const int KeyLength = 32;
    private readonly string _keyPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<StorageDiagnostic> _diagnostics;

    // The optional sink receives the key store's create/regenerate decisions
    // (see StorageDiagnostic); null drops them, which keeps existing callers unchanged.
    public AccountScopeKeyStore(string userDataDirectory, Action<StorageDiagnostic>? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        _diagnostics = diagnostics ?? (static _ => { });
        var identityDirectory = Path.Combine(Path.GetFullPath(userDataDirectory), "identity");
        Directory.CreateDirectory(identityDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(identityDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        _keyPath = Path.Combine(identityDirectory, "account-scope.key");
    }

    public async ValueTask<string> ResolveAsync(
        string providerId,
        string issuer,
        string subject,
        CancellationToken cancellationToken)
    {
        ValidatePart(providerId, nameof(providerId));
        ValidatePart(issuer, nameof(issuer));
        ValidatePart(subject, nameof(subject));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = await LoadOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var input = Encoding.UTF8.GetBytes($"{providerId}\0{issuer}\0{subject}");
                try
                {
                    var digest = HMACSHA256.HashData(key, input);
                    return Convert.ToHexString(digest).ToLowerInvariant();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(input);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<byte[]> LoadOrCreateKeyAsync(CancellationToken cancellationToken)
    {
        // Publish through a temporary file and no-overwrite rename so a crash cannot leave a partial key
        // and concurrent instances agree on the first complete key.
        if (!File.Exists(_keyPath))
        {
            try
            {
                var created = await PublishNewKeyAsync(cancellationToken).ConfigureAwait(false);
                _diagnostics(new StorageDiagnostic("account_scope_key_created", "published"));
                return created;
            }
            catch (IOException) when (File.Exists(_keyPath))
            {
                // CreateNew gives concurrent application instances a single key owner.
                // The no-overwrite rename makes the first complete key the shared key for all instances.
                _diagnostics(new StorageDiagnostic("account_scope_key_created", "lost_race_reading_existing"));
            }
        }

        var loaded = await TryReadKeyAsync(cancellationToken).ConfigureAwait(false);
        if (loaded is not null)
            return loaded;

        // Treat a zero-length key as missing. Remove it only while it is still empty, then publish a
        // replacement with the same no-overwrite rename used for initial creation.
        var stale = new FileInfo(_keyPath);
        if (stale.Exists && stale.Length == 0)
            File.Delete(_keyPath);
        try
        {
            var regenerated = await PublishNewKeyAsync(cancellationToken).ConfigureAwait(false);
            _diagnostics(new StorageDiagnostic("account_scope_key_regenerated", "zero_length_replaced"));
            return regenerated;
        }
        catch (IOException) when (File.Exists(_keyPath))
        {
            _diagnostics(new StorageDiagnostic("account_scope_key_regenerated", "lost_race_reading_existing"));
        }

        return await TryReadKeyAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("The account-scope key is still zero-length after regeneration.");
    }

    /// <summary>Reads the published key; null means the file is zero-length and should be regenerated.
    /// Any other wrong length is an error.</summary>
    private async Task<byte[]?> TryReadKeyAsync(CancellationToken cancellationToken)
    {
        await using var existing = new FileStream(_keyPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        if (existing.Length == 0)
            return null;
        if (existing.Length != KeyLength)
            throw new IOException("The account-scope key has an invalid length.");

        var loaded = new byte[KeyLength];
        await existing.ReadExactlyAsync(loaded, cancellationToken).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return loaded;
    }

    /// <summary>
    /// Writes a new random key to a temporary file in the identity directory,
    /// flushes it to disk, and renames it onto account-scope.key. The rename never overwrites: it fails
    /// (IOException) when a key already exists, which is how concurrent instances agree on one owner.
    /// The temporary file is removed on every path.
    /// </summary>
    private async Task<byte[]> PublishNewKeyAsync(CancellationToken cancellationToken)
    {
        var tempPath = $"{_keyPath}.{Guid.NewGuid():N}.tmp";
        var key = RandomNumberGenerator.GetBytes(KeyLength);
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(tempPath, options))
            {
                await stream.WriteAsync(key, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, _keyPath, overwrite: false);
            return key.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temporary file holds a key nothing uses; it is harmless but logged.
                _diagnostics(new StorageDiagnostic("account_scope_key_temp_not_removed", ex.GetType().Name));
            }
        }
    }

    private static void ValidatePart(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 2048 || value.Any(char.IsControl))
            throw new ArgumentException("Account identity fields must be bounded and contain no control characters.", parameterName);
    }
}
