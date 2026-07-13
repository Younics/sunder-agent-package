using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentWorkspacesViewModel
{
    [RelayCommand(CanExecute = nameof(CanSaveWorkspace))]
    private async Task SaveWorkspace()
    {
        var selectedWorkspace = SelectedWorkspace;
        if (selectedWorkspace is null)
        {
            return;
        }

        var operation = BeginOperation(AgentWorkspaceOperation.Save);
        try
        {
            if (!ValidateWorkspacePathsAndDocuments(out var validationMessage))
            {
                SetStatus(validationMessage, AgentWorkspaceStatusKind.Error);
                return;
            }

            var snapshot = new WorkspaceSaveSnapshot(
                selectedWorkspace,
                DisplayName,
                Description,
                WorkspacePaths.Select((path, index) =>
                    path.ToRecord(selectedWorkspace.WorkspaceId, index)).ToArray(),
                WorkspaceDocuments.Select((document, index) =>
                    document.ToRecord(selectedWorkspace.WorkspaceId, index)).ToArray(),
                SelectedExecutionTarget is { IsUnconfigured: false } target
                    ? target.TargetId
                    : null,
                EditorSections.ToArray());
            var editorSaveResult = await SaveEditorSectionsAsync(snapshot.EditorSections);
            if (!editorSaveResult.Success)
            {
                SetStatus(editorSaveResult.Message, AgentWorkspaceStatusKind.Error);
                return;
            }

            AgentExecutionTargetWarmupResult? warmupResult = null;
            _suppressWorkspaceChangeNotifications = true;
            try
            {
                _workspaceService.SaveWorkspaceAggregate(
                    snapshot.Workspace.WorkspaceId,
                    snapshot.DisplayName,
                    snapshot.Description,
                    snapshot.Paths,
                    snapshot.Documents,
                    snapshot.ExecutionTargetId);
                if (snapshot.ExecutionTargetId is not null)
                {
                    var warmupWorkspace = _workspaceService.GetWorkspace(snapshot.Workspace.WorkspaceId)
                        ?? snapshot.Workspace;
                    SetStatus("Workspace saved. Preparing execution target...", AgentWorkspaceStatusKind.Warning);
                    warmupResult = await _executionGateway.WarmWorkspaceAsync(
                        warmupWorkspace,
                        _lifetimeCancellation.Token);
                }
            }
            finally
            {
                _suppressWorkspaceChangeNotifications = false;
            }

            var shouldClearSelection = IsCompactLayout;
            ReloadWorkspaceList(snapshot.Workspace.WorkspaceId);
            if (shouldClearSelection)
            {
                SelectedWorkspace = null;
                ClearStatus();
            }
            else
            {
                var statusText = warmupResult?.Status == AgentExecutionTargetWarmupStatus.Failed
                    ? $"Workspace saved, but execution target is not ready: {warmupResult.Message}"
                    : warmupResult?.Status == AgentExecutionTargetWarmupStatus.Ready
                        ? "Workspace saved. Execution target is ready."
                        : "Workspace saved.";
                var statusKind = warmupResult?.Status == AgentExecutionTargetWarmupStatus.Failed
                    ? AgentWorkspaceStatusKind.Warning
                    : AgentWorkspaceStatusKind.Success;

                SetStatus(statusText, statusKind, autoClear: statusKind == AgentWorkspaceStatusKind.Success);
            }

            IsEditorActive = false;
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentWorkspaceStatusKind.Error);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    private sealed record WorkspaceSaveSnapshot(
        AgentWorkspaceRecord Workspace,
        string DisplayName,
        string Description,
        IReadOnlyList<AgentWorkspacePathRecord> Paths,
        IReadOnlyList<AgentWorkspaceDocumentRecord> Documents,
        string? ExecutionTargetId,
        IReadOnlyList<AgentEditorSectionViewModel> EditorSections);
}
