using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Runtime;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentProfilesViewModel
{
    [RelayCommand(CanExecute = nameof(CanNavigateProfiles))]
    private async Task CreateProfileAsync()
    {
        var operation = BeginOperation(AgentProfileOperation.Create);
        try
        {
            AgentProfileRecord created;
            _suppressProfileChangeNotifications = true;
            try
            {
                created = await _profileService.CreateProfileAsync("New Agent");
            }
            finally
            {
                _suppressProfileChangeNotifications = false;
            }

            await ReloadProfilesAsync(created.ProfileId);
            IsEditorActive = true;
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
            _suppressProfileChangeNotifications = true;
            try
            {
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
            }
            finally
            {
                _suppressProfileChangeNotifications = false;
            }

            _drafts.Remove(profileId);
            OnPropertyChanged(nameof(IsDirty));
            var shouldClearSelection = IsCompactLayout;
            await ReloadProfilesAsync(profileId);
            if (shouldClearSelection)
            {
                SelectedProfile = null;
                ClearStatus();
            }
            else
            {
                SetStatus("Profile saved.", AgentProfileStatusKind.Success, autoClear: true);
            }

            IsEditorActive = false;
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
            _suppressProfileChangeNotifications = true;
            try
            {
                _profileService.DeleteProfile(profileId);
            }
            finally
            {
                _suppressProfileChangeNotifications = false;
            }

            _drafts.Remove(profileId);
            await ReloadProfilesAsync(selectProfileId: null);
            if (shouldClearSelection)
            {
                SelectedProfile = null;
                ClearStatus();
            }
            else
            {
                SetStatus($"Deleted profile '{deletedName}'.", AgentProfileStatusKind.Success, autoClear: true);
            }

            IsEditorActive = false;
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

    [RelayCommand(CanExecute = nameof(CanNavigateProfiles))]
    private void BackToProfileList()
    {
        UpdateCurrentDraft();
        if (IsCompactLayout)
        {
            SelectedProfile = null;
        }

        IsEditorActive = false;
    }

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
                EmbeddingBinding.RefreshAsync(EmbeddingBinding.Selection, _lifetimeCancellation.Token));
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

        if (!string.Equals(
            SelectedProfile?.ProfileId,
            profile.ProfileId,
            StringComparison.OrdinalIgnoreCase))
        {
            SelectedProfile = profile;
        }

        if (IsCompactLayout)
        {
            IsEditorActive = true;
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var initializationTask = _profileService is IAgentPresentationInitialization initialization
                ? initialization.InitializeAsync(cancellationToken)
                : Task.CompletedTask;
            await Task.WhenAll(
                initializationTask,
                _profileService.ListInstalledLocalToolsAsync(cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Task reload = Task.CompletedTask;
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    reload = ReloadProfilesAsync(selectProfileId: null);
                }
            }).ConfigureAwait(false);
            await reload.WaitAsync(cancellationToken).ConfigureAwait(false);
            await _uiDispatcher.InvokeAsync(() => _isInitialized = !_disposed).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    ClearEditor();
                    SetStatus(ex.Message, AgentProfileStatusKind.Error);
                }
            }).ConfigureAwait(false);
        }
    }

    private async Task ReloadProfilesAsync(string? selectProfileId)
    {
        AgentProfileRecord? profileToLoad = null;
        var currentProfileId = SelectedProfile?.ProfileId;
        SetSelectionSilently(() =>
        {
            Profiles.Clear();
            foreach (var profile in _profileService.ListProfiles())
            {
                Profiles.Add(profile);
            }

            var selected = Profiles.FirstOrDefault(profile => string.Equals(
                profile.ProfileId,
                selectProfileId,
                StringComparison.OrdinalIgnoreCase));
            if (selected is null && (!IsCompactLayout || selectProfileId is not null))
            {
                selected = Profiles.FirstOrDefault(profile => string.Equals(
                        profile.ProfileId,
                        currentProfileId,
                        StringComparison.OrdinalIgnoreCase))
                    ?? Profiles.FirstOrDefault();
            }

            SelectedProfile = selected;
        });

        if (SelectedProfile is null)
        {
            ClearEditor();
            SetStatus(
                Profiles.Count == 0 ? "No profiles available." : string.Empty,
                Profiles.Count == 0 ? AgentProfileStatusKind.Warning : AgentProfileStatusKind.None);
        }
        else
        {
            profileToLoad = SelectedProfile;
        }

        if (profileToLoad is not null)
        {
            await LoadSelectedProfileAsync(profileToLoad, ++_profileLoadVersion);
        }
    }
}
