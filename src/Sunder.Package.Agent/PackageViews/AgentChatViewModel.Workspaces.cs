using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel
{
    partial void OnSelectedWorkspaceChanged(AgentWorkspaceRecord? value)
    {
        if (_suppressWorkspaceSelection)
        {
            return;
        }

        _selectionState?.SaveSelectedWorkspaceId(value?.WorkspaceId);
        _globalStatusText = string.Empty;
        RefreshSetupState();
        ScheduleSelectedWorkspaceWarmup();
    }

    private bool ReloadWorkspaces(string? selectWorkspaceId)
    {
        var previousSelectedWorkspaceId = SelectedWorkspace?.WorkspaceId;
        var workspaces = _workspaceService.ListWorkspaces();
        _suppressWorkspaceSelection = true;
        try
        {
            ReconcileWorkspaces(workspaces);

            var desiredWorkspaceId = selectWorkspaceId ?? previousSelectedWorkspaceId;

            SelectedWorkspace =
                Workspaces.FirstOrDefault(workspace =>
                    string.Equals(
                        workspace.WorkspaceId,
                        desiredWorkspaceId,
                        StringComparison.OrdinalIgnoreCase
                    )
                ) ?? Workspaces.FirstOrDefault();
        }
        finally
        {
            _suppressWorkspaceSelection = false;
        }

        var selectedWorkspaceChanged = !string.Equals(
            previousSelectedWorkspaceId,
            SelectedWorkspace?.WorkspaceId,
            StringComparison.OrdinalIgnoreCase
        );
        _selectionState?.SaveSelectedWorkspaceId(SelectedWorkspace?.WorkspaceId);
        NotifyWorkspaceStateChanged();
        CreateSessionCommand.NotifyCanExecuteChanged();
        RefreshSetupState();
        return selectedWorkspaceChanged;
    }

    private void ReconcileWorkspaces(IReadOnlyList<AgentWorkspaceRecord> workspaces)
    {
        var desiredWorkspaceIds = workspaces
            .Select(workspace => workspace.WorkspaceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var index = Workspaces.Count - 1; index >= 0; index--)
        {
            if (desiredWorkspaceIds.Contains(Workspaces[index].WorkspaceId))
            {
                continue;
            }

            Workspaces.RemoveAt(index);
        }

        for (var index = 0; index < workspaces.Count; index++)
        {
            var workspace = workspaces[index];
            var existingIndex = FindWorkspaceIndex(workspace.WorkspaceId);
            if (existingIndex < 0)
            {
                Workspaces.Insert(index, workspace);
                continue;
            }

            if (!Equals(Workspaces[existingIndex], workspace))
            {
                Workspaces[existingIndex] = workspace;
            }

            if (existingIndex == index)
            {
                continue;
            }

            Workspaces.Move(existingIndex, index);
        }
    }

    private void NotifyWorkspaceStateChanged()
    {
        OnPropertyChanged(nameof(HasWorkspaces));
        OnPropertyChanged(nameof(HasNoWorkspaces));
    }

    private int FindWorkspaceIndex(string workspaceId)
    {
        for (var index = 0; index < Workspaces.Count; index++)
        {
            if (
                string.Equals(
                    Workspaces[index].WorkspaceId,
                    workspaceId,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return index;
            }
        }

        return -1;
    }

    private void OnWorkspacesChanged() => RunOnUiThread(ApplyWorkspacesChanged);

    private void ApplyWorkspacesChanged()
    {
        ReloadWorkspaces(SelectedWorkspace?.WorkspaceId);
        ScheduleSelectedWorkspaceWarmup();
    }
}
