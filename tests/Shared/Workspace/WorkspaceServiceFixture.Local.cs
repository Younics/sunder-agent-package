extern alias LocalWorker;

using LocalWorker::Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tests;

internal static partial class WorkspaceServiceFixture
{
    internal static async Task<(AgentWorkspaceRecord Workspace, AgentWorkspaceBindingRecord Binding)> CreateLocalWorkspaceAsync(
        string root, LocalExecutionWorkspaceConfigService configService)
    {
        var workspace = CreateWorkspace(root);
        var binding = CreateBinding(workspace.WorkspaceId);
        await configService.SaveConfigAsync(binding.BindingId, new LocalExecutionWorkspaceConfig(null, []));
        return (workspace, binding);
    }
}
