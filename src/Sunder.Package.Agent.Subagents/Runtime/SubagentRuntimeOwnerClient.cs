namespace Sunder.Package.Agent.Subagents.Runtime;

internal sealed class SubagentRuntimeOwnerClient(SubagentRuntimeHandler handler, SubagentRuntimeChangeStream changes) : ISubagentOwnerClient
{
    public async ValueTask<SubagentProjection> QueryAsync(SubagentQuery request, CancellationToken token = default)
    {
        try { return await handler.HandleAsync(request, token).ConfigureAwait(false); }
        catch (SubagentOwnerOperationException error) { throw SubagentOwnerJson.Map(error); }
    }

    public async ValueTask<SubagentProjection> CommandAsync(SubagentCommand request, CancellationToken token = default)
    {
        try { return await handler.HandleAsync(request, token).ConfigureAwait(false); }
        catch (SubagentOwnerOperationException error) { throw SubagentOwnerJson.Map(error); }
    }

    public IAsyncEnumerable<SubagentChanged> SubscribeChangesAsync(SubagentChangeSubscription request, CancellationToken token = default)
        => changes.SubscribeAsync(request, token);
}
