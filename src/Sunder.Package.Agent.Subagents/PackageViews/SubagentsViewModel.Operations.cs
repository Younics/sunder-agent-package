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

        CancelPendingMutation();
        var intentRevision = _listDetail.ShowNewDetail();
        var mutation = _requests.Begin(MutationChannel, _lifetimeCancellation.Token);
        var operation = BeginOperation(SubagentOperation.Create);
        Task hydration = Task.CompletedTask;
        try
        {
            var created = await _gateway.CreateSubagentAsync(
                "New Subagent",
                mutation.CancellationToken)
                .WaitAsync(mutation.CancellationToken);
            DiscardPendingSubagentRefresh();
            await _uiDispatcher.InvokeAsync(() =>
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
            }).ConfigureAwait(false);
            await hydration.WaitAsync(mutation.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (mutation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_requests.IsCurrent(mutation))
            {
                SetStatus(ex.Message, SubagentStatusKind.Error);
            }
        }
        finally
        {
            _requests.Complete(mutation);
            EndOperation(operation);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveSubagent))]
    private async Task SaveSubagentAsync()
    {
        var selected = SelectedSubagent;
        if (_gateway is null || selected is null || !CanSaveSubagent())
        {
            return;
        }

        var intentRevision = IntentRevision;
        var layoutRevision = LayoutRevision;
        var editRevision = _editRevision;
        var request = new SubagentSaveRequest(
            selected.SubagentId,
            DisplayName,
            Description,
            Instructions,
            ChatBinding.SelectedProvider?.Id,
            ChatBinding.SelectedModel?.Id,
            Capabilities.Assignments,
            ChatBinding.SettingsJson);
        var mutation = _requests.Begin(MutationChannel, _lifetimeCancellation.Token);
        var operation = BeginOperation(SubagentOperation.Save);
        try
        {
            var saved = await _gateway.SaveSubagentAsync(request, mutation.CancellationToken)
                .WaitAsync(mutation.CancellationToken);
            if (!_requests.IsCurrent(mutation)
                || intentRevision != IntentRevision
                || !string.Equals(
                    SelectedSubagent?.SubagentId,
                    selected.SubagentId,
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
        }
        catch (OperationCanceledException) when (mutation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_requests.IsCurrent(mutation))
            {
                SetStatus(ex.Message, SubagentStatusKind.Error);
            }
        }
        finally
        {
            _requests.Complete(mutation);
            EndOperation(operation);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditSubagent))]
    private async Task DeleteSubagentAsync()
    {
        var selected = SelectedSubagent;
        if (_gateway is null || selected is null)
        {
            return;
        }

        var intentRevision = IntentRevision;
        var layoutRevision = LayoutRevision;
        var mutation = _requests.Begin(MutationChannel, _lifetimeCancellation.Token);
        var operation = BeginOperation(SubagentOperation.Delete);
        try
        {
            var subagentId = selected.SubagentId;
            var deletedName = selected.DisplayName;
            await _gateway.DeleteSubagentAsync(subagentId, mutation.CancellationToken)
                .WaitAsync(mutation.CancellationToken);
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
        }
        catch (OperationCanceledException) when (mutation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_requests.IsCurrent(mutation))
            {
                SetStatus(ex.Message, SubagentStatusKind.Error);
            }
        }
        finally
        {
            _requests.Complete(mutation);
            EndOperation(operation);
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
        if (SelectedSubagent is null)
        {
            return;
        }

        var operation = BeginOperation(SubagentOperation.ReloadProviders);
        try
        {
            await ChatBinding.RefreshAsync(ChatBinding.Selection, _lifetimeCancellation.Token)
                .WaitAsync(_lifetimeCancellation.Token);
            UpdateCurrentDraft();
            ClearStatus();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, SubagentStatusKind.Error);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    [RelayCommand]
    private Task OpenSelectedChatProviderSettingsAsync()
        => OpenProviderSettingsAsync(ChatBinding.SelectedProvider?.PackageId);

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
