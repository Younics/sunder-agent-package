using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Execution.Local;

internal sealed class LocalExecutionAppRuntimeClient(IPackageRuntimeClient client)
{
    internal ValueTask<LocalExecutionOperationResponse> InvokeAsync(
        LocalExecutionOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!client.IsAvailable)
        {
            return ValueTask.FromException<LocalExecutionOperationResponse>(
                new PackageRuntimeInvocationException(
                    "runtime.v1.unavailable",
                    isTransient: true,
                    statusCode: 503));
        }

        return client.InvokeAsync(LocalExecutionRuntimeOperations.Execute, request, cancellationToken);
    }
}

internal sealed class LocalExecutionWorkspaceEditorPresentationContributor(LocalExecutionAppRuntimeClient runtimeClient)
    : IAgentWorkspaceEditorContributor
{
    public string ContributorId => "sunder.package.agent.execution.local.workspace-editor-presentation";

    public bool CanEdit(AgentWorkspaceEditorContext context)
        => string.Equals(context.TargetId, "local", StringComparison.OrdinalIgnoreCase);

    public async ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(
        AgentWorkspaceEditorContext context,
        CancellationToken cancellationToken = default)
        => (await runtimeClient.InvokeAsync(
            new LocalExecutionOperationRequest(LocalExecutionOperationKind.GetWorkspaceEditor, EditorContext: context),
            cancellationToken)).EditorSections ?? [];

    public async ValueTask<AgentEditorSaveResult> SaveSectionAsync(
        AgentWorkspaceEditorContext context,
        AgentEditorSaveRequest request,
        CancellationToken cancellationToken = default)
        => (await runtimeClient.InvokeAsync(
            new LocalExecutionOperationRequest(
                LocalExecutionOperationKind.SaveWorkspaceEditor,
                EditorContext: context,
                EditorSaveRequest: request),
            cancellationToken)).EditorSaveResult ?? AgentEditorSaveResult.Failed("Runtime did not return a save result.");
}
