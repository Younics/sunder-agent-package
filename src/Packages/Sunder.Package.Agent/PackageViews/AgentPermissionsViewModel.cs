using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentPermissionsViewModel : ObservableObject
{
    private readonly AgentPermissionService _permissionService;

    public AgentPermissionsViewModel(AgentPermissionService permissionService)
    {
        _permissionService = permissionService;
        Reload();
    }

    public ObservableCollection<PermissionBoundaryRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private string _statusText = string.Empty;

    [RelayCommand]
    private void Save()
    {
        foreach (var row in Rows)
        {
            if (row.SelectedDecision == row.DefaultDecision)
            {
                _permissionService.DeleteOverride(row.ActionId, row.BoundaryId);
            }
            else
            {
                _permissionService.SaveOverride(row.ActionId, row.BoundaryId, row.SelectedDecision);
            }
        }

        Reload();
        StatusText = "Permission defaults saved.";
    }

    [RelayCommand]
    private void ResetToPackageDefaults()
    {
        foreach (var row in Rows)
        {
            _permissionService.DeleteOverride(row.ActionId, row.BoundaryId);
        }

        Reload();
        StatusText = "Permission defaults restored.";
    }

    private void Reload()
    {
        var overrides = _permissionService.ListOverrides()
            .ToDictionary(item => (item.ActionId, item.BoundaryId), item => item.Decision);
        Rows.Clear();
        foreach (var action in _permissionService.ListActions())
        {
            foreach (var boundary in action.Boundaries)
            {
                var selected = overrides.TryGetValue((action.ActionId, boundary.BoundaryId), out var decision)
                    ? decision
                    : boundary.DefaultDecision;
                Rows.Add(new PermissionBoundaryRowViewModel(
                    action.ActionId,
                    action.DisplayName,
                    boundary.BoundaryId,
                    boundary.DisplayName,
                    boundary.Description,
                    boundary.DefaultDecision,
                    selected));
            }
        }
    }
}

public sealed partial class PermissionBoundaryRowViewModel(
    string actionId,
    string actionDisplayName,
    string boundaryId,
    string boundaryDisplayName,
    string boundaryDescription,
    AgentPermissionDecision defaultDecision,
    AgentPermissionDecision selectedDecision) : ObservableObject
{
    public string ActionId { get; } = actionId;

    public string ActionDisplayName { get; } = actionDisplayName;

    public string BoundaryId { get; } = boundaryId;

    public string BoundaryDisplayName { get; } = boundaryDisplayName;

    public string BoundaryDescription { get; } = boundaryDescription;

    public AgentPermissionDecision DefaultDecision { get; } = defaultDecision;

    public IReadOnlyList<AgentPermissionDecision> Decisions { get; } = Enum.GetValues<AgentPermissionDecision>();

    [ObservableProperty]
    private AgentPermissionDecision _selectedDecision = selectedDecision;
}
