using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Agent.Subagents.Runtime;

internal sealed class SubagentsActivation(SubagentStore store, ISubagentRuntimeBindings runtime, SubagentRuntimeChangeStream changes)
    : ISunderWorkerActivationService, IAsyncDisposable
{
    public async ValueTask ActivateAsync(SunderWorkerContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        store.Activate();
        await runtime.StartAsync(cancellationToken).ConfigureAwait(false);
        // Catalog adapters still expose synchronous snapshots. Keep the Worker
        // reader free to deliver their replies while initial subscriptions attach.
        await Task.Run(changes.RefreshAfterActivation, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => runtime.DisposeAsync();
}
