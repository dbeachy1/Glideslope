namespace Glideslope.Providers;

/// <summary>What waiting for a CLI's pipe readers after the process ended found. <see cref="HeldOpen"/> is true
/// when a reader was still blocked after <see cref="CliProcessSupport.PipeDrainAfterExit"/> (a descendant still
/// held the pipe); <see cref="FaultTypeName"/> names the first reader failure other than a cancellation.</summary>
internal readonly record struct PipeDrainResult(bool HeldOpen, string? FaultTypeName);

/// <summary>
/// Shared pipe handling for provider CLI transports.
/// </summary>
internal static class CliProcessSupport
{
    /// <summary>
    /// Bounds pipe draining after the CLI exits, including when a descendant inherited a pipe.
    /// </summary>
    public static readonly TimeSpan PipeDrainAfterExit = TimeSpan.FromSeconds(2);

    /// <summary>Reads a pipe to its end, handing every chunk to <paramref name="scan"/> (which keeps only a
    /// count and a boolean, never the text).</summary>
    public static async Task DrainScanningAsync(StreamReader reader, PipeScan scan, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return;
            scan.Observe(buffer.AsSpan(0, read));
        }
    }

    /// <summary>
    /// Waits at most <see cref="PipeDrainAfterExit"/> for <paramref name="readers"/>, which use
    /// <paramref name="pipes"/>' token. If a reader remains blocked, cancels the readers and observes their
    /// eventual outcomes in the background. Never throws.
    /// </summary>
    public static async Task<PipeDrainResult> AwaitPipesAfterExitAsync(CancellationTokenSource pipes, params Task[] readers)
    {
        var settled = Task.WhenAll(readers.Select(IgnoreOutcome));
        var heldOpen = false;
        try
        {
            await settled.WaitAsync(PipeDrainAfterExit).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A descendant of the CLI still holds a pipe. The caller logs HeldOpen; the readers are stopped here.
            heldOpen = true;
            pipes.Cancel();
            foreach (var reader in readers) ObserveInBackground(reader);
        }

        string? fault = null;
        foreach (var reader in readers)
        {
            if (!reader.IsFaulted) continue;
            var exception = reader.Exception?.InnerException;
            if (exception is null or OperationCanceledException) continue;
            fault = exception.GetType().Name;
            break;
        }
        return new PipeDrainResult(heldOpen, fault);
    }

    private static Task IgnoreOutcome(Task task) =>
        task.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static void ObserveInBackground(Task task) =>
        _ = task.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}

/// <summary>
/// Counts what a CLI wrote to a pipe and remembers whether it contained any
/// of a few fixed markers (a sign-in URL, login wording). Only the count and one boolean are kept; the text is
/// discarded as it is read, apart from the few characters carried between reads so a marker split across two
/// reads is still found. Never logged.
/// </summary>
internal sealed class PipeScan
{
    private readonly string[] _markers;
    private readonly int _carryLength;
    private string _carry = string.Empty;
    private long _characters;
    private int _matched;

    public PipeScan(params string[] markers)
    {
        _markers = markers;
        _carryLength = markers.Length == 0 ? 0 : markers.Max(marker => marker.Length) - 1;
    }

    public long Characters => Interlocked.Read(ref _characters);
    public bool Matched => Volatile.Read(ref _matched) != 0;

    /// <summary>Called only by the single reader of the pipe.</summary>
    public void Observe(ReadOnlySpan<char> chunk)
    {
        Interlocked.Add(ref _characters, chunk.Length);
        if (_markers.Length == 0 || Matched) return;
        var window = string.Concat(_carry.AsSpan(), chunk);
        foreach (var marker in _markers)
        {
            if (!window.Contains(marker, StringComparison.OrdinalIgnoreCase)) continue;
            Volatile.Write(ref _matched, 1);
            _carry = string.Empty;
            return;
        }
        _carry = window.Length <= _carryLength ? window : window[^_carryLength..];
    }
}

/// <summary>Captures stdout up to a character limit, including partial output while a reader is running or after
/// cancellation. Only the character count is logged, never the captured text.</summary>
internal sealed class BoundedTextCapture(int maximum)
{
    private readonly object _gate = new();
    private readonly System.Text.StringBuilder _builder = new(Math.Min(maximum, 4096));
    private bool _tooLarge;

    public void Append(char[] buffer, int count)
    {
        lock (_gate)
        {
            var remaining = maximum - _builder.Length;
            if (remaining > 0) _builder.Append(buffer, 0, Math.Min(count, remaining));
            if (count > remaining) _tooLarge = true;
        }
    }

    public (string Text, bool TooLarge) Snapshot()
    {
        lock (_gate) return (_builder.ToString(), _tooLarge);
    }

    /// <summary>Reads <paramref name="reader"/> to its end into <paramref name="capture"/>.</summary>
    public static async Task ReadToEndAsync(StreamReader reader, BoundedTextCapture capture, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return;
            capture.Append(buffer, read);
        }
    }
}
