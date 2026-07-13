using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentWorkspacesViewModel
{
    public void AddWorkspacePath(string hostPath)
    {
        if (SelectedWorkspace is null || string.IsNullOrWhiteSpace(hostPath))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var fullPath = AgentWorkspacePathFormatter.GetFullPath(hostPath);
        if (WorkspacePaths.Any(path => string.Equals(AgentWorkspacePathFormatter.GetFullPath(path.HostPath), fullPath, GetPathStringComparison())))
        {
            return;
        }

        var item = new AgentWorkspacePathItemViewModel(new AgentWorkspacePathRecord(
            Guid.NewGuid().ToString("N"),
            SelectedWorkspace.WorkspaceId,
            fullPath,
            WorkspacePaths.Count == 0,
            WorkspacePaths.Count,
            now,
            now));
        WorkspacePaths.Add(item);
        SelectedWorkspacePath = item;
        NotifyWorkspacePathCollectionChanged();
    }

    public void AddWorkspaceDocument(string filePath)
    {
        if (SelectedWorkspace is null || string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var fullPath = AgentWorkspacePathFormatter.GetFullPath(filePath);
        if (WorkspaceDocuments.Any(document => string.Equals(AgentWorkspacePathFormatter.GetFullPath(document.FilePath), fullPath, GetPathStringComparison())))
        {
            return;
        }

        var item = new AgentWorkspaceDocumentItemViewModel(new AgentWorkspaceDocumentRecord(
            Guid.NewGuid().ToString("N"),
            SelectedWorkspace.WorkspaceId,
            fullPath,
            WorkspaceDocuments.Count,
            now,
            now));
        WorkspaceDocuments.Add(item);
        SelectedWorkspaceDocument = item;
        NotifyWorkspaceDocumentCollectionChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSetSelectedWorkspacePathAsDefault))]
    private void SetSelectedWorkspacePathAsDefault() => SetWorkspacePathAsDefault(SelectedWorkspacePath);

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedWorkspacePath))]
    private void DeleteSelectedWorkspacePath() => DeleteWorkspacePath(SelectedWorkspacePath);

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedWorkspaceDocument))]
    private void DeleteSelectedWorkspaceDocument() => DeleteWorkspaceDocument(SelectedWorkspaceDocument);

    [RelayCommand]
    private void BeginEditWorkspacePath(AgentWorkspacePathItemViewModel? path)
    {
        if (path is null)
        {
            return;
        }

        foreach (var item in WorkspacePaths)
        {
            if (!ReferenceEquals(item, path) && item.IsEditActive)
            {
                item.CancelEdit();
            }
        }

        path.BeginEdit();
    }

    [RelayCommand]
    private static void SaveWorkspacePathEdit(AgentWorkspacePathItemViewModel? path) => path?.SaveEdit();

    [RelayCommand]
    private static void CancelWorkspacePathEdit(AgentWorkspacePathItemViewModel? path) => path?.CancelEdit();

    [RelayCommand]
    private void SetWorkspacePathAsDefault(AgentWorkspacePathItemViewModel? path)
    {
        if (path is null)
        {
            return;
        }

        foreach (var item in WorkspacePaths)
        {
            item.IsDefault = ReferenceEquals(item, path);
        }

        SelectedWorkspacePath = path;
        SetSelectedWorkspacePathAsDefaultCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void DeleteWorkspacePath(AgentWorkspacePathItemViewModel? path)
    {
        if (path is null)
        {
            return;
        }

        var wasDefault = path.IsDefault;
        WorkspacePaths.Remove(path);
        if (wasDefault && WorkspacePaths.Count > 0)
        {
            WorkspacePaths[0].IsDefault = true;
        }

        if (ReferenceEquals(SelectedWorkspacePath, path))
        {
            SelectedWorkspacePath = WorkspacePaths.FirstOrDefault(item => item.IsDefault) ?? WorkspacePaths.FirstOrDefault();
        }

        NotifyWorkspacePathCollectionChanged();
    }

    [RelayCommand]
    private void BeginEditWorkspaceDocument(AgentWorkspaceDocumentItemViewModel? document)
    {
        if (document is null)
        {
            return;
        }

        foreach (var item in WorkspaceDocuments)
        {
            if (!ReferenceEquals(item, document) && item.IsEditActive)
            {
                item.CancelEdit();
            }
        }

        document.BeginEdit();
    }

    [RelayCommand]
    private static void SaveWorkspaceDocumentEdit(AgentWorkspaceDocumentItemViewModel? document) => document?.SaveEdit();

    [RelayCommand]
    private static void CancelWorkspaceDocumentEdit(AgentWorkspaceDocumentItemViewModel? document) => document?.CancelEdit();

    [RelayCommand]
    private void DeleteWorkspaceDocument(AgentWorkspaceDocumentItemViewModel? document)
    {
        if (document is null)
        {
            return;
        }

        WorkspaceDocuments.Remove(document);
        if (ReferenceEquals(SelectedWorkspaceDocument, document))
        {
            SelectedWorkspaceDocument = WorkspaceDocuments.FirstOrDefault();
        }

        NotifyWorkspaceDocumentCollectionChanged();
    }

    private bool CanSetSelectedWorkspacePathAsDefault() => SelectedWorkspacePath is not null;

    private bool CanDeleteSelectedWorkspacePath() => SelectedWorkspacePath is not null;

    private bool CanDeleteSelectedWorkspaceDocument() => SelectedWorkspaceDocument is not null;
}
