using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Workspace_Editor;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal static class AgentWorkspaceEditorBindings
{
    internal static AgentRpcService<IAgentWorkspaceEditorContributor> Service { get; } =
        AgentRpcService<IAgentWorkspaceEditorContributor>.WithProviderIdentity(Api.Contract.ContractId,
            static (rpc, provider) => new GeneratedWorkspaceEditorClient(rpc, provider));

    internal static ValueTask<bool> CanEditAsync(IAgentWorkspaceEditorContributor editor, AgentWorkspaceEditorContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return editor is GeneratedWorkspaceEditorClient client ? client.CanEditAsync(context, token) : ValueTask.FromResult(editor.CanEdit(context));
    }
}
