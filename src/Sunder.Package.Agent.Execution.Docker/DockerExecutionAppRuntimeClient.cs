using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Execution.Docker;

internal sealed class DockerExecutionAppRuntimeClient(IPackageRuntimeClient client)
{
    internal ValueTask<DockerExecutionOperationResponse> InvokeAsync(
        DockerExecutionOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!client.IsAvailable)
        {
            return ValueTask.FromException<DockerExecutionOperationResponse>(
                new InvalidOperationException("Docker execution Runtime is unavailable."));
        }

        return client.InvokeAsync(DockerExecutionRuntimeOperations.Execute, request, cancellationToken);
    }
}

internal sealed class DockerExecutionWorkspaceEditorPresentationContributor(DockerExecutionAppRuntimeClient runtimeClient)
    : IAgentWorkspaceEditorContributor
{
    public string ContributorId => "sunder.package.agent.execution.docker.workspace-editor-presentation";

    public bool CanEdit(AgentWorkspaceEditorContext context)
        => string.Equals(context.TargetId, "docker", StringComparison.OrdinalIgnoreCase);

    public async ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(
        AgentWorkspaceEditorContext context,
        CancellationToken cancellationToken = default)
        => (await runtimeClient.InvokeAsync(
            new DockerExecutionOperationRequest(DockerExecutionOperationKind.GetWorkspaceEditor, EditorContext: context),
            cancellationToken)).EditorSections ?? [];

    public async ValueTask<AgentEditorSaveResult> SaveSectionAsync(
        AgentWorkspaceEditorContext context,
        AgentEditorSaveRequest request,
        CancellationToken cancellationToken = default)
        => (await runtimeClient.InvokeAsync(
            new DockerExecutionOperationRequest(
                DockerExecutionOperationKind.SaveWorkspaceEditor,
                EditorContext: context,
                EditorSaveRequest: request),
            cancellationToken)).EditorSaveResult ?? AgentEditorSaveResult.Failed("Runtime did not return a save result.");
}
