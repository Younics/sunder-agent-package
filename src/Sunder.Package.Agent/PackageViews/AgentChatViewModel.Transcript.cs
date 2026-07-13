using System.ComponentModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel
{
    private void RefreshTranscript()
    {
        var displayedSession = DisplayedSession;
        if (displayedSession is null)
        {
            _timeline.ClearSession();
            StatusText = string.IsNullOrWhiteSpace(_globalStatusText)
                ? GetSetupStatusText()
                : _globalStatusText;
            return;
        }

        var ticket = _timeline.BeginInitialLoad(displayedSession.SessionId);
        StatusText = "Loading transcript...";
        if (Application.Current is null)
        {
            var turns = _sessionService.ListRecentTurns(
                displayedSession.SessionId,
                InitialTranscriptTurnLimit + 1);
            CompleteTranscriptRefresh(displayedSession, ticket, turns);
            return;
        }

        _backgroundTasks.Run(_ => RefreshTranscriptAsync(displayedSession, ticket));
    }

    private async Task RefreshTranscriptAsync(
        AgentSessionListItemViewModel displayedSession,
        TranscriptLoadTicket ticket)
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            ticket.Generation.CancellationToken.ThrowIfCancellationRequested();
            var turns = await Task.Run(
                () => _sessionService.ListRecentTurns(
                    displayedSession.SessionId,
                    InitialTranscriptTurnLimit + 1),
                ticket.Generation.CancellationToken);
            await Dispatcher.UIThread.InvokeAsync(
                () => CompleteTranscriptRefresh(displayedSession, ticket, turns),
                DispatcherPriority.Background);
        }
        catch (OperationCanceledException) when (ticket.Generation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_timeline.TryFailInitialLoad(ticket))
                {
                    StatusText = $"Unable to load transcript: {ex.Message}";
                }
            }, DispatcherPriority.Background);
        }
    }

    private void CompleteTranscriptRefresh(
        AgentSessionListItemViewModel displayedSession,
        TranscriptLoadTicket ticket,
        IReadOnlyList<AgentTurnRecord> turns)
    {
        if (!_timeline.TryCompleteInitialLoad(ticket, turns))
        {
            return;
        }

        ReloadPendingPermissionRequests();
        TrackCheckpointActivity(_sessionService.GetLatestCheckpoint(displayedSession.SessionId));
        ApplyRunActivityState();
        UpdateSessionState(displayedSession.SessionId, markUnread: false);
        displayedSession.ClearUnreadActivity();
        StatusText = displayedSession.StatusText;
    }

    public async Task<bool> LoadOlderTranscriptRowsAsync(
        object? protectedAnchorKey = null,
        CancellationToken cancellationToken = default)
    {
        var loaded = await _timeline.LoadOlderAsync(
            async (sessionId, beforeCreatedAt, beforeTurnId, limit, pageCancellationToken) =>
            {
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    pageCancellationToken);
                var turns = await Task.Run(
                    () => _sessionService.ListTurnsBefore(
                        sessionId,
                        beforeCreatedAt,
                        beforeTurnId,
                        limit),
                    linkedCancellation.Token);
                linkedCancellation.Token.ThrowIfCancellationRequested();
                return turns;
            },
            protectedAnchorKey);
        if (loaded)
        {
            ApplyRunActivityState();
        }

        return loaded;
    }

    public async Task<bool> LoadNewerTranscriptRowsAsync(
        object? protectedAnchorKey = null,
        CancellationToken cancellationToken = default)
    {
        var loaded = await _timeline.LoadNewerAsync(
            async (sessionId, afterCreatedAt, afterTurnId, limit, pageCancellationToken) =>
            {
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    pageCancellationToken);
                var turns = await Task.Run(
                    () => _sessionService.ListTurnsAfter(
                        sessionId,
                        afterCreatedAt,
                        afterTurnId,
                        limit),
                    linkedCancellation.Token);
                linkedCancellation.Token.ThrowIfCancellationRequested();
                return turns;
            },
            protectedAnchorKey);
        if (loaded)
        {
            _runActivity.NotifyFollowStateChanged();
            ApplyRunActivityState();
        }

        return loaded;
    }

    internal void ReportTranscriptPagingFailure(Exception exception)
    {
        if (!_disposed)
        {
            StatusText = $"Unable to load transcript: {exception.Message}";
        }
    }

    [RelayCommand]
    private void JumpToLatestTranscript()
    {
        if (_timeline.RequestJumpToLatest())
        {
            RefreshTranscript();
        }
    }

    public void DetachTranscriptFromLatest()
    {
        _timeline.DetachFromLatest();
        _runActivity.NotifyFollowStateChanged();
    }

    public void ResumeTranscriptFollowingLatestIfCaughtUp()
    {
        if (_timeline.ResumeFollowingLatestIfCaughtUp())
        {
            _runActivity.NotifyFollowStateChanged();
            ApplyRunActivityState();
            _timeline.NotifyRowsChanged();
        }
    }

    internal void SetTranscriptJumpToLatestVisible(bool isVisible)
        => _timeline.SetJumpToLatestVisible(isVisible);

    internal void SetTranscriptViewportAnchor(TranscriptViewportAnchorData? anchor)
        => _timeline.SetViewportAnchor(anchor);

    internal void SetTranscriptRowExpanded(AgentTranscriptRowViewModel row, bool isExpanded)
        => _timeline.SetRowExpanded(row, isExpanded);

    private void OnTurnChanged(Guid sessionId, AgentTurnRecord turn)
        => RunOnUiThread(() =>
        {
            if (DisplayedSession?.SessionId == sessionId)
            {
                _timeline.ApplyLiveTurn(turn);
                ApplyRunActivityState();
            }
        });

    private void OnTranscriptReset(Guid sessionId)
        => RunOnUiThread(() =>
        {
            if (DisplayedSession?.SessionId == sessionId)
            {
                RefreshTranscript();
            }
        });

    private void OnRunActivityChanged(Guid sessionId, AgentRunActivityUpdate activity)
        => RunOnUiThread(() => ApplyRunActivityChanged(sessionId, activity));

    private void ApplyRunActivityChanged(Guid sessionId, AgentRunActivityUpdate activity)
    {
        if (DisplayedSession?.SessionId != sessionId
            || !IsDisplayedSessionRunActive
            || !_timeline.IsFollowingLatest)
        {
            return;
        }

        var checkpoint = _sessionService.GetLatestCheckpoint(sessionId);
        if (checkpoint?.Status != AgentRunStatus.Running
            || checkpoint.RunRevision != activity.RunRevision)
        {
            return;
        }

        _runActivity.TrackUpdate(
            activity.Text,
            activity.Kind == AgentRunActivityKind.Reasoning);
    }

    private void OnTimelineTurnProjected(
        AgentTurnRecord turn,
        bool trackRunActivity,
        bool scheduleQuietTimer)
    {
        if (trackRunActivity)
        {
            _runActivity.TrackTurn(turn, scheduleQuietTimer);
        }
    }

    private void OnRunActivityStateChanged()
    {
        ApplyRunActivityState();
        _timeline.NotifyRowsChanged();
    }

    private void ApplyRunActivityState()
        => _timeline.ApplyActivity(
            _runActivity.Text,
            _runActivity.IsReasoning,
            _runActivity.ShouldShow);

    private void UpdateActivityRowForCurrentState() => ApplyRunActivityState();

    private void TrackCheckpointActivity(AgentRunCheckpointRecord? checkpoint)
        => _runActivity.TrackCheckpoint(checkpoint);

    private void OnTimelinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TranscriptTimelineState<AgentTranscriptRowViewModel>.HasOlderRows):
                OnPropertyChanged(nameof(HasOlderTranscriptRows));
                break;
            case nameof(TranscriptTimelineState<AgentTranscriptRowViewModel>.HasNewerRows):
                OnPropertyChanged(nameof(HasNewerTranscriptRows));
                break;
            case nameof(TranscriptTimelineState<AgentTranscriptRowViewModel>.IsLoadingOlder):
                OnPropertyChanged(nameof(IsLoadingOlderTranscriptRows));
                break;
            case nameof(TranscriptTimelineState<AgentTranscriptRowViewModel>.IsLoadingNewer):
                OnPropertyChanged(nameof(IsLoadingNewerTranscriptRows));
                break;
            case nameof(TranscriptTimelineState<AgentTranscriptRowViewModel>.IsInitialLoading):
                OnPropertyChanged(nameof(IsTranscriptLoading));
                break;
        }

        OnPropertyChanged(nameof(CanLoadOlderTranscriptRows));
        OnPropertyChanged(nameof(CanLoadNewerTranscriptRows));
    }

    private void RefreshVisibleChildSessionLinks()
        => _timeline.Projector.RefreshRelatedRows();

    private IReadOnlyList<AgentChildSessionLinkViewModel> ResolveChildSessionLinksFromStore(
        AgentTurnRecord turn,
        AgentTurnItemRecord item)
    {
        if (!IsSubagentTool(item.ToolId) || string.IsNullOrWhiteSpace(item.CallId))
        {
            return [];
        }

        var parentSession = _sessionService.GetSession(turn.SessionId);
        if (parentSession is null)
        {
            return [];
        }

        return _sessionService.ListSessions()
            .Where(session => session.ParentSessionId == parentSession.SessionId
                              && string.Equals(
                                  session.ParentToolCallId,
                                  item.CallId,
                                  StringComparison.Ordinal))
            .OrderBy(session => session.CreatedAtUtc)
            .Select(CreateChildSessionLink)
            .ToArray();
    }

    private AgentChildSessionLinkViewModel CreateChildSessionLink(AgentSessionRecord childSession)
    {
        var childProfile = string.IsNullOrWhiteSpace(childSession.ProfileId)
            ? null
            : _profileService.GetProfile(childSession.ProfileId);
        return new AgentChildSessionLinkViewModel(
            childSession.SessionId,
            childSession.Title,
            FormatChildSessionSubtitle(childProfile?.DisplayName, childSession.AgentKind),
            _sessionService.GetLatestCheckpoint(childSession.SessionId)?.Status
            ?? AgentRunStatus.Idle);
    }

    private static bool IsSubagentTool(string? toolId)
        => string.Equals(toolId, "task", StringComparison.OrdinalIgnoreCase)
           || string.Equals(toolId, "delegate_tasks", StringComparison.OrdinalIgnoreCase);

    private static string FormatChildSessionSubtitle(
        string? profileDisplayName,
        string? agentKind)
    {
        var name = string.IsNullOrWhiteSpace(profileDisplayName)
            ? agentKind
            : profileDisplayName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Subagent";
        }

        return string.Equals(agentKind, "subagent", StringComparison.OrdinalIgnoreCase)
               && !name.EndsWith("subagent", StringComparison.OrdinalIgnoreCase)
            ? $"{name} subagent"
            : name;
    }

    private string ResolveTurnSenderDisplayName(AgentTurnRecord turn)
    {
        var session = _sessionService.GetSession(turn.SessionId);
        var profileId = session?.ProfileId;
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            var profile = Profiles.FirstOrDefault(profile => string.Equals(
                    profile.ProfileId,
                    profileId,
                    StringComparison.OrdinalIgnoreCase))
                ?? _profileService.GetProfile(profileId);
            if (!string.IsNullOrWhiteSpace(profile?.DisplayName))
            {
                return profile.DisplayName.Trim();
            }
        }

        var hasSpecificAgentKind = !string.IsNullOrWhiteSpace(session?.AgentKind)
            && !string.Equals(session.AgentKind, "agent", StringComparison.OrdinalIgnoreCase);
        if (SelectedProfile is not null
            && !hasSpecificAgentKind
            && (session is null
                || string.IsNullOrWhiteSpace(session.ProfileId)
                || string.Equals(
                    session.ProfileId,
                    SelectedProfile.ProfileId,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return SelectedProfile.DisplayName;
        }

        return !string.IsNullOrWhiteSpace(session?.AgentKind)
            ? session.AgentKind.Trim()
            : "Agent";
    }
}
