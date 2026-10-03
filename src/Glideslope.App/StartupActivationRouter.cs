using Glideslope.Core;

namespace Glideslope.App;

/// <summary>
/// Holds activation intents that arrive after the owner process has acquired
/// its instance lease but before the Avalonia coordinator is ready to handle
/// them. The broker waits for delivery so an early launch is never silently
/// acknowledged and lost.
/// The coordinator holds queued intents until startup has loaded settings and restored the layout.
/// </summary>
internal sealed class StartupActivationRouter
{
    private readonly object _sync = new();
    private readonly List<PendingActivation> _pending = [];
    private Func<ActivationIntent, Task>? _handler;

    public Task DispatchAsync(ActivationIntent intent)
    {
        Func<ActivationIntent, Task>? handler;
        lock (_sync)
        {
            handler = _handler;
            if (handler is null)
            {
                var pending = new PendingActivation(intent);
                _pending.Add(pending);
                return pending.Completion.Task;
            }
        }

        return handler(intent);
    }

    public void Attach(Func<ActivationIntent, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        PendingActivation[] pending;
        lock (_sync)
        {
            if (_handler is not null)
            {
                throw new InvalidOperationException("startup_activation_handler_already_attached");
            }

            _handler = handler;
            pending = _pending.ToArray();
            _pending.Clear();
        }

        foreach (var activation in pending)
        {
            _ = DeliverAsync(handler, activation);
        }
    }

    private static async Task DeliverAsync(
        Func<ActivationIntent, Task> handler,
        PendingActivation activation)
    {
        try
        {
            await handler(activation.Intent).ConfigureAwait(false);
            activation.Completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            activation.Completion.TrySetException(exception);
        }
    }

    private sealed record PendingActivation(ActivationIntent Intent)
    {
        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
