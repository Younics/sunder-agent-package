using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentPermissionsViewModel : ObservableObject,
    IPackageViewNavigationPreparationTarget,
    IDisposable
{
    private readonly IAgentPermissionGateway _permissionService;
    private readonly IAgentGlobalPermissionGateway? _globalPermissionGateway;
    private readonly IAgentRuntimeAvailability? _runtimeAvailability;
    private readonly PresentationTaskScope _tasks = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly IPresentationDispatcher _uiDispatcher;
    private readonly Task _initialization;
    private long _appliedPermissionRevision = -1;
    private long _reloadGeneration;
    private bool _disposed;

    internal static IReadOnlyCollection<string> OwnedConfigurationKeys { get; } = [];

    public AgentPermissionsViewModel(IAgentPermissionGateway permissionService)
        : this(permissionService, PresentationDispatcher.Capture())
    {
    }

    internal AgentPermissionsViewModel(
        IAgentPermissionGateway permissionService,
        IPresentationDispatcher uiDispatcher)
    {
        _permissionService = permissionService;
        _globalPermissionGateway = permissionService as IAgentGlobalPermissionGateway;
        _runtimeAvailability = permissionService as IAgentRuntimeAvailability;
        _uiDispatcher = uiDispatcher;
        _lifetimeToken = _tasks.CancellationToken;
        if (_runtimeAvailability is not null)
        {
            _runtimeAvailability.ConnectionStateChanged += OnRuntimeConnectionStateChanged;
        }
        _initialization = InitializeCoreAsync();
    }

    public ObservableCollection<PermissionBoundaryRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    partial void OnIsBusyChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        ResetToPackageDefaultsCommand.NotifyCanExecuteChanged();
    }

    public async ValueTask<bool> PrepareNavigationAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        await _initialization.WaitAsync(cancellationToken);
        return true;
    }

    public ValueTask OnNavigationPresentedAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanMutatePermissions))]
    private async Task SaveAsync()
    {
        PermissionOverrideChange[]? changes = null;
        await _uiDispatcher.InvokeAsync(() =>
        {
            if (!CanMutatePermissions())
            {
                return;
            }

            IsBusy = true;
            changes = Rows
                .Select(row => new PermissionOverrideChange(
                    row.ActionId,
                    row.BoundaryId,
                    row.DefaultDecision,
                    row.SelectedDecision))
                .ToArray();
        }).ConfigureAwait(false);
        if (changes is null)
        {
            return;
        }

        var cancellationToken = _lifetimeToken;
        try
        {
            foreach (var change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (change.SelectedDecision == change.DefaultDecision)
                {
                    if (_globalPermissionGateway is null)
                    {
                        _permissionService.DeleteOverride(change.ActionId, change.BoundaryId);
                    }
                    else
                    {
                        await _globalPermissionGateway.DeleteOverrideAsync(
                                change.ActionId,
                                change.BoundaryId,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    if (_globalPermissionGateway is null)
                    {
                        _permissionService.SaveOverride(
                            change.ActionId,
                            change.BoundaryId,
                            change.SelectedDecision);
                    }
                    else
                    {
                        await _globalPermissionGateway.SaveOverrideAsync(
                                change.ActionId,
                                change.BoundaryId,
                                change.SelectedDecision,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }

            _ = await ReloadAsync(cancellationToken, "Permission defaults saved.")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
        finally
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    IsBusy = false;
                }
            }).ConfigureAwait(false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanMutatePermissions))]
    private async Task ResetToPackageDefaultsAsync()
    {
        (string ActionId, string BoundaryId)[]? overrides = null;
        await _uiDispatcher.InvokeAsync(() =>
        {
            if (!CanMutatePermissions())
            {
                return;
            }

            IsBusy = true;
            overrides = Rows
                .Select(row => (row.ActionId, row.BoundaryId))
                .ToArray();
        }).ConfigureAwait(false);
        if (overrides is null)
        {
            return;
        }

        var cancellationToken = _lifetimeToken;
        try
        {
            foreach (var (actionId, boundaryId) in overrides)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_globalPermissionGateway is null)
                {
                    _permissionService.DeleteOverride(actionId, boundaryId);
                }
                else
                {
                    await _globalPermissionGateway.DeleteOverrideAsync(
                            actionId,
                            boundaryId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            _ = await ReloadAsync(cancellationToken, "Permission defaults restored.")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
        finally
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    IsBusy = false;
                }
            }).ConfigureAwait(false);
        }
    }

    private bool CanMutatePermissions() => !_disposed && !IsBusy;

    private Task<bool> ReloadAsync(
        CancellationToken cancellationToken,
        string? statusText = null)
        => ReloadCoreAsync(
            cancellationToken,
            Interlocked.Increment(ref _reloadGeneration),
            statusText);

    private async Task<bool> ReloadCoreAsync(
        CancellationToken cancellationToken,
        long reloadGeneration,
        string? statusText)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var projection = _globalPermissionGateway is null
            ? new AgentPermissionProjection(
                0,
                null,
                _permissionService.ListActions(),
                _permissionService.ListOverrides(),
                [])
            : await _globalPermissionGateway.LoadGlobalPermissionsAsync(cancellationToken)
                .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var applied = false;
        await _uiDispatcher.InvokeAsync(() =>
        {
            if (_disposed
                || cancellationToken.IsCancellationRequested
                || projection.Revision < _appliedPermissionRevision)
            {
                return;
            }

            _appliedPermissionRevision = projection.Revision;
            ApplyProjection(projection);
            if (statusText is not null
                && reloadGeneration == Volatile.Read(ref _reloadGeneration))
            {
                StatusText = statusText;
            }
            applied = true;
        }).ConfigureAwait(false);
        return applied;
    }

    private void ApplyProjection(AgentPermissionProjection projection)
    {
        var overrides = projection.Overrides
            .ToDictionary(item => (item.ActionId, item.BoundaryId), item => item.Decision);
        Rows.Clear();
        foreach (var action in projection.Actions)
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

    private async Task<bool> TryReloadAsync(CancellationToken cancellationToken = default)
    {
        var reloadGeneration = Interlocked.Increment(ref _reloadGeneration);
        try
        {
            return await ReloadCoreAsync(cancellationToken, reloadGeneration, string.Empty)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            return false;
        }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_disposed
                    && !cancellationToken.IsCancellationRequested
                    && reloadGeneration == Volatile.Read(ref _reloadGeneration))
                {
                    StatusText = $"Agent Runtime is unavailable: {ex.Message}";
                }
            }).ConfigureAwait(false);
            return false;
        }
    }

    private async Task InitializeCoreAsync()
    {
        var cancellationToken = _lifetimeToken;
        try
        {
            if (_permissionService is IAgentPresentationInitialization initialization)
            {
                await initialization.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }
            _ = await TryReloadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    StatusText = $"Agent Runtime is unavailable: {ex.Message}";
                }
            }).ConfigureAwait(false);
        }
    }

    private void OnRuntimeConnectionStateChanged(AgentRuntimeConnectionState state)
        => _tasks.Run(async cancellationToken =>
        {
            if (state == AgentRuntimeConnectionState.Connected)
            {
                _ = await TryReloadAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            if (state is AgentRuntimeConnectionState.Unavailable or AgentRuntimeConnectionState.Reconnecting)
            {
                await _uiDispatcher.InvokeAsync(() =>
                {
                    if (!_disposed && !cancellationToken.IsCancellationRequested)
                    {
                        StatusText = "Agent Runtime is unavailable. Reconnecting...";
                    }
                }).ConfigureAwait(false);
            }
        });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Interlocked.Increment(ref _reloadGeneration);
        _tasks.Dispose();
        if (_runtimeAvailability is not null)
        {
            _runtimeAvailability.ConnectionStateChanged -= OnRuntimeConnectionStateChanged;
        }
        SaveCommand.NotifyCanExecuteChanged();
        ResetToPackageDefaultsCommand.NotifyCanExecuteChanged();
    }

    private readonly record struct PermissionOverrideChange(
        string ActionId,
        string BoundaryId,
        AgentPermissionDecision DefaultDecision,
        AgentPermissionDecision SelectedDecision);
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
