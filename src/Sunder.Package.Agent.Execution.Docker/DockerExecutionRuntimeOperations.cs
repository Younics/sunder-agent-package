using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Execution.Docker;

internal static class DockerExecutionRuntimeOperations
{
    internal static readonly PackageRuntimeOperation<DockerExecutionOperationRequest, DockerExecutionOperationResponse> Execute =
        new("docker-execution.presentation.v1");
}

internal enum DockerExecutionOperationKind
{
    GetSettings,
    SaveSettings,
    AddImage,
    DeleteImage,
    RefreshImage,
    RefreshImages,
    PullImage,
    TestDocker,
    GetWorkspaceEditor,
    SaveWorkspaceEditor,
}

internal sealed record DockerExecutionOperationRequest(
    DockerExecutionOperationKind Kind,
    string? TimeoutSeconds = null,
    string? DockerCliPath = null,
    string? ImageReference = null,
    AgentWorkspaceEditorContext? EditorContext = null,
    AgentEditorSaveRequest? EditorSaveRequest = null);

internal sealed record DockerExecutionOperationResponse(
    string? TimeoutSeconds = null,
    string? DockerCliPath = null,
    IReadOnlyList<DockerImageDefinition>? Images = null,
    IReadOnlyList<AgentEditorSection>? EditorSections = null,
    AgentEditorSaveResult? EditorSaveResult = null,
    bool Success = true,
    string? Message = null,
    long? CatalogRevision = null,
    DockerExecutionOperationError? Error = null);

internal sealed record DockerExecutionOperationError(
    string Code,
    string Message,
    bool IsTransient,
    string CorrelationId);
