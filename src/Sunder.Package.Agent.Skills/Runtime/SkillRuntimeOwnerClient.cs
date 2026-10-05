namespace Sunder.Package.Agent.Skills.Runtime;

internal sealed class SkillRuntimeOwnerClient(SkillRuntimeHandler handler, SkillRuntimeChangeStream changes) : ISkillOwnerClient
{
    public async ValueTask<SkillProjection> QueryAsync(CancellationToken token = default)
    {
        try { return await handler.HandleAsync(new SkillQuery(), token).ConfigureAwait(false); }
        catch (SkillOwnerOperationException error) { throw OwnerJson.Map(error); }
    }

    public async ValueTask<SkillProjection> CommandAsync(SkillCommand command, CancellationToken token = default)
    {
        try { return await handler.HandleAsync(command, token).ConfigureAwait(false); }
        catch (SkillOwnerOperationException error) { throw OwnerJson.Map(error); }
    }

    public IAsyncEnumerable<SkillChanged> SubscribeChangesAsync(CancellationToken token = default)
        => changes.SubscribeAsync(new SkillChangeSubscription(), token);
}
