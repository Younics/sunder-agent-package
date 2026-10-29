using System.ComponentModel;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentWorkspacesViewModel
{
    private void OnWorkspaceEditorChanged()
    {
        if (_suppressDraftTracking || SelectedWorkspace is not { } workspace)
        {
            return;
        }

        _listDetail.PromoteSelectionToExplicit();
        var revision = ++_workspaceDraftRevision;
        var draft = CaptureWorkspaceDraft(workspace);
        if (_workspaceDrafts.TryGetValue(workspace.WorkspaceId, out var state))
        {
            state.Draft = draft;
            state.Revision = revision;
            state.IsDirty = true;
        }
        else
        {
            _workspaceDrafts[workspace.WorkspaceId] = new AgentWorkspaceDraftState(
                draft,
                revision,
                isDirty: true);
        }
    }

    private AgentWorkspaceEditorDraft CaptureWorkspaceDraft(AgentWorkspaceRecord workspace)
        => new(
            DisplayName,
            Description,
            WorkspacePaths.Select((path, index) => path.ToRecord(workspace.WorkspaceId, index)).ToArray(),
            WorkspaceDocuments.Select((document, index) => document.ToRecord(workspace.WorkspaceId, index)).ToArray(),
            SelectedExecutionTarget is { IsUnconfigured: false } target ? target.TargetId : null,
            EditorSections.ToArray());

    private void CaptureCurrentWorkspaceDraft()
    {
        if (_suppressDraftTracking
            || SelectedWorkspace is not { } workspace
            || !_workspaceDrafts.TryGetValue(workspace.WorkspaceId, out var state))
        {
            return;
        }

        state.Draft = CaptureWorkspaceDraft(workspace);
    }

    private void ApplyWorkspaceDraft(
        AgentWorkspaceRecord workspace,
        AgentWorkspaceDraftState state)
    {
        _suppressDraftTracking = true;
        try
        {
            DisplayName = state.Draft.DisplayName;
            Description = state.Draft.Description;
            ReplaceWorkspacePaths(state.Draft.Paths);
            ReplaceWorkspaceDocuments(state.Draft.Documents);
            SelectedExecutionTarget = ResolveTargetOption(state.Draft.ExecutionTargetId);
            ReplaceEditorSections(state.Draft.EditorSections);
        }
        finally
        {
            _suppressDraftTracking = false;
        }

        state.Draft = CaptureWorkspaceDraft(workspace);
        MarkCurrentWorkspaceDetailReady();
    }

    private void RegisterCleanWorkspaceDraft(AgentWorkspaceRecord workspace)
    {
        var revision = _workspaceDrafts.TryGetValue(workspace.WorkspaceId, out var existing)
            ? existing.Revision
            : _workspaceDraftRevision;
        _workspaceDrafts[workspace.WorkspaceId] = new AgentWorkspaceDraftState(
            CaptureWorkspaceDraft(workspace),
            revision,
            isDirty: false);
    }

    private long GetWorkspaceDraftRevision(string workspaceId)
        => _workspaceDrafts.TryGetValue(workspaceId, out var state)
            ? state.Revision
            : 0;

    private bool IsWorkspaceDraftDirty(string workspaceId)
        => _workspaceDrafts.TryGetValue(workspaceId, out var state) && state.IsDirty;

    private void MarkWorkspaceDraftClean(string workspaceId)
    {
        if (_workspaceDrafts.TryGetValue(workspaceId, out var state))
        {
            state.IsDirty = false;
        }
    }

    private void ReplaceWorkspacePaths(IEnumerable<AgentWorkspacePathRecord> paths)
    {
        var selectedId = SelectedWorkspacePath?.PathId;
        var desired = paths.OrderBy(path => path.SortOrder).Select(path =>
            WorkspacePaths.FirstOrDefault(item => item.PathId == path.PathId
                && item.HostPath == AgentWorkspacePathFormatter.FormatForDisplay(path.HostPath)
                && item.IsDefault == path.IsDefault) ?? new AgentWorkspacePathItemViewModel(path)).ToArray();
        foreach (var item in WorkspacePaths.Except(desired)) item.PropertyChanged -= OnWorkspacePathPropertyChanged;
        foreach (var item in desired.Except(WorkspacePaths)) item.PropertyChanged += OnWorkspacePathPropertyChanged;
        ObservableCollectionReconciler.Apply(WorkspacePaths, desired);

        SelectedWorkspacePath = WorkspacePaths.FirstOrDefault(path => path.PathId == selectedId)
            ?? WorkspacePaths.FirstOrDefault(path => path.IsDefault)
            ?? WorkspacePaths.FirstOrDefault();
        NotifyWorkspacePathCollectionChanged();
    }

    private void ReplaceWorkspaceDocuments(IEnumerable<AgentWorkspaceDocumentRecord> documents)
    {
        var selectedId = SelectedWorkspaceDocument?.DocumentId;
        var desired = documents.OrderBy(document => document.SortOrder).Select(document =>
            WorkspaceDocuments.FirstOrDefault(item => item.DocumentId == document.DocumentId
                && item.FilePath == AgentWorkspacePathFormatter.FormatForDisplay(document.FilePath))
            ?? new AgentWorkspaceDocumentItemViewModel(document)).ToArray();
        foreach (var item in WorkspaceDocuments.Except(desired)) item.PropertyChanged -= OnWorkspaceDocumentPropertyChanged;
        foreach (var item in desired.Except(WorkspaceDocuments)) item.PropertyChanged += OnWorkspaceDocumentPropertyChanged;
        ObservableCollectionReconciler.Apply(WorkspaceDocuments, desired);

        SelectedWorkspaceDocument = WorkspaceDocuments.FirstOrDefault(document => document.DocumentId == selectedId)
            ?? WorkspaceDocuments.FirstOrDefault();
        NotifyWorkspaceDocumentCollectionChanged();
    }

    private void ReplaceEditorSections(IEnumerable<AgentEditorSectionViewModel> sections)
    {
        var desired = sections.ToArray();
        foreach (var section in EditorSections.Except(desired)) section.Changed -= OnContributedEditorChanged;
        foreach (var section in desired.Except(EditorSections)) section.Changed += OnContributedEditorChanged;
        ObservableCollectionReconciler.Apply(EditorSections, desired);
    }

    private void InsertEditorSection(int index, AgentEditorSectionViewModel section)
    {
        section.Changed += OnContributedEditorChanged;
        EditorSections.Insert(index, section);
    }

    private void RemoveEditorSectionAt(int index)
    {
        EditorSections[index].Changed -= OnContributedEditorChanged;
        EditorSections.RemoveAt(index);
    }

    private void OnWorkspacePathPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => OnWorkspaceEditorChanged();

    private void OnWorkspaceDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => OnWorkspaceEditorChanged();

    private void OnContributedEditorChanged() => OnWorkspaceEditorChanged();

    private void MarkCurrentWorkspaceDetailReady()
    {
        if (_listDetail.IsExistingDetail && _listDetail.DetailPhase == AdaptiveDetailPhase.None)
        {
            var ticket = _listDetail.BeginDetailLoad(_lifetimeCancellation.Token);
            _listDetail.TrySetDetailReady(ticket);
        }
    }
}
