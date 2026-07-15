using System.Collections.ObjectModel;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel
{
    private string[] _workspacePathChipLabels = [];
    private int _wideWorkspacePathChipVisibleCount = int.MaxValue;
    private int _narrowWorkspacePathChipVisibleCount = int.MaxValue;
    partial void OnSelectedWorkspaceChanged(AgentWorkspaceRecord? value)
    {
        if (_suppressWorkspaceSelection)
        {
            return;
        }

        _globalStatusText = string.Empty;
        RefreshWorkspacePathChips();
        CreateSessionCommand.NotifyCanExecuteChanged();
        RefreshSetupState();
        ScheduleSelectedWorkspaceWarmup();
        ScheduleChatSnapshotRequest(
            SelectedProfile?.ProfileId,
            value?.WorkspaceId,
            preferredSessionId: null);
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

    private void RefreshWorkspacePathChips()
    {
        _workspacePathChipLabels = (SelectedWorkspace?.Paths ?? [])
            .OrderBy(path => path.SortOrder)
            .Select(path => AgentWorkspacePathFormatter.FormatForDisplay(path.HostPath))
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .ToArray();

        ApplyWorkspacePathChipLayout();
        OnPropertyChanged(nameof(WorkspacePathChipLabels));
        OnPropertyChanged(nameof(HasWorkspacePathChips));
    }

    public void UpdateWorkspacePathChipLayout(int wideVisibleCount, int narrowVisibleCount)
    {
        wideVisibleCount = Math.Max(0, wideVisibleCount);
        narrowVisibleCount = Math.Max(0, narrowVisibleCount);
        if (_wideWorkspacePathChipVisibleCount == wideVisibleCount
            && _narrowWorkspacePathChipVisibleCount == narrowVisibleCount)
        {
            return;
        }

        _wideWorkspacePathChipVisibleCount = wideVisibleCount;
        _narrowWorkspacePathChipVisibleCount = narrowVisibleCount;
        ApplyWorkspacePathChipLayout();
    }

    private void ApplyWorkspacePathChipLayout()
    {
        var wideCount = Math.Min(_workspacePathChipLabels.Length, _wideWorkspacePathChipVisibleCount);
        var narrowCount = Math.Min(_workspacePathChipLabels.Length, _narrowWorkspacePathChipVisibleCount);
        ReplaceWorkspacePathChips(WideWorkspacePathChips, _workspacePathChipLabels.Take(wideCount));
        ReplaceWorkspacePathChips(NarrowWorkspacePathChips, _workspacePathChipLabels.Take(narrowCount));
        WideWorkspacePathOverflowText = _workspacePathChipLabels.Length > wideCount
            ? $"+{_workspacePathChipLabels.Length - wideCount} more"
            : string.Empty;
        NarrowWorkspacePathOverflowText = _workspacePathChipLabels.Length > narrowCount
            ? $"+{_workspacePathChipLabels.Length - narrowCount} more"
            : string.Empty;
    }

    private static void ReplaceWorkspacePathChips(
        ObservableCollection<AgentWorkspacePathChipViewModel> target,
        IEnumerable<string> labels)
    {
        target.Clear();
        foreach (var label in labels)
        {
            target.Add(new AgentWorkspacePathChipViewModel(label));
        }
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

    private void OnWorkspacesChanged()
    {
        if (_isInitialized)
        {
            RunOnUiThread(ApplyWorkspacesChanged);
        }
    }

    private void ApplyWorkspacesChanged()
    {
        ScheduleChatSnapshotRequest(
            SelectedProfile?.ProfileId,
            SelectedWorkspace?.WorkspaceId,
            SelectedSession?.SessionId);
    }
}
