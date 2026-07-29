using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Runtime;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentProfilesViewModel
{
    [RelayCommand(CanExecute = nameof(CanNavigateProfiles))]
    private async Task CreateProfileAsync()
    {
        CancelPendingMutation();
        var intentRevision = _listDetail.ShowNewDetail();
        var mutation = _requests.Begin(MutationChannel, _lifetimeCancellation.Token);
        var operation = BeginOperation(AgentProfileOperation.Create);
        Task hydration = Task.CompletedTask;
        try
        {
            var created = await _profileService.CreateProfileAsync(
                "New Agent",
                mutation.CancellationToken)
                .WaitAsync(mutation.CancellationToken);
            DiscardPendingProfileRefresh();
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_requests.IsCurrent(mutation))
                {
                    return;
                }

                _listDetail.Reconcile(_profileService.ListProfiles());
                if (_listDetail.TryShowCreatedDetail(created.ProfileId, intentRevision))
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
                SetStatus(ex.Message, AgentProfileStatusKind.Error);
            }
        }
        finally
        {
            _requests.Complete(mutation);
            EndOperation(operation);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditProfile))]
    private async Task SaveProfileAsync()
    {
        if (!CanEditProfile() || SelectedProfile is null)
        {
            return;
        }

        var operation = BeginOperation(AgentProfileOperation.Save);
        try
        {
            var profileId = SelectedProfile.ProfileId;
            _profileService.SaveProfile(
                profileId,
                string.IsNullOrWhiteSpace(DisplayName) ? "Unnamed Profile" : DisplayName.Trim(),
                Normalize(Description),
                Normalize(Instructions),
                ChatBinding.SelectedProvider?.Id,
                ChatBinding.SelectedModel?.Id,
                CanConfigureEmbeddings ? EmbeddingBinding.SelectedProvider?.Id : null,
                CanConfigureEmbeddings ? EmbeddingBinding.SelectedModel?.Id : null,
                Capabilities.Assignments,
                SelectedBehaviorLoop?.LoopId ?? string.Empty,
                SelectedBehaviorLoop?.SourceId ?? string.Empty,
                SelectedProfile.BehaviorLoopSettingsJson ?? string.Empty,
                ChatBinding.SettingsJson ?? string.Empty);

            _drafts.Remove(profileId);
            OnPropertyChanged(nameof(IsDirty));
            DiscardPendingProfileRefresh();
            if (IsCompactLayout)
            {
                _listDetail.ShowList();
                ClearStatus();
            }
            else
            {
                SetStatus("Profile saved.", AgentProfileStatusKind.Success, autoClear: true);
            }
            _listDetail.Reconcile(_profileService.ListProfiles());
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentProfileStatusKind.Error);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditProfile))]
    private async Task DeleteProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var operation = BeginOperation(AgentProfileOperation.Delete);
        try
        {
            var profileId = SelectedProfile.ProfileId;
            var deletedName = SelectedProfile.DisplayName;
            var shouldClearSelection = IsCompactLayout;
            _profileService.DeleteProfile(profileId);

            _drafts.Remove(profileId);
            DiscardPendingProfileRefresh();
            if (shouldClearSelection)
            {
                _listDetail.ShowList();
                ClearStatus();
            }
            else
            {
                SetStatus($"Deleted profile '{deletedName}'.", AgentProfileStatusKind.Success, autoClear: true);
            }
            _listDetail.Reconcile(_profileService.ListProfiles());
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentProfileStatusKind.Error);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    private bool CanEditProfile() => SelectedProfile is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanLeaveProfileDetail))]
    private void BackToProfileList()
    {
        UpdateCurrentDraft();
        ShowProfileListFromUserIntent();
    }

    private bool CanLeaveProfileDetail() => !_disposed;

    [RelayCommand]
    private async Task ReloadProfileProvidersAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var operation = BeginOperation(AgentProfileOperation.ReloadProviders);
        try
        {
            await Task.WhenAll(
                    ChatBinding.RefreshAsync(ChatBinding.Selection, _lifetimeCancellation.Token),
                    EmbeddingBinding.RefreshAsync(EmbeddingBinding.Selection, _lifetimeCancellation.Token))
                .WaitAsync(_lifetimeCancellation.Token);
            UpdateCurrentDraft();
            ClearStatus();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentProfileStatusKind.Error);
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
    private Task OpenSelectedEmbeddingProviderSettingsAsync()
        => OpenProviderSettingsAsync(EmbeddingBinding.SelectedProvider?.PackageId);

    [RelayCommand]
    private void OpenProfileEditor(AgentProfileRecord? profile)
    {
        if (profile is not null)
        {
            ActivateProfile(profile);
        }
    }

    public void ActivateProfile(AgentProfileRecord profile)
    {
        if (!CanNavigateProfiles)
        {
            return;
        }

        ShowProfileFromUserIntent(profile);
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        var initializationTask = _profileService is IAgentPresentationInitialization initialization
            ? initialization.InitializeAsync(cancellationToken)
            : Task.CompletedTask;
        await Task.WhenAll(
                initializationTask,
                _profileService.ListInstalledLocalToolsAsync(cancellationToken))
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        while (!await TryRefreshProfileListAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        await _currentDetailLoad.WaitAsync(cancellationToken).ConfigureAwait(false);
        var replayPendingRefresh = false;
        await _uiDispatcher.InvokeAsync(() =>
        {
            _isInitialized = !_disposed;
            replayPendingRefresh = _initializationRefreshPending;
            _initializationRefreshPending = false;
        }).ConfigureAwait(false);
        if (replayPendingRefresh)
        {
            await _runtimeRefresh.MarkDirty().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void DiscardPendingProfileRefresh()
    {
        _runtimeRefresh.DiscardPending();
        _requests.Invalidate(ListRefreshChannel);
    }
}
