using System.IO;

namespace Glideslope.NativeProof;

/// <summary>One parsed line of glideslope.log (format from src/Glideslope.Core/Diagnostics.cs
/// FileDiagnosticSink.FormatLine: "{timestamp:O} code={code} provider={id} status={status} durationMs={ms}",
/// with provider/status/durationMs omitted when null).</summary>
internal sealed record LogLine(string Raw, string Code, string? ProviderId, string? Status);

/// <summary>
/// Watches the app log for events asserted by the scenarios and reads only lines appended during this run.
/// A named auto-reset event signals each completed log write. The hang watchdog applies only when no log
/// line arrives; any line restarts the wait.
/// </summary>
internal sealed class ProofLog
{
    /// <summary>The only time limit WaitForLine has left: a single wait with no signal at all for this
    /// long means the app has stopped logging entirely, not that the awaited line is merely late. Any log
    /// line at all - not just the one being waited for - re-arms this by restarting the wait, so a busy
    /// machine that is still logging something never trips it.</summary>
    private static readonly TimeSpan LogSilenceHangWatchdog = TimeSpan.FromSeconds(60);

    private readonly string _path;
    private readonly EventWaitHandle _logEvent;
    private readonly Action<string> _harnessLog;

    public ProofLog(string path, EventWaitHandle logEvent, Action<string> harnessLog)
    {
        _path = path;
        _logEvent = logEvent;
        _harnessLog = harnessLog;
    }

    /// <summary>Byte length of the log file at the moment this is called; pass the result to
    /// <see cref="WaitForLine"/> as the starting offset so only lines written after that moment count.</summary>
    public long CurrentLength() => File.Exists(_path) ? new FileInfo(_path).Length : 0;

    /// <summary>Waits for the first line at or after <paramref name="sinceOffset"/> whose parsed fields
    /// satisfy <paramref name="predicate"/>. Reads whatever is already on disk first; if nothing matches,
    /// waits for the next afterWrite signal and reads again, repeating until a line matches or
    /// <see cref="LogSilenceHangWatchdog"/> passes with no signal at all. Returns null only in that second
    /// case; the caller then reports <see cref="TailSince"/> as the "nothing happened" evidence (the window
    /// design's own convention is to name that failure app_silent_60s).</summary>
    public LogLine? WaitForLine(long sinceOffset, Func<LogLine, bool> predicate)
    {
        while (true)
        {
            foreach (var line in ReadLinesSince(sinceOffset))
            {
                if (predicate(line))
                {
                    _harnessLog($"log_wait_ended reason=signal sinceOffset={sinceOffset} code={line.Code} providerId={line.ProviderId} status={line.Status}");
                    return line;
                }
            }

            if (!_logEvent.WaitOne(LogSilenceHangWatchdog))
            {
                _harnessLog($"log_wait_ended reason=app_silent_60s sinceOffset={sinceOffset}");
                return null;
            }
        }
    }

    /// <summary>Every parsed line written at or after <paramref name="sinceOffset"/>, for evidence in a
    /// failure report.</summary>
    public IReadOnlyList<LogLine> TailSince(long sinceOffset) => ReadLinesSince(sinceOffset).ToList();

    private IEnumerable<LogLine> ReadLinesSince(long sinceOffset)
    {
        if (!File.Exists(_path)) yield break;
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (sinceOffset > stream.Length) yield break;
        stream.Seek(sinceOffset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        string? raw;
        while ((raw = reader.ReadLine()) is not null)
        {
            if (Parse(raw) is { } parsed) yield return parsed;
        }
    }

    private static LogLine? Parse(string raw)
    {
        var codeIndex = raw.IndexOf("code=", StringComparison.Ordinal);
        if (codeIndex < 0) return null;
        var afterCode = raw[(codeIndex + "code=".Length)..];
        var code = TakeToken(afterCode);
        var providerId = ExtractField(raw, "provider=");
        var status = ExtractField(raw, "status=");
        return new LogLine(raw, code, providerId, status);
    }

    private static string? ExtractField(string raw, string key)
    {
        var index = raw.IndexOf(key, StringComparison.Ordinal);
        return index < 0 ? null : TakeToken(raw[(index + key.Length)..]);
    }

    private static string TakeToken(string remainder)
    {
        var spaceIndex = remainder.IndexOf(' ');
        return spaceIndex < 0 ? remainder : remainder[..spaceIndex];
    }
}
