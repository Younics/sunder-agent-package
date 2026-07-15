using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel
{
    partial void OnSelectedProfileChanged(AgentProfileRecord? value)
    {
        if (_isApplyingChatSnapshot)
        {
            return;
        }

        _globalStatusText = string.Empty;
        CreateSessionCommand.NotifyCanExecuteChanged();
        RefreshSetupState();
        ScheduleChatSnapshotRequest(
            value?.ProfileId,
            SelectedWorkspace?.WorkspaceId,
            SelectedSession?.SessionId);
    }

    private void OnProfilesChanged(string profileId)
    {
        if (!_isInitialized)
        {
            return;
        }

        ScheduleChatSnapshotRequest(
            SelectedProfile?.ProfileId,
            SelectedWorkspace?.WorkspaceId,
            SelectedSession?.SessionId);
    }

    private void NotifyProfileStateChanged()
    {
        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(HasNoProfiles));
    }
}
