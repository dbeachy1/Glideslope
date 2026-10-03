using System.Text.Json;

namespace Glideslope.Core;

public sealed record SettingsLoadResult(
    AppSettings Settings,
    string? IssueCode = null,
    bool WasCreated = false,
    bool WasMigrated = false);
/// <summary>WarningCode is set when the settings were saved and applied but a
/// side effect did not complete (for example start-at-sign-in registration). Succeeded stays true; the
/// Settings window shows only the warning, never "Could not apply settings".</summary>
public sealed record SettingsSaveResult(bool Succeeded, string? IssueCode = null, long? CommittedRevision = null, string? WarningCode = null);

public sealed class SettingsStore
{
    private readonly IUserPathProvider _paths;
    private readonly IDiagnosticSink _diagnostics;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public SettingsStore(IUserPathProvider paths, IDiagnosticSink? diagnostics = null) { _paths = paths; _diagnostics = diagnostics ?? new NullDiagnosticSink(); }
    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = _paths.Get().SettingsFile;
        // Keep the parsed revision available to fallback paths after deserialization fails.
        byte[]? document = null;
        try
        {
            if (!File.Exists(path))
            {
                var defaults = AppSettings.CreateDefault();
                var save = await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
                if (save.CommittedRevision is not null)
                    defaults.Revision = save.CommittedRevision.Value;
                return save.Succeeded ? new SettingsLoadResult(defaults, WasCreated: true) : new SettingsLoadResult(defaults, save.IssueCode);
            }

            document = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            // A non-object root is corrupt; preserve the file and use defaults.
            var rootIssue = DescribeNonObjectRoot(document);
            if (rootIssue is not null)
            {
                _diagnostics.Record(new DiagnosticEvent("settings_document_rejected", Status: rootIssue));
                throw new JsonException("settings_root_not_object");
            }

            var hasSchemaVersion = TryReadSchemaVersion(document, out var schemaVersion);
            if (hasSchemaVersion && schemaVersion > AppSettings.CurrentSchemaVersion)
            {
                _diagnostics.Record(new DiagnosticEvent("settings_newer_schema", Status: "settings_load_rejected"));
                return new SettingsLoadResult(DefaultsAtOnDiskRevision(document), "settings_newer_schema");
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(document, _json) ?? throw new JsonException("empty_settings");
            if (settings.TryNormalizeLanguageChoice(out var storedLanguage))
                _diagnostics.Record(new DiagnosticEvent("settings_language_normalized", Status: "invalid_choice_to_auto"));
            // Normalize and log scale values without discarding the rest of the settings. A missing field is 100.
            if (settings.TryNormalizeFullCardScale(out var storedScale))
                _diagnostics.Record(new DiagnosticEvent("settings_full_card_scale_normalized", Status: $"load,from_{storedScale}_to_{settings.FullCardScalePercent}"));
            // Normalize and log the mini scale the same way.
            if (settings.TryNormalizeMiniCardScale(out var storedMiniScale))
                _diagnostics.Record(new DiagnosticEvent("settings_mini_card_scale_normalized", Status: $"load,from_{storedMiniScale}_to_{settings.MiniCardScalePercent}"));
            var sourceSchemaVersion = hasSchemaVersion ? schemaVersion : 1;
            var migration = SettingsProviderIdMigration.Apply(settings, sourceSchemaVersion);
            if (migration.WasMigrated && migration.IssueCode is not null)
            {
                _diagnostics.Record(new DiagnosticEvent(migration.IssueCode, Status: "settings_migration_rejected"));
                return new SettingsLoadResult(PreserveInvalidThenDefaults(path, document), migration.IssueCode);
            }

            if (migration.GeminiLayoutDetached)
                _diagnostics.Record(new DiagnosticEvent("settings_gemini_layout_detached", Status: "invalid_migrated_layout"));

            if (migration.WasMigrated)
            {
                _diagnostics.Record(new DiagnosticEvent(
                    migration.GeminiIdMigrated ? "settings_provider_id_migrated" : "settings_schema_migrated",
                    Status: migration.GeminiIdMigrated ? "legacy_id_migrated" : "schema_v1_to_v2"));
                var save = await SaveAsync(settings, settings.Revision, cancellationToken).ConfigureAwait(false);
                if (save.CommittedRevision is not null) settings.Revision = save.CommittedRevision.Value;
                if (!save.Succeeded)
                    _diagnostics.Record(new DiagnosticEvent("settings_migration_save_failed", Status: save.IssueCode ?? "unknown"));
                return new SettingsLoadResult(settings, save.IssueCode, WasMigrated: true);
            }

            var issue = settings.Validate();
            if (issue is not null) { _diagnostics.Record(new DiagnosticEvent(issue, Status: "settings_invalid")); return new SettingsLoadResult(PreserveInvalidThenDefaults(path, document), issue); }
            return new SettingsLoadResult(settings);
        }
        catch (JsonException)
        {
            var preserved = PreserveCorruptFile(path); var issue = preserved ? "settings_corrupt_preserved" : "settings_corrupt";
            _diagnostics.Record(new DiagnosticEvent(issue, Status: "settings_load_failed"));
            // Adopt the file revision only when a copy was preserved so the next save cannot overwrite the
            // only copy after a preservation failure.
            return new SettingsLoadResult(document is null || !preserved ? AppSettings.CreateDefault() : DefaultsAtOnDiskRevision(document), issue);
        }
        catch (Exception ex) when (document is not null && IsDocumentShapeException(ex))
        {
            // Preserve readable files that cannot be migrated or validated, then use defaults and log the error.
            _diagnostics.Record(new DiagnosticEvent("settings_shape_exception", Status: ex.GetType().Name));
            return new SettingsLoadResult(PreserveInvalidThenDefaults(path, document), "settings_shape_invalid");
        }
        // Keep revision zero when the file cannot be read; the revision conflict prevents defaults from
        // overwriting a file whose settings have not been seen.
        catch (IOException ex) { _diagnostics.Record(new DiagnosticEvent("settings_io_error", Status: $"settings_load_failed,type={ex.GetType().Name}")); return new SettingsLoadResult(AppSettings.CreateDefault(), "settings_io_error"); }
        catch (UnauthorizedAccessException ex) { _diagnostics.Record(new DiagnosticEvent("settings_io_error", Status: $"settings_load_failed,type={ex.GetType().Name}")); return new SettingsLoadResult(AppSettings.CreateDefault(), "settings_io_error"); }
    }
    public Task<SettingsSaveResult> SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        return SaveAsync(settings, settings.Revision, cancellationToken);
    }

    public async Task<SettingsSaveResult> SaveAsync(AppSettings settings, long expectedRevision, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var path = _paths.Get().SettingsFile; var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            if (File.Exists(path))
            {
                var existing = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                if (TryReadSchemaVersion(existing, out var schemaVersion) && schemaVersion > AppSettings.CurrentSchemaVersion)
                {
                    _diagnostics.Record(new DiagnosticEvent("settings_newer_schema", Status: "settings_save_rejected"));
                    return new SettingsSaveResult(false, "settings_newer_schema");
                }

                if (TryReadRevision(existing, out var actualRevision))
                {
                    if (actualRevision != expectedRevision)
                    {
                        _diagnostics.Record(new DiagnosticEvent("settings_revision_conflict", Status: "settings_save_rejected"));
                        return new SettingsSaveResult(false, "settings_revision_conflict");
                    }
                }
                else
                {
                    // An absent or unparseable revision cannot conflict with the current settings revision.
                    _diagnostics.Record(new DiagnosticEvent("settings_revision_absent", Status: $"save_unchecked_expected_{expectedRevision}"));
                }
            }

            // Normalize and log scale values from any caller before saving.
            if (settings.TryNormalizeFullCardScale(out var requestedScale))
                _diagnostics.Record(new DiagnosticEvent("settings_full_card_scale_normalized", Status: $"save,from_{requestedScale}_to_{settings.FullCardScalePercent}"));
            // Normalize and log the mini scale the same way.
            if (settings.TryNormalizeMiniCardScale(out var requestedMiniScale))
                _diagnostics.Record(new DiagnosticEvent("settings_mini_card_scale_normalized", Status: $"save,from_{requestedMiniScale}_to_{settings.MiniCardScalePercent}"));
            var issue = settings.Validate();
            if (issue is not null)
            {
                _diagnostics.Record(new DiagnosticEvent(issue, Status: "settings_save_rejected"));
                return new SettingsSaveResult(false, issue);
            }

            var committed = settings.Clone();
            committed.Revision = expectedRevision + 1;
            var directory = Path.GetDirectoryName(path); if (string.IsNullOrWhiteSpace(directory)) return new SettingsSaveResult(false, "settings_path_invalid");
            Directory.CreateDirectory(directory);
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, committed, _json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                // Flush to disk before replacing settings.json so a power loss cannot leave a partial file.
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
            return new SettingsSaveResult(true, CommittedRevision: committed.Revision);
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException ex) { _diagnostics.Record(new DiagnosticEvent("settings_save_io_error", Status: $"settings_save_failed,type={ex.GetType().Name}")); return new SettingsSaveResult(false, "settings_save_io_error"); }
        catch (UnauthorizedAccessException ex) { _diagnostics.Record(new DiagnosticEvent("settings_save_io_error", Status: $"settings_save_failed,type={ex.GetType().Name}")); return new SettingsSaveResult(false, "settings_save_io_error"); }
        finally { _writeGate.Release(); TryDelete(temp); }
    }
    private bool PreserveCorruptFile(string path)
    {
        if (!File.Exists(path)) return false; var copy = $"{path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}";
        try
        {
            File.Copy(path, copy);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Log I/O and access failures. Keeping revision zero protects an unreadable or unpreserved file
            // from a later save.
            _diagnostics.Record(new DiagnosticEvent("settings_corrupt_not_preserved", Status: ex.GetType().Name));
            return false;
        }
    }

    /// <summary>Returns null when the document's root is a JSON object, otherwise why
    /// not ("not_json", or "root_" plus the JSON value kind).</summary>
    private static string? DescribeNonObjectRoot(byte[] document)
    {
        try
        {
            using var json = JsonDocument.Parse(document);
            return json.RootElement.ValueKind == JsonValueKind.Object ? null : $"root_{json.RootElement.ValueKind}";
        }
        catch (JsonException)
        {
            // Not JSON at all: reported as the reason; the caller logs it and takes the corrupt-file path.
            return "not_json";
        }
    }

    /// <summary>The exceptions a readable settings document of the wrong shape
    /// can raise while it is migrated and validated. ObjectDisposedException derives from
    /// InvalidOperationException but never comes from the document, so it is not one of them.</summary>
    private static bool IsDocumentShapeException(Exception ex) =>
        ex is not ObjectDisposedException &&
        ex is InvalidOperationException or ArgumentException or NullReferenceException or FormatException
            or OverflowException or InvalidCastException or KeyNotFoundException or IndexOutOfRangeException;

    private static bool TryReadSchemaVersion(byte[] document, out int schemaVersion)
    {
        schemaVersion = 0;
        try
        {
            using var json = JsonDocument.Parse(document);
            // EnumerateObject throws InvalidOperationException for a non-object root.
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            foreach (var property in json.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "schemaVersion", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.TryGetInt32(out schemaVersion))
                    return true;
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    private static bool TryReadRevision(byte[] document, out long revision)
    {
        revision = 0;
        try
        {
            using var json = JsonDocument.Parse(document);
            // A non-object root has no revision.
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            foreach (var property in json.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "revision", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.TryGetInt64(out revision))
                    return true;
            }
        }
        catch (JsonException)
        {
            // Not JSON: no revision can be read. Callers log what they decide from the false result.
        }

        // Distinguish a missing revision from an explicit revision zero.
        return false;
    }

    /// <summary>
    /// Defaults to use when a readable settings file cannot be used. Starting at the file's revision allows
    /// the next save to replace the invalid settings after their bytes have been preserved.
    /// </summary>
    /// <summary>
    /// Before falling back, preserve the exact file bytes beside the source. If preservation fails, retain
    /// revision zero so a later save cannot overwrite the only copy.
    /// </summary>
    private AppSettings PreserveInvalidThenDefaults(string path, byte[] document)
    {
        var copy = $"{path}.invalid-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}";
        var created = false;
        try
        {
            using (var stream = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(document);
                stream.Flush(flushToDisk: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _diagnostics.Record(new DiagnosticEvent("settings_invalid_not_preserved", Status: ex.GetType().Name));
            if (created)
            {
                // Only a copy this call created is removed, never one an earlier load made.
                try { File.Delete(copy); }
                catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
                {
                    _diagnostics.Record(new DiagnosticEvent("settings_invalid_partial_copy_kept", Status: cleanupEx.GetType().Name));
                }
            }

            return AppSettings.CreateDefault();
        }

        _diagnostics.Record(new DiagnosticEvent("settings_invalid_preserved", Status: Path.GetFileName(copy)));
        return DefaultsAtOnDiskRevision(document);
    }

    private AppSettings DefaultsAtOnDiskRevision(byte[] document)
    {
        var defaults = AppSettings.CreateDefault();
        if (TryReadRevision(document, out var revision))
        {
            defaults.Revision = revision;
            _diagnostics.Record(new DiagnosticEvent("settings_fallback_revision", Status: $"adopted_{revision}"));
        }
        else
        {
            _diagnostics.Record(new DiagnosticEvent("settings_fallback_revision", Status: "absent_kept_0"));
        }

        return defaults;
    }
    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is harmless, but report it for diagnosis.
            _diagnostics.Record(new DiagnosticEvent("settings_temp_cleanup_failed", Status: ex.GetType().Name));
        }
    }
}
