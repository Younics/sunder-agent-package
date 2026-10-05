using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Workspace_Editor;

namespace Sunder.Package.Agent.Execution.Docker.Rpc;

[SunderRpcProvider(typeof(IWorkspaceEditorService), ProviderId = "docker.workspace.editor")]
public sealed partial class WorkspaceEditorProvider(IAgentWorkspaceEditorContributor editor) : IWorkspaceEditorService
{
    public ValueTask<Wire.AgentRpcIdentity> DescribeAsync(SunderRpcInvocationContext context, Wire.Unit request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new Wire.AgentRpcIdentity { Id = editor.ContributorId, DisplayName = editor.ContributorId }); }
    public ValueTask<bool> CanEditAsync(SunderRpcInvocationContext context, Wire.AgentWorkspaceEditorContext request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(editor.CanEdit(ExecutionValues.Convert<Domain.AgentWorkspaceEditorContext>(request))); }
    public async ValueTask<IReadOnlyList<Wire.AgentEditorSection>> GetSectionsAsync(SunderRpcInvocationContext context, Wire.AgentWorkspaceEditorContext request, CancellationToken token)
        => ExecutionValues.Convert<Wire.AgentEditorSection[]>(await editor.GetSectionsAsync(ExecutionValues.Convert<Domain.AgentWorkspaceEditorContext>(request), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentEditorSaveResult> SaveSectionAsync(SunderRpcInvocationContext context, Wire.WorkspaceEditorSaveRequest request, CancellationToken token)
        => ExecutionValues.Convert<Wire.AgentEditorSaveResult>(await editor.SaveSectionAsync(ExecutionValues.Convert<Domain.AgentWorkspaceEditorContext>(request.Context), ExecutionValues.Convert<Domain.AgentEditorSaveRequest>(request.Request), token).ConfigureAwait(false));
}
