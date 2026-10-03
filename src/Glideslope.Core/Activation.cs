namespace Glideslope.Core;

public enum ActivationIntent { Manual, Autostart }
public static class ActivationIntentParser
{
    public static ActivationIntent Parse(IReadOnlyList<string> args) => args.Any(arg => string.Equals(arg, "--autostart", StringComparison.OrdinalIgnoreCase)) ? ActivationIntent.Autostart : ActivationIntent.Manual;
}
public enum InstanceRole { Owner, Forwarded, ActivationFailed }
public sealed record InstanceAcquireResult(InstanceRole Role, string? IssueCode = null);
public interface IInstanceBroker : IAsyncDisposable
{
    Task<InstanceAcquireResult> AcquireAsync(ActivationIntent intent, Func<ActivationIntent, Task> onActivation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Call when the shell starts shutting down, and
    /// dispose the broker last, after the monitoring session has flushed. From this call on, a second launch
    /// is answered "closing" instead of being routed into the shell; it waits for this process to release
    /// the single-instance lock in DisposeAsync and then starts normally, so it never runs beside a process
    /// that still has settings and history open.
    /// </summary>
    void BeginClosing();
}

public enum UserCloseDecision { DisableProvider, ExitKeepSelection }

public static class UserClosePolicy
{
    public static UserCloseDecision Decide(int visibleWindowCount) =>
        visibleWindowCount <= 1 ? UserCloseDecision.ExitKeepSelection : UserCloseDecision.DisableProvider;
}
