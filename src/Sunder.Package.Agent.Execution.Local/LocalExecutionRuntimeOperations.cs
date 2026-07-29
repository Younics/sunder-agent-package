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
    long? ExpectedShellCatalogRevision = null,
    AgentWorkspaceEditorContext? EditorContext = null,
    AgentEditorSaveRequest? EditorSaveRequest = null);

internal sealed record LocalExecutionOperationResponse(
    string? TimeoutSeconds = null,
    IReadOnlyList<LocalShellDefinition>? DetectedShells = null,
    IReadOnlyList<LocalShellDefinition>? CustomShells = null,
    long? ShellCatalogRevision = null,
    IReadOnlyList<AgentEditorSection>? EditorSections = null,
    AgentEditorSaveResult? EditorSaveResult = null,
    string? Message = null,
    bool Success = true,
    LocalExecutionOperationError? Error = null);

internal sealed record LocalExecutionOperationError(
    string Code,
    string Message,
    bool IsTransient = false,
    string? CorrelationId = null);
