using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Agent.Subagents.Runtime;

internal sealed class SubagentsActivation(SubagentStore store, SubagentEditorCapabilityCatalog capabilities, SubagentRuntimeChangeStream changes)
    : ISunderWorkerActivationService, IAsyncDisposable
{
    public async ValueTask ActivateAsync(SunderWorkerContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        store.Activate();
        await capabilities.StartAsync(cancellationToken).ConfigureAwait(false);
        await changes.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    { try { await changes.DisposeAsync().ConfigureAwait(false); } finally { await capabilities.DisposeAsync().ConfigureAwait(false); } }
}
