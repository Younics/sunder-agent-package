using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Execution.Local;

internal static class LocalExecutionRuntimeOperations
{
    internal static readonly PackageRuntimeOperation<LocalExecutionOperationRequest, LocalExecutionOperationResponse> Execute =
        new("local-execution.presentation.v1");
}

internal enum LocalExecutionOperationKind
{
    GetSettings,
    SaveSettings,
    SaveShells,
    GetWorkspaceEditor,
    SaveWorkspaceEditor,
}

internal sealed record LocalExecutionOperationRequest(
    LocalExecutionOperationKind Kind,
    string? TimeoutSeconds = null,
    IReadOnlyList<LocalShellDefinition>? Shells = null,
    AgentWorkspaceEditorContext? EditorContext = null,
    AgentEditorSaveRequest? EditorSaveRequest = null);

internal sealed record LocalExecutionOperationResponse(
    string? TimeoutSeconds = null,
    IReadOnlyList<LocalShellDefinition>? Shells = null,
    IReadOnlyList<AgentEditorSection>? EditorSections = null,
    AgentEditorSaveResult? EditorSaveResult = null,
    string? Message = null);
