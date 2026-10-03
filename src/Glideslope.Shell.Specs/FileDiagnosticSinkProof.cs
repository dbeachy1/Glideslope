using Glideslope.Core;

namespace Glideslope.Shell.Specs;

/// <summary>
/// The file-backed diagnostic sink exposes events from a normal launch without a console. This proof covers
/// one line per event, size-based rotation, concurrent writes, and containment of I/O failures.
/// </summary>
internal static class FileDiagnosticSinkProof
{
    public static void Run()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"Glideslope.FileDiagnosticSinkProof-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            WritesOneLineWithExpectedFields(tempRoot);
            RotatesAtTheSizeCapKeepingOneBackup(tempRoot);
            SwallowsAndCountsIoFailuresInsteadOfThrowing(tempRoot);
            ConcurrentWritesAreNotLost(tempRoot);
            AfterWriteFiresOncePerSuccessfulLineNotOnFailure(tempRoot);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static void WritesOneLineWithExpectedFields(string tempRoot)
    {
        var path = Path.Combine(tempRoot, "basic", "glideslope.log");
        var sink = new FileDiagnosticSink(path);
        sink.Record(new DiagnosticEvent("layout_gesture_started", "codex", "resize"));
        sink.Record(new DiagnosticEvent("layout_screen_snap_evaluated", "codex", "succeeded=True,moved=True", 12));

        Assert(File.Exists(path), "the log file is created on first write");
        var lines = File.ReadAllLines(path);
        Assert(lines.Length == 2, "one line per recorded event");
        Assert(lines[0].Contains("code=layout_gesture_started") && lines[0].Contains("provider=codex") &&
               lines[0].Contains("status=resize"),
            "the line carries the event's code, provider, and status");
        Assert(lines[1].Contains("durationMs=12"), "a supplied duration is written");
        Assert(DateTimeOffset.TryParse(lines[0].Split(' ')[0], out var stamp) && stamp.Offset == TimeSpan.Zero,
            "each line starts with a parseable UTC timestamp");
        Assert(sink.IoFailureCount == 0, "no IO failures for an ordinary writable path");
    }

    private static void RotatesAtTheSizeCapKeepingOneBackup(string tempRoot)
    {
        var path = Path.Combine(tempRoot, "rotation", "glideslope.log");
        var sink = new FileDiagnosticSink(path);
        // A single line is roughly 90-100 bytes; well over 1 MB / 100 clears the ~1 MB cap.
        for (var i = 0; i < 12000; i++)
            sink.Record(new DiagnosticEvent("layout_screen_snap_evaluated", "codex", $"filler_line_{i:D8}"));

        Assert(new FileInfo(path).Length < 1_000_000, "the live file itself never grows past the rotation cap");
        var backupPath = path + ".1";
        Assert(File.Exists(backupPath), "rotation keeps exactly one backup file");
        sink.Record(new DiagnosticEvent("layout_gesture_ended", "codex", "idle_timeout"));
        Assert(File.ReadAllLines(path).Any(line => line.Contains("layout_gesture_ended")),
            "writes after rotation land in the fresh, now-small live file");
        Assert(sink.IoFailureCount == 0, "rotation itself is not counted as a failure");
    }

    private static void SwallowsAndCountsIoFailuresInsteadOfThrowing(string tempRoot)
    {
        // A file sitting where the log's own parent directory needs to be makes every
        // Directory.CreateDirectory call for that path fail with IOException.
        var blockingFile = Path.Combine(tempRoot, "blocked-parent");
        File.WriteAllText(blockingFile, "owned-test-file");
        var path = Path.Combine(blockingFile, "glideslope.log");
        var sink = new FileDiagnosticSink(path);

        sink.Record(new DiagnosticEvent("layout_gesture_started", "codex", "move"));
        sink.Record(new DiagnosticEvent("layout_gesture_ended", "codex", "idle_timeout"));

        Assert(sink.IoFailureCount == 2, "every write against an unwritable log path is swallowed and counted, never thrown");
        Assert(File.ReadAllText(blockingFile) == "owned-test-file", "the failure never touches the file blocking the log directory");
    }

    private static void ConcurrentWritesAreNotLost(string tempRoot)
    {
        var path = Path.Combine(tempRoot, "concurrent", "glideslope.log");
        var sink = new FileDiagnosticSink(path);
        const int threads = 8;
        const int perThread = 50;
        Parallel.For(0, threads, t =>
        {
            for (var i = 0; i < perThread; i++)
                sink.Record(new DiagnosticEvent("layout_gesture_started", $"thread{t}", $"call{i}"));
        });

        Assert(File.ReadAllLines(path).Length == threads * perThread,
            "every concurrent write lands as its own line; the write lock never drops or corrupts one");
        Assert(sink.IoFailureCount == 0, "ordinary concurrent writes never count as IO failures");
    }

    /// <summary>FileDiagnosticSink's afterWrite callback signals a proof-root harness after each successful
    /// log append. It is called exactly once per
    /// line that is actually written, inside the write lock and only after the append has returned, and
    /// never called for a write that throws (see SwallowsAndCountsIoFailuresInsteadOfThrowing above for
    /// the fixture that makes every write to this path fail).</summary>
    private static void AfterWriteFiresOncePerSuccessfulLineNotOnFailure(string tempRoot)
    {
        var path = Path.Combine(tempRoot, "after-write", "glideslope.log");
        var successfulCallCount = 0;
        var sink = new FileDiagnosticSink(path, () => Interlocked.Increment(ref successfulCallCount));

        sink.Record(new DiagnosticEvent("layout_gesture_started", "codex", "move"));
        sink.Record(new DiagnosticEvent("layout_gesture_ended", "codex", "idle_timeout"));
        Assert(File.ReadAllLines(path).Length == 2, "both lines were actually written");
        Assert(successfulCallCount == 2, "afterWrite fires exactly once per line successfully written");
        Assert(sink.IoFailureCount == 0, "no IO failures for an ordinary writable path");

        var blockingFile = Path.Combine(tempRoot, "after-write-blocked-parent");
        File.WriteAllText(blockingFile, "owned-test-file");
        var failingPath = Path.Combine(blockingFile, "glideslope.log");
        var failureCallCount = 0;
        var failingSink = new FileDiagnosticSink(failingPath, () => Interlocked.Increment(ref failureCallCount));
        failingSink.Record(new DiagnosticEvent("layout_gesture_started", "codex", "move"));
        Assert(failingSink.IoFailureCount == 1, "the write against an unwritable log path still fails as before");
        Assert(failureCallCount == 0, "afterWrite is not called when the write fails");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {message}");
    }
}
