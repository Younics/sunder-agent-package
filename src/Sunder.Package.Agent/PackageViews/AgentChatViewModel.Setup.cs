namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel
{
    private void RefreshSetupState()
    {
        var (title, description) = GetSetupContent();
        SetupTitle = title;
        SetupDescription = description;
        if (SelectedSession is null)
        {
            StatusText = string.IsNullOrWhiteSpace(_globalStatusText)
                ? description
                : _globalStatusText;
        }
        else if (!CanUseChat)
        {
            StatusText = string.IsNullOrWhiteSpace(_globalStatusText)
                ? GetSetupStatusText()
                : _globalStatusText;
        }

        OnPropertyChanged(nameof(CanUseChat));
        OnPropertyChanged(nameof(CannotUseChat));
        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(HasNoProfiles));
        OnPropertyChanged(nameof(HasSelectedSession));
        OnPropertyChanged(nameof(ShowSetupInstructions));
        OnPropertyChanged(nameof(ShowTranscriptSurface));
        OnPropertyChanged(nameof(ShowCollapsedComposer));
        OnPropertyChanged(nameof(ShowExpandedComposer));
        OnPropertyChanged(nameof(IsSelectedSessionRunInactive));
        SendMessageCommand.NotifyCanExecuteChanged();
    }

    private (string Title, string Description) GetSetupContent()
    {
        if (Profiles.Count == 0)
        {
            return (
                "Create an agent before chatting",
                "Agents choose model settings, instructions, and runtime capabilities. Create one in Agents, then return here to chat."
            );
        }

        if (Workspaces.Count == 0)
        {
            return (
                "Create a workspace before chatting",
                "Workspaces choose the execution environment used by sessions. Open Workspaces and create one to start chatting."
            );
        }

        if (SelectedWorkspace is null)
        {
            return (
                "Select a workspace",
                "Choose the workspace this session should run against. You can switch workspaces without changing sessions."
            );
        }

        return (
            "Create a session to start chatting",
            "Create or select a session to begin a conversation with the selected agent."
        );
    }

    private string GetSetupStatusText()
    {
        if (Profiles.Count == 0)
        {
            return "Create an Agent before chatting.";
        }

        if (Workspaces.Count == 0)
        {
            return "Create a workspace before chatting. Workspaces choose the execution environment used by sessions.";
        }

        if (SelectedWorkspace is null)
        {
            return "Select a workspace to run the selected session.";
        }

        if (SelectedProfile is null)
        {
            return "Select an Agent before chatting.";
        }

        return "Create a session to start chatting.";
    }

    private void SetGlobalStatus(string statusText)
    {
        _globalStatusText = statusText;
        if (SelectedSession is null)
        {
            StatusText = statusText;
        }
    }
}
