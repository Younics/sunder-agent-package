using System.Runtime.CompilerServices;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.TestSupport;

/// <summary>Pauses catalog event delivery, not provider retirement or RPC authority.</summary>
public sealed class PausedServiceProviderEvents<TService>(IServiceProviders<TService> inner) : IServiceProviders<TService>
    where TService : class
{
    private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _watching = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Watching => _watching.Task;

    public void Resume() => _resume.TrySetResult();

    public ValueTask<ServiceProviderCatalog<TService>> ListAsync(CancellationToken cancellationToken = default)
        => inner.ListAsync(cancellationToken);

    public async IAsyncEnumerable<ServiceProviderCatalogEvent<TService>> WatchAsync(long afterRevision, long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _watching.TrySetResult();
        await foreach (var change in inner.WatchAsync(afterRevision, afterSequence, cancellationToken).ConfigureAwait(false))
        {
            await _resume.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield return change;
        }
    }
}
