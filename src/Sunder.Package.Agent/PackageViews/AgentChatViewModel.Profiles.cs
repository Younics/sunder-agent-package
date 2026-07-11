using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel
{
    partial void OnSelectedProfileChanged(AgentProfileRecord? value)
    {
        _ = _selectionState?.SaveSelectedProfileIdAsync(value?.ProfileId);
        _globalStatusText = string.Empty;
        CreateSessionCommand.NotifyCanExecuteChanged();
        RefreshSetupState();
    }

    private void OnProfilesChanged(string profileId) =>
        RunOnUiThread(
            () =>
                ReloadProfiles(
                    SelectedProfile?.ProfileId
                )
        );

    private void ReloadProfiles(string? selectProfileId)
    {
        var profiles = _profileService.ListProfiles();
        Profiles.Clear();
        foreach (var profile in profiles)
        {
            Profiles.Add(profile);
        }

        var desiredProfileId = selectProfileId ?? SelectedProfile?.ProfileId;
        SelectedProfile =
            Profiles.FirstOrDefault(profile =>
                string.Equals(
                    profile.ProfileId,
                    desiredProfileId,
                    StringComparison.OrdinalIgnoreCase
                )
            ) ?? Profiles.FirstOrDefault();
        _ = _selectionState?.SaveSelectedProfileIdAsync(SelectedProfile?.ProfileId);
        NotifyProfileStateChanged();
        CreateSessionCommand.NotifyCanExecuteChanged();
        RefreshSetupState();
    }

    private void NotifyProfileStateChanged()
    {
        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(HasNoProfiles));
    }
}
