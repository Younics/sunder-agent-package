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

        var operation = BeginOperation(SubagentOperation.Create);
        try
        {
            SubagentRecord created;
            _suppressSubagentChangeNotifications = true;
            try
            {
                created = await _gateway.CreateSubagentAsync("New Subagent");
            }
            finally
            {
                _suppressSubagentChangeNotifications = false;
            }

            await ReloadAsync(created.SubagentId);
            IsEditorActive = true;
            ClearStatus();
        }
        finally
        {
            EndOperation(operation);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveSubagent))]
    private async Task SaveSubagentAsync()
    {
        if (_gateway is null || SelectedSubagent is null || !CanSaveSubagent())
        {
            return;
        }

        var operation = BeginOperation(SubagentOperation.Save);
        try
        {
            SubagentRecord saved;
            _suppressSubagentChangeNotifications = true;
            try
            {
                saved = await _gateway.SaveSubagentAsync(new SubagentSaveRequest(
                    SelectedSubagent.SubagentId,
                    DisplayName,
                    Description,
                    Instructions,
                    ChatBinding.SelectedProvider?.Id,
                    ChatBinding.SelectedModel?.Id,
                    Capabilities.Assignments,
                    ChatBinding.SettingsJson));
            }
            finally
            {
                _suppressSubagentChangeNotifications = false;
            }

            _drafts.Remove(saved.SubagentId);
            OnPropertyChanged(nameof(IsDirty));
            var shouldClearSelection = IsCompactLayout;
            await ReloadAsync(saved.SubagentId);
            if (shouldClearSelection)
            {
                SelectedSubagent = null;
                ClearStatus();
            }
            else
            {
                SetStatus("Subagent saved.", SubagentStatusKind.Success, autoClear: true);
            }

            IsEditorActive = false;
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message, SubagentStatusKind.Error);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditSubagent))]
    private async Task DeleteSubagentAsync()
    {
        if (_gateway is null || SelectedSubagent is null)
        {
            return;
        }

        var operation = BeginOperation(SubagentOperation.Delete);
        try
        {
            var subagentId = SelectedSubagent.SubagentId;
            var deletedName = SelectedSubagent.DisplayName;
            var shouldClearSelection = IsCompactLayout;
            _suppressSubagentChangeNotifications = true;
            try
            {
                await _gateway.DeleteSubagentAsync(subagentId);
            }
            finally
            {
                _suppressSubagentChangeNotifications = false;
            }

            _drafts.Remove(subagentId);
            await ReloadAsync(null);
            if (shouldClearSelection)
            {
                SelectedSubagent = null;
                ClearStatus();
            }
            else
            {
                SetStatus($"Deleted subagent '{deletedName}'.", SubagentStatusKind.Success, autoClear: true);
            }

            IsEditorActive = false;
        }
        finally
        {
            EndOperation(operation);
        }
    }

    private bool CanEditSubagent() => SelectedSubagent is not null && !IsBusy;

    private bool CanSaveSubagent() => CanSaveSelectedSubagent;

    [RelayCommand(CanExecute = nameof(CanNavigateSubagents))]
    private void BackToSubagentList()
    {
        UpdateCurrentDraft();
        if (IsCompactLayout)
        {
            SelectedSubagent = null;
        }

        IsEditorActive = false;
    }

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
            await ChatBinding.RefreshAsync(ChatBinding.Selection);
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

        if (!string.Equals(SelectedSubagent?.SubagentId, subagent.SubagentId, StringComparison.OrdinalIgnoreCase))
        {
            SelectedSubagent = subagent;
        }

        if (IsCompactLayout)
        {
            IsEditorActive = true;
        }
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
}

internal enum SubagentOperation
{
    Create,
    Save,
    Delete,
    ReloadProviders,
    RefreshCapabilities,
}
