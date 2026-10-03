using System.Text;
using System.Text.Json;
using Glideslope.Core;
using Glideslope.Domain;

namespace Glideslope.Providers;

/// <summary>A Claude CLI latch recorded by this or an earlier launch. <see cref="Reason"/> is "spent_turn" or
/// "unproven_timeout".</summary>
public sealed record ClaudeCliPersistedLatch(string Reason, DateTimeOffset LatchedAtUtc);

/// <summary>
/// Records the latch that stops the Claude CLI after a run spent a model turn or after consecutive timeouts
/// could not prove zero spend. The latch is stored in
/// {data}\provider-state\claude-cli-latch.json, keyed by the CLI build (CliVersionKey: a hash of the launched
/// file's resolved path, size and modification time, plus package.json for an npm install; no extra CLI run).
/// A different build clears it. Manual Refresh clears an "unproven_timeout" latch, but never a "spent_turn"
/// latch. The file holds the key hash, reason code, and time, nothing
/// about the account.
/// </summary>
internal sealed class ClaudeCliLatchStore
{
    internal const string FileName = "claude-cli-latch.json";
    private const int FileFormat = 1;
    private const int MaxFileBytes = 4 * 1024;
    private readonly string _path;
    private readonly IProviderDiagnosticSink _diagnostics;
    private readonly TimeProvider _timeProvider;

    public ClaudeCliLatchStore(string path, IProviderDiagnosticSink diagnostics, TimeProvider? timeProvider = null)
    {
        _path = Path.GetFullPath(path);
        _diagnostics = diagnostics;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>{data}\provider-state\claude-cli-latch.json under the app's per-user data root, or null when the
    /// platform gives no rooted location.</summary>
    public static string? DefaultPath(IProviderDiagnosticSink? diagnostics)
    {
        try
        {
            var data = new DefaultUserPathProvider().Get().DataDirectory;
            if (Path.IsPathFullyQualified(data)) return Path.Combine(data, "provider-state", FileName);
            diagnostics?.Record(new ProviderDiagnostic("claude_latch_store_unavailable", ProviderIds.Claude, "not_rooted", 0));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException
                                              or PlatformNotSupportedException or System.Security.SecurityException)
        {
            diagnostics?.Record(new ProviderDiagnostic("claude_latch_store_unavailable", ProviderIds.Claude, exception.GetType().Name, 0));
        }
        return null;
    }

    /// <summary>Returns the latch for <paramref name="versionKey"/>, or null. A latch for a different identified
    /// build is deleted; when the current build cannot be identified, preserve any recorded latch. An unreadable
    /// file is logged and ignored while the in-memory latch still guards the current session.</summary>
    public ClaudeCliPersistedLatch? Read(string? versionKey)
    {
        string text;
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists) return null;
            if (info.Length > MaxFileBytes)
            {
                Record("claude_latch_file_unreadable", "too_large");
                return null;
            }
            text = File.ReadAllText(_path, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Record("claude_latch_file_unreadable", exception.GetType().Name);
            return null;
        }

        string? storedKey = null;
        string? reason = null;
        var latchedAt = DateTimeOffset.MinValue;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                storedKey = TryString(root, "versionKey");
                reason = TryString(root, "reason");
                if (root.TryGetProperty("latchedAtUtc", out var at) && at.ValueKind == JsonValueKind.String &&
                    at.TryGetDateTimeOffset(out var parsed))
                    latchedAt = parsed;
            }
        }
        catch (JsonException exception)
        {
            Record("claude_latch_file_unreadable", exception.GetType().Name);
            return null;
        }
        if (storedKey is null || reason is null)
        {
            Record("claude_latch_file_unreadable", "shape");
            return null;
        }

        if (versionKey is null)
        {
            Record("claude_latch_honored", "version_key_unavailable");
            return new ClaudeCliPersistedLatch(reason, latchedAt);
        }
        if (!string.Equals(storedKey, versionKey, StringComparison.Ordinal))
        {
            Delete($"version_changed,was={CliVersionKey.Short(storedKey)},now={CliVersionKey.Short(versionKey)}");
            return null;
        }
        return new ClaudeCliPersistedLatch(reason, latchedAt);
    }

    public void Write(string versionKey, string reason)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("format", FileFormat);
                writer.WriteString("versionKey", versionKey);
                writer.WriteString("reason", reason);
                writer.WriteString("latchedAtUtc", _timeProvider.GetUtcNow());
                writer.WriteEndObject();
            }
            File.Move(temporary, _path, overwrite: true);
            Record("claude_latch_persisted", $"reason={reason},version={CliVersionKey.Short(versionKey)}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Record("claude_latch_persist_failed", exception.GetType().Name);
        }
    }

    /// <summary>Deletes the latch file (a manual Refresh clearing an unproven_timeout latch).</summary>
    public void Clear(string reason) => Delete(reason);

    private void Delete(string reason)
    {
        try
        {
            File.Delete(_path);
            Record("claude_latch_cleared", reason);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Record("claude_latch_clear_failed", exception.GetType().Name);
        }
    }

    private static string? TryString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private void Record(string code, string status) =>
        _diagnostics.Record(new ProviderDiagnostic(code, ProviderIds.Claude, status, 0));
}
