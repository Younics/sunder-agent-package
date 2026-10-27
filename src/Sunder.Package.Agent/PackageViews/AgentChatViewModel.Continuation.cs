using CommunityToolkit.Mvvm.Input;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel
{
    public bool CanContinueRun => !_disposed && CanUseChat && IsSelectedSessionRunInactive
        && !IsSelectedSessionSendPending() && !IsRollbackPending
        && string.IsNullOrWhiteSpace(DraftMessage) && PendingAttachments.Count == 0
        && SelectedSession?.StatusBadgeText is "Interrupted" or "Failed";

    private void NotifyContinuationStateChanged()
    {
        OnPropertyChanged(nameof(CanContinueRun));
        ContinueRunCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanContinueRun))]
    private async Task ContinueRunAsync()
    {
        if (!CanContinueRun) return;
        // An explicit new user instruction, never replay of a failed command or
        // an old tool invocation. The ordinary correlated submission path owns it.
        DraftMessage = "Continue from the saved progress without repeating completed content or completed tool calls. "
            + "If any tool execution was ambiguous or unconfirmed, verify current state before making another change.";
        await SendMessageAsync();
    }
}
