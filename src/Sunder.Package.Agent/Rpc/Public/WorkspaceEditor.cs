using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderService(SunderServiceExposure.Public, ContractId = "sunder.agent.workspace.editor", ServiceId = "workspace-editor", IncludeSchemaDialect = true)]
[SunderAppUse(Actions = ["discover", "invoke"])]
public interface WorkspaceEditorService
{
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<AgentServiceIdentity> DescribeAsync(CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<bool> CanEditAsync(AgentWorkspaceEditorContext context, CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(AgentWorkspaceEditorContext context, CancellationToken cancellationToken = default);
    ValueTask<AgentEditorSaveResult> SaveSectionAsync(AgentWorkspaceEditorContext context, AgentEditorSaveRequest request, CancellationToken cancellationToken = default);
}
