using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Runtime;
using Avalonia.Threading;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentPermissionsViewModel : ObservableObject, IDisposable
{
    private readonly IAgentPermissionGateway _permissionService;
    private readonly IAgentRuntimeAvailability? _runtimeAvailability;
    private readonly PresentationTaskScope _tasks = new();
    private bool _disposed;

    internal static IReadOnlyCollection<string> OwnedConfigurationKeys { get; } = [];

    public AgentPermissionsViewModel(IAgentPermissionGateway permissionService)
    {
        _permissionService = permissionService;
        _runtimeAvailability = permissionService as IAgentRuntimeAvailability;
        if (_runtimeAvailability is not null)
        {
            _runtimeAvailability.ConnectionStateChanged += OnRuntimeConnectionStateChanged;
        }
        TryReload();
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

    private void TryReload()
    {
        try
        {
            Reload();
        }
        catch (Exception ex)
        {
            StatusText = $"Agent Runtime is unavailable: {ex.Message}";
        }
    }

    private void OnRuntimeConnectionStateChanged(AgentRuntimeConnectionState state)
        => _tasks.Run(async cancellationToken =>
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                if (state == AgentRuntimeConnectionState.Connected)
                {
                    TryReload();
                    if (Rows.Count > 0)
                    {
                        StatusText = string.Empty;
                    }
                }
                else if (state is AgentRuntimeConnectionState.Unavailable or AgentRuntimeConnectionState.Reconnecting)
                {
                    StatusText = "Agent Runtime is unavailable. Reconnecting...";
                }
            }, DispatcherPriority.Background);
        });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _tasks.Dispose();
        if (_runtimeAvailability is not null)
        {
            _runtimeAvailability.ConnectionStateChanged -= OnRuntimeConnectionStateChanged;
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
