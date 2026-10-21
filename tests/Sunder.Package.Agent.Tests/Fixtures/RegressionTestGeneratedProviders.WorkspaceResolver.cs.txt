using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using IWorkspaceExecutionResolverService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Workspace_Execution_Resolver.IWorkspaceExecutionResolverService;
using Sunder.Sdk.Services;

namespace RegressionTestGeneratedProviders;

[SunderServiceProvider(typeof(IWorkspaceExecutionResolverService), ProviderId = "fixture.regression.workspace.resolver")]
public sealed partial class WorkspaceResolverProvider(Func<string, Type, object?[], CancellationToken, object> invoke) : IWorkspaceExecutionResolverService
{
    private ValueTask<T> Reply<T>(string operation, object?[] arguments, CancellationToken token) => (ValueTask<T>)invoke(operation, typeof(T), arguments, token);
    public ValueTask<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(CancellationToken token = default)
        => Reply<IReadOnlyList<AgentWorkspaceRecord>>(nameof(ListWorkspacesAsync), [], token);
    public ValueTask<AgentWorkspaceExecutionResolution> ResolveAsync(string workspaceId, CancellationToken token = default)
        => Reply<AgentWorkspaceExecutionResolution>(nameof(ResolveAsync), [workspaceId], token);
}
