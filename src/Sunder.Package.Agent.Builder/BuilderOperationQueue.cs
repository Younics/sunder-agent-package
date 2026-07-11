using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Builder;

public sealed class BuilderOperationQueue(IBackgroundProcessQueue backgroundProcesses)
{
    public const string GroupKey = "sunder-package-builder";

    private readonly SemaphoreSlim _gate = new(1, 1);

    public BackgroundProcessSnapshot Enqueue(
        string title,
        Func<BackgroundProcessContext, Task> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(operation);
        return backgroundProcesses.Enqueue(new BackgroundProcessRequest(
            title,
            GroupKey,
            BackgroundProcessIndicator.Main,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            CanCancel: true,
            context => RunAsync(() => operation(context), context.CancellationToken)));
    }

    public async Task RunAsync(Func<Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await operation().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
