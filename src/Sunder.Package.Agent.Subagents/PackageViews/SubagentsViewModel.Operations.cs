using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Package.Agent.Subagents.Runtime;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public sealed partial class SubagentsViewModel
{
    [RelayCommand(CanExecute = nameof(CanNavigateSubagents))]
    private async Task CreateSubagentAsync()
    {
        if (_gateway is null)
        {
            return;
        }

        var intentRevision = 0L;
        LatestRequestTicket mutation = default;
        OperationGeneration operation = default;
        var started = false;
        await RunOnUiThreadAsync(() =>
        {
            CancelPendingMutation();
            intentRevision = _listDetail.ShowNewDetail();
            mutation = _requests.Begin(MutationChannel, _lifetimeCancellation.Token);
            operation = BeginOperation(SubagentOperation.Create);
            started = true;
        });
        if (!started)
        {
            return;
        }

        Task hydration = Task.CompletedTask;
        try
        {
            var created = await _gateway.CreateSubagentAsync(
                "New Subagent",
                mutation.CancellationToken)
                .WaitAsync(mutation.CancellationToken);
            DiscardPendingSubagentRefresh();
            await RunOnUiThreadAsync(() =>
            {
                if (!_requests.IsCurrent(mutation))
                {
                    return;
                }

                var createdAlreadyPresent = Subagents.Any(subagent => string.Equals(
                    subagent.SubagentId,
                    created.SubagentId,
                    StringComparison.OrdinalIgnoreCase));
                _listDetail.Reconcile(createdAlreadyPresent
                    ? Subagents.Select(subagent => string.Equals(
                            subagent.SubagentId,
                            created.SubagentId,
                            StringComparison.OrdinalIgnoreCase)
                        ? created
                        : subagent).ToArray()
                    : [.. Subagents, created]);
                if (_listDetail.TryShowCreatedDetail(created.SubagentId, intentRevision))
                {
                    ClearStatus();
                }
                hydration = _currentDetailLoad;
            });
            await hydration.WaitAsync(mutation.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (mutation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await RunOnUiThreadAsync(() =>
            {
                if (_requests.IsCurrent(mutation))
                {
                    SetStatus(ex.Message, SubagentStatusKind.Error);
                }
            });
        }
        finally
        {
            _requests.Complete(mutation);
            await RunOnUiThreadAsync(() => EndOperation(operation));
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveSubagent))]
    private async Task SaveSubagentAsync()
    {
        if (_gateway is null)
        {
            return;
        }

        SubagentRecord? selected = null;
        SubagentSaveRequest? request = null;
        var intentRevision = 0L;
        var layoutRevision = 0L;
        var editRevision = 0L;
        LatestRequestTicket mutation = default;
        OperationGeneration operation = default;
        var started = false;
        await RunOnUiThreadAsync(() =>
        {
            selected = SelectedSubagent;
            if (selected is null || !CanSaveSubagent())
            {
                return;
            }

            intentRevision = IntentRevision;
            layoutRevision = LayoutRevision;
            editRevision = _editRevision;
            request = new SubagentSaveRequest(
                selected.SubagentId,
                DisplayName,
                Description,
                Instructions,
                ChatBinding.SelectedProvider?.Id,
                ChatBinding.SelectedModel?.Id,
                Capabilities.Assignments,
                ChatBinding.SettingsJson);
            mutation = _requests.Begin(MutationChannel, _lifetimeCancellation.Token);
            operation = BeginOperation(SubagentOperation.Save);
            started = true;
        });
        if (!started)
        {
            return;
        }

        var selectedSubagent = selected!;
        try
        {
            var saved = await _gateway.SaveSubagentAsync(request!, mutation.CancellationToken)
                .WaitAsync(mutation.CancellationToken);
            await RunOnUiThreadAsync(() =>
            {
                if (!_requests.IsCurrent(mutation)
                    || intentRevision != IntentRevision
                    || !string.Equals(
                        SelectedSubagent?.SubagentId,
                        selectedSubagent.SubagentId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var editedDuringSave = editRevision != _editRevision;
                if (!editedDuringSave)
                {
                    _drafts.Remove(saved.SubagentId);
                }
                OnPropertyChanged(nameof(IsDirty));
                DiscardPendingSubagentRefresh();
                if (layoutRevision == LayoutRevision && IsCompactLayout && !editedDuringSave)
                {
                    _listDetail.ShowList();
                    ClearStatus();
                }
                else
                {
                    SetStatus(
                        editedDuringSave
                            ? "Subagent saved. New edits remain unsaved."
                            : "Subagent saved.",
                        SubagentStatusKind.Success,
                        autoClear: !editedDuringSave);
                }
                _listDetail.Reconcile(Subagents.Select(subagent => string.Equals(
                        subagent.SubagentId,
                        saved.SubagentId,
                        StringComparison.OrdinalIgnoreCase)
                    ? saved
                    : subagent).ToArray());
            });
        }
        catch (OperationCanceledException) when (mutation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await RunOnUiThreadAsync(() =>
            {
                if (_requests.IsCurrent(mutation))
                {
                    SetStatus(ex.Message, SubagentStatusKind.Error);
                }
            });
        }
        finally
        {
            _requests.Complete(mutation);
            await RunOnUiThreadAsync(() => EndOperation(operation));
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditSubagent))]
    private async Task DeleteSubagentAsync()
    {
        if (_gateway is null)
        {
            return;
        }

        SubagentRecord? selected = null;
        var intentRevision = 0L;
        var layoutRevision = 0L;
        LatestRequestTicket mutation = default;
        OperationGeneration operation = default;
        var started = false;
        await RunOnUiThreadAsync(() =>
        {
            selected = SelectedSubagent;
            if (selected is null)
            {
                return;
            }

            intentRevision = IntentRevision;
            layoutRevision = LayoutRevision;
            mutation = _requests.Begin(MutationChannel, _lifetimeCancellation.Token);
            operation = BeginOperation(SubagentOperation.Delete);
            started = true;
        });
        if (!started)
        {
            return;
        }

        var selectedSubagent = selected!;
        try
        {
            var subagentId = selectedSubagent.SubagentId;
            var deletedName = selectedSubagent.DisplayName;
            await _gateway.DeleteSubagentAsync(subagentId, mutation.CancellationToken)
                .WaitAsync(mutation.CancellationToken);
            await RunOnUiThreadAsync(() =>
            {
                if (!_requests.IsCurrent(mutation)
                    || intentRevision != IntentRevision)
                {
                    return;
                }

                _drafts.Remove(subagentId);
                DiscardPendingSubagentRefresh();
                var clearCurrentCompactSelection = layoutRevision == LayoutRevision && IsCompactLayout;
                if (clearCurrentCompactSelection)
                {
                    _listDetail.ShowList();
                    ClearStatus();
                }
                else
                {
                    SetStatus($"Deleted subagent '{deletedName}'.", SubagentStatusKind.Success, autoClear: true);
                }
                _listDetail.Reconcile(Subagents.Where(subagent => !string.Equals(
                    subagent.SubagentId,
                    subagentId,
                    StringComparison.OrdinalIgnoreCase)).ToArray());
            });
        }
        catch (OperationCanceledException) when (mutation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await RunOnUiThreadAsync(() =>
            {
                if (_requests.IsCurrent(mutation))
                {
                    SetStatus(ex.Message, SubagentStatusKind.Error);
                }
            });
        }
        finally
        {
            _requests.Complete(mutation);
            await RunOnUiThreadAsync(() => EndOperation(operation));
        }
    }

    private bool CanEditSubagent() => SelectedSubagent is not null && !IsBusy;

    private bool CanSaveSubagent() => CanSaveSelectedSubagent;

    [RelayCommand(CanExecute = nameof(CanLeaveSubagentDetail))]
    private void BackToSubagentList()
    {
        UpdateCurrentDraft();
        ShowSubagentListFromUserIntent();
    }

    private bool CanLeaveSubagentDetail() => !_disposed;

    [RelayCommand]
    private async Task ReloadSubagentChatProvidersAsync()
    {
        OperationGeneration operation = default;
        var started = false;
        ModelBindingSelection? selection = null;
        await RunOnUiThreadAsync(() =>
        {
            if (SelectedSubagent is null)
            {
                return;
            }

            selection = ChatBinding.Selection;
            operation = BeginOperation(SubagentOperation.ReloadProviders);
            started = true;
        });
        if (!started)
        {
            return;
        }

        try
        {
            await ChatBinding.RefreshAsync(selection!, _lifetimeCancellation.Token)
                .WaitAsync(_lifetimeCancellation.Token);
            await RunOnUiThreadAsync(() =>
            {
                UpdateCurrentDraft();
                ClearStatus();
            });
        }
        catch (Exception ex)
        {
            await RunOnUiThreadAsync(() => SetStatus(ex.Message, SubagentStatusKind.Error));
        }
        finally
        {
            await RunOnUiThreadAsync(() => EndOperation(operation));
        }
    }

    [RelayCommand]
    private async Task OpenSelectedChatProviderSettingsAsync()
    {
        string? packageId = null;
        await RunOnUiThreadAsync(() => packageId = ChatBinding.SelectedProvider?.PackageId);
        await OpenProviderSettingsAsync(packageId);
    }

    [RelayCommand]
    private void OpenSubagentEditor(SubagentRecord? subagent)
    {
        if (subagent is not null)
        {
            ActivateSubagent(subagent);
        }
    }

    public void ActivateSubagent(SubagentRecord subagent)
    {
        if (!CanNavigateSubagents)
        {
            return;
        }

        ShowSubagentFromUserIntent(subagent);
    }

    private OperationGeneration BeginOperation(SubagentOperation operation)
    {
        var generation = _operation.Begin(operation, canCancel: false);
        NotifyOperationStateChanged();
        return generation;
    }

    private void EndOperation(OperationGeneration generation)
    {
        _operation.TryComplete(generation);
        NotifyOperationStateChanged();
    }

    private void NotifyOperationStateChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        NotifyDescriptionStateChanged();
        DeleteSubagentCommand.NotifyCanExecuteChanged();
    }

    private void DiscardPendingSubagentRefresh()
    {
        _runtimeRefresh.DiscardPending();
        _requests.Invalidate(ListRefreshChannel);
    }
}

internal enum SubagentOperation
{
    Create,
    Save,
    Delete,
    ReloadProviders,
    RefreshCapabilities,
}
