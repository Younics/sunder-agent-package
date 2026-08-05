using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Services;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel
{
    [RelayCommand(CanExecute = nameof(CanCreateSession))]
    private async Task CreateSessionAsync()
    {
        var profile = SelectedProfile;
        var workspace = SelectedWorkspace;
        if (profile is null)
        {
            SetGlobalStatus("Create an Agent before starting a session.");
            return;
        }
        if (workspace is null)
        {
            SetGlobalStatus("Select a workspace before starting a session.");
            return;
        }

        var workspaceId = NormalizeSelectedWorkspaceId(workspace.WorkspaceId);
        if (workspaceId is null || IsUnassignedSessionsWorkspace(workspaceId))
        {
            SetGlobalStatus("Select a workspace before starting a session.");
            return;
        }

        var title = AgentSessionTitleDefaults.CreateNextTitle(ListMainSessions());
        var session = _chatSessionCommandGateway is null
            ? _sessionService.CreateSession(
                title,
                profileId: profile.ProfileId,
                behaviorLoopId: profile.BehaviorLoopId,
                workspaceId: workspaceId)
            : (await _chatSessionCommandGateway.CreateRootSessionAsync(
                title,
                profile.ProfileId,
                profile.BehaviorLoopId,
                workspaceId,
                _lifetimeCancellation.Token).ConfigureAwait(false)).Session;
        await InvokeOnUiThreadAsync(() =>
        {
            if (!string.Equals(session.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Created session workspace did not match the selected workspace.");
            }

            ScheduleChatSnapshotRequest(profile.ProfileId, workspaceId, session.SessionId);
        }).ConfigureAwait(false);
    }

    [RelayCommand]
    private void BeginRenameSession(AgentSessionListItemViewModel? session)
    {
        if (session is null)
        {
            return;
        }
        foreach (var item in Sessions)
        {
            if (!ReferenceEquals(item, session) && item.IsRenameActive)
            {
                item.CancelRename();
            }
        }
        session.BeginRename();
    }

    [RelayCommand]
    private async Task SaveSessionRenameAsync(AgentSessionListItemViewModel? session)
    {
        if (session is null)
        {
            return;
        }

        var updated = session.Session with
        {
            Title = string.IsNullOrWhiteSpace(session.RenameTitle)
                ? "Unnamed Session"
                : session.RenameTitle.Trim(),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        if (_chatSessionCommandGateway is null)
        {
            _sessionService.UpdateSession(updated);
        }
        else
        {
            _ = await _chatSessionCommandGateway.UpdateSessionAsync(
                updated,
                _lifetimeCancellation.Token).ConfigureAwait(false);
        }
        await InvokeOnUiThreadAsync(() =>
        {
            ScheduleChatSnapshotRequest(
                SelectedProfile?.ProfileId,
                SelectedWorkspace?.WorkspaceId,
                updated.SessionId);
            session.CancelRename();
        }).ConfigureAwait(false);
    }

    [RelayCommand]
    private static void CancelSessionRename(AgentSessionListItemViewModel? session)
        => session?.CancelRename();

    [RelayCommand]
    private async Task DeleteSessionAsync(AgentSessionListItemViewModel? session)
    {
        if (session is null)
        {
            return;
        }

        session.CancelRename();
        if (_chatSessionCommandGateway is null)
        {
            _sessionService.DeleteSession(session.SessionId);
        }
        else
        {
            await _chatSessionCommandGateway.DeleteSessionAsync(
                session.SessionId,
                _lifetimeCancellation.Token).ConfigureAwait(false);
        }
        await InvokeOnUiThreadAsync(() =>
            ScheduleChatSnapshotRequest(
                SelectedProfile?.ProfileId,
                SelectedWorkspace?.WorkspaceId,
                preferredSessionId: null)).ConfigureAwait(false);
    }

    private bool CanCreateSession()
        => SelectedProfile is not null
           && NormalizeSelectedWorkspaceId(SelectedWorkspace?.WorkspaceId) is { } workspaceId
           && !IsUnassignedSessionsWorkspace(workspaceId);

    private static string? NormalizeSelectedWorkspaceId(string? workspaceId)
        => string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId.Trim();

    private static bool IsUnassignedSessionsWorkspace(string workspaceId)
        => string.Equals(
            workspaceId,
            AgentWorkspaceService.UnassignedSessionsWorkspaceId,
            StringComparison.OrdinalIgnoreCase);
}
