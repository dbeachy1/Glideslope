using System.Threading;

namespace Glideslope.Core;

public sealed record DiagnosticEvent(string Code, string? ProviderId = null, string? Status = null, long? DurationMilliseconds = null);
public interface IDiagnosticSink { void Record(DiagnosticEvent diagnostic); }
public sealed class ConsoleDiagnosticSink : IDiagnosticSink
{
    public void Record(DiagnosticEvent diagnostic)
    {
        var provider = diagnostic.ProviderId is null ? "" : $" provider={diagnostic.ProviderId}";
        var status = diagnostic.Status is null ? "" : $" status={diagnostic.Status}";
        var duration = diagnostic.DurationMilliseconds is null ? "" : $" durationMs={diagnostic.DurationMilliseconds}";
        Console.Error.WriteLine($"glideslope shell code={diagnostic.Code}{provider}{status}{duration}");
    }
}
public sealed class NullDiagnosticSink : IDiagnosticSink { public void Record(DiagnosticEvent diagnostic) { } }

/// <summary>
/// A normal launch has no console, so the existing ConsoleDiagnosticSink
/// is invisible for a real user session. This sink writes the same events to a per-user log file
/// (one line per event: UTC time, code, provider, status, duration) so a live drag/snap/dock report
/// can be diagnosed after the fact. It rotates at about 1 MB into a single ".1" backup, is safe to
/// call from multiple threads, and never lets a write failure reach the caller: an IO problem is
/// swallowed and counted so the app keeps running with only diagnostics degraded.
/// </summary>
public sealed class FileDiagnosticSink : IDiagnosticSink
{
    /// <summary>Rotate once the live file reaches roughly this size; kept generous since a line is short.</summary>
    private const long MaximumBytesBeforeRotation = 1_000_000;
    private readonly string _path;
    private readonly string _rotatedPath;
    private readonly object _writeGate = new();
    private readonly Action? _afterWrite;
    private int _ioFailureCount;

    public FileDiagnosticSink(string path, Action? afterWrite = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _rotatedPath = path + ".1";
        // A harness
        // running the app under --proof-root needs to know the instant a new line lands in glideslope.log,
        // without polling the file on a timer. afterWrite is that signal - see Record below for exactly
        // when it fires.
        _afterWrite = afterWrite;
    }

    /// <summary>Count of swallowed write/rotation failures, exposed so a caller can notice a persistently unwritable log.</summary>
    public int IoFailureCount => Volatile.Read(ref _ioFailureCount);

    public void Record(DiagnosticEvent diagnostic)
    {
        var line = FormatLine(diagnostic);
        lock (_writeGate)
        {
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                RotateIfNeededLocked();
                File.AppendAllText(_path, line + Environment.NewLine);
                // Called once per line, inside this write lock and only after the append above has
                // actually returned, so a waiter that wakes on this
                // signal and re-reads the file is guaranteed to see the line that triggered it. Not called
                // when the write throws below (the catch blocks return before reaching this line).
                _afterWrite?.Invoke();
            }
            catch (IOException) { Interlocked.Increment(ref _ioFailureCount); }
            catch (UnauthorizedAccessException) { Interlocked.Increment(ref _ioFailureCount); }
        }
    }

    private void RotateIfNeededLocked()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length < MaximumBytesBeforeRotation) return;
            File.Copy(_path, _rotatedPath, overwrite: true);
            File.Delete(_path);
        }
        catch (IOException) { Interlocked.Increment(ref _ioFailureCount); }
        catch (UnauthorizedAccessException) { Interlocked.Increment(ref _ioFailureCount); }
    }

    private static string FormatLine(DiagnosticEvent diagnostic)
    {
        var provider = diagnostic.ProviderId is null ? "" : $" provider={diagnostic.ProviderId}";
        var status = diagnostic.Status is null ? "" : $" status={diagnostic.Status}";
        var duration = diagnostic.DurationMilliseconds is null ? "" : $" durationMs={diagnostic.DurationMilliseconds}";
        return $"{DateTimeOffset.UtcNow:O} code={diagnostic.Code}{provider}{status}{duration}";
    }
}

/// <summary>Fans one diagnostic event out to every wrapped sink so a file sink can sit beside the console one.</summary>
public sealed class CompositeDiagnosticSink(params IDiagnosticSink[] sinks) : IDiagnosticSink
{
    public void Record(DiagnosticEvent diagnostic)
    {
        foreach (var sink in sinks) sink.Record(diagnostic);
    }
}
