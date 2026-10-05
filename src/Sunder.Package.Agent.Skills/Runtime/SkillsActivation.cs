using Sunder.Package.Agent.Skills.Services;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Agent.Skills.Runtime;

internal sealed class SkillsActivation(SkillStore store, SkillRuntimeHandler handler, ISkillsRuntimeBindings runtime)
    : ISunderWorkerActivationService, IAsyncDisposable
{
    public async ValueTask ActivateAsync(SunderWorkerContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        store.Activate();
        await handler.OrphanedTransferCleanup.WaitAsync(cancellationToken).ConfigureAwait(false);
        await runtime.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try { await runtime.DisposeAsync().ConfigureAwait(false); }
        finally { await handler.StartedTransferCleanup.ConfigureAwait(false); }
    }
}
