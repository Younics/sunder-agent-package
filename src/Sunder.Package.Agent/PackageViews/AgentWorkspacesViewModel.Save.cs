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

        WorkspaceSaveSnapshot? snapshot = null;
        var operation = BeginOperation(AgentWorkspaceOperation.Save);
        try
        {
            if (!ValidateWorkspacePathsAndDocuments(out var validationMessage))
            {
                SetStatus(validationMessage, AgentWorkspaceStatusKind.Error);
                return;
            }

            CaptureCurrentWorkspaceDraft();
            var editorContext = BuildEditorContext();
            snapshot = new WorkspaceSaveSnapshot(
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
                EditorSections.ToArray(),
                editorContext is null ? null : CaptureEditorIntent(editorContext),
                IntentRevision,
                LayoutRevision,
                GetWorkspaceDraftRevision(selectedWorkspace.WorkspaceId));
            var editorSaveResult = await SaveEditorSectionsAsync(
                snapshot.EditorSections,
                snapshot.EditorIntent,
                _lifetimeCancellation.Token);
            if (!editorSaveResult.Success)
            {
                if (IsCurrentSaveIntent(snapshot))
                {
                    SetStatus(editorSaveResult.Message, AgentWorkspaceStatusKind.Error);
                }
                return;
            }

            AgentExecutionTargetWarmupResult? warmupResult = null;
            _suppressWorkspaceRefresh = true;
            try
            {
                if (_workspaceCommands is null)
                {
                    _workspaceService.SaveWorkspaceAggregate(
                        snapshot.Workspace.WorkspaceId,
                        snapshot.DisplayName,
                        snapshot.Description,
                        snapshot.Paths,
                        snapshot.Documents,
                        snapshot.ExecutionTargetId);
                }
                else
                {
                    await _workspaceCommands.SaveWorkspaceAggregateAsync(
                        snapshot.Workspace.WorkspaceId,
                        snapshot.DisplayName,
                        snapshot.Description,
                        snapshot.Paths,
                        snapshot.Documents,
                        snapshot.ExecutionTargetId,
                        _lifetimeCancellation.Token);
                }
            }
            finally
            {
                _suppressWorkspaceRefresh = false;
            }
            var refreshedWorkspaces = await LoadWorkspacesAsync(_lifetimeCancellation.Token);
            if (snapshot.ExecutionTargetId is not null)
            {
                var warmupWorkspace = refreshedWorkspaces.FirstOrDefault(workspace => string.Equals(
                        workspace.WorkspaceId,
                        snapshot.Workspace.WorkspaceId,
                        StringComparison.OrdinalIgnoreCase))
                    ?? snapshot.Workspace;
                if (IsCurrentSaveIntent(snapshot))
                {
                    SetStatus("Workspace saved. Preparing execution target...", AgentWorkspaceStatusKind.Warning);
                }
                warmupResult = await _executionGateway.WarmWorkspaceAsync(
                    warmupWorkspace,
                    _lifetimeCancellation.Token);
            }

            var intentIsCurrent = IsCurrentSaveIntent(snapshot);
            if (intentIsCurrent)
            {
                DiscardPendingWorkspaceRefresh();
            }
            var editedDuringSave = GetWorkspaceDraftRevision(snapshot.Workspace.WorkspaceId)
                != snapshot.DraftRevision;
            var layoutIsCurrent = snapshot.LayoutRevision == LayoutRevision;
            if (intentIsCurrent && !editedDuringSave)
            {
                MarkWorkspaceDraftClean(snapshot.Workspace.WorkspaceId);
            }
            if (intentIsCurrent
                && !editedDuringSave
                && layoutIsCurrent
                && IsCompactLayout)
            {
                _listDetail.ShowList();
                ClearStatus();
            }
            var currentSelection = SelectedWorkspace;
            var workspaces = refreshedWorkspaces
                .Select(workspace => currentSelection is not null
                    && (!intentIsCurrent || editedDuringSave)
                    && string.Equals(
                        workspace.WorkspaceId,
                        currentSelection.WorkspaceId,
                        StringComparison.OrdinalIgnoreCase)
                        ? currentSelection
                        : workspace)
                .ToArray();
            _listDetail.Reconcile(workspaces);
            if (intentIsCurrent && _listDetail.IsExistingDetail)
            {
                var statusText = editedDuringSave
                    ? "Workspace saved. New edits remain unsaved."
                    : warmupResult?.Status == AgentExecutionTargetWarmupStatus.Failed
                    ? $"Workspace saved, but execution target is not ready: {warmupResult.Message}"
                    : warmupResult?.Status == AgentExecutionTargetWarmupStatus.Ready
                        ? "Workspace saved. Execution target is ready."
                        : "Workspace saved.";
                var statusKind = warmupResult?.Status == AgentExecutionTargetWarmupStatus.Failed
                    ? AgentWorkspaceStatusKind.Warning
                    : AgentWorkspaceStatusKind.Success;

                SetStatus(
                    statusText,
                    statusKind,
                    autoClear: statusKind == AgentWorkspaceStatusKind.Success && !editedDuringSave);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (snapshot is not null && IsCurrentSaveIntent(snapshot))
            {
                SetStatus(ex.Message, AgentWorkspaceStatusKind.Error);
            }
        }
        finally
        {
            EndOperation(operation);
        }
    }

    private bool IsCurrentSaveIntent(WorkspaceSaveSnapshot snapshot)
        => snapshot.IntentRevision == IntentRevision
           && string.Equals(
               SelectedWorkspace?.WorkspaceId,
               snapshot.Workspace.WorkspaceId,
               StringComparison.OrdinalIgnoreCase);

    private sealed record WorkspaceSaveSnapshot(
        AgentWorkspaceRecord Workspace,
        string DisplayName,
        string Description,
        IReadOnlyList<AgentWorkspacePathRecord> Paths,
        IReadOnlyList<AgentWorkspaceDocumentRecord> Documents,
        string? ExecutionTargetId,
        IReadOnlyList<AgentEditorSectionViewModel> EditorSections,
        AgentWorkspaceEditorIntent? EditorIntent,
        long IntentRevision,
        long LayoutRevision,
        long DraftRevision);
}
