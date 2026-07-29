namespace Sunder.Package.Agent.Services;

internal static class AgentRuntimeWorkerCancellation
{
    internal static Task SignalAsync(CancellationTokenSource? lifetime)
    {
        if (lifetime is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            return lifetime.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            return Task.CompletedTask;
        }
    }
}
