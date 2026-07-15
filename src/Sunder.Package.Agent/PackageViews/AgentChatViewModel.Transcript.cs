using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Runtime;
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
        _backgroundTasks.Run(_ => RefreshTranscriptAsync(displayedSession, ticket));
    }

    private async Task RefreshTranscriptAsync(
        AgentSessionListItemViewModel displayedSession,
        TranscriptLoadTicket ticket)
    {
        try
        {
            ticket.Generation.CancellationToken.ThrowIfCancellationRequested();
            var page = await LoadTranscriptPageAsync(
                new AgentTranscriptPageRequest(
                    displayedSession.SessionId,
                    AgentTranscriptPageDirection.Recent,
                    InitialTranscriptTurnLimit),
                ticket.Generation.CancellationToken).ConfigureAwait(false);
            await InvokeOnUiThreadAsync(
                () => CompleteTranscriptRefresh(displayedSession, ticket, page));
        }
        catch (OperationCanceledException) when (ticket.Generation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await InvokeOnUiThreadAsync(() =>
            {
                if (_timeline.TryFailInitialLoad(ticket))
                {
                    StatusText = $"Unable to load transcript: {ex.Message}";
                }
            });
        }
    }

    private void CompleteTranscriptRefresh(
        AgentSessionListItemViewModel displayedSession,
        TranscriptLoadTicket ticket,
        AgentTranscriptPage page)
    {
        if (!_timeline.TryCompleteInitialLoad(ticket, page.Turns, page.HasMore))
        {
            return;
        }

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
                var page = await LoadTranscriptPageAsync(
                    new AgentTranscriptPageRequest(
                        sessionId,
                        AgentTranscriptPageDirection.Before,
                        limit,
                        beforeCreatedAt,
                        beforeTurnId),
                    linkedCancellation.Token).ConfigureAwait(false);
                linkedCancellation.Token.ThrowIfCancellationRequested();
                return page.Turns;
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
                var page = await LoadTranscriptPageAsync(
                    new AgentTranscriptPageRequest(
                        sessionId,
                        AgentTranscriptPageDirection.After,
                        limit,
                        afterCreatedAt,
                        afterTurnId),
                    linkedCancellation.Token).ConfigureAwait(false);
                linkedCancellation.Token.ThrowIfCancellationRequested();
                return page.Turns;
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

    internal TranscriptViewportAnchorData? TranscriptViewportAnchor
        => _timeline.ViewportAnchor;

    internal void SetTranscriptRowExpanded(AgentTranscriptRowViewModel row, bool isExpanded)
        => _timeline.SetRowExpanded(row, isExpanded);

    private void OnTurnChanged(Guid sessionId, AgentTurnRecord turn)
    {
        if (!_isInitialized)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (DisplayedSession?.SessionId == sessionId)
            {
                _timeline.ApplyLiveTurn(turn);
                ApplyRunActivityState();
            }
        });
    }

    private void OnTranscriptReset(Guid sessionId)
    {
        if (!_isInitialized)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (DisplayedSession?.SessionId == sessionId)
            {
                RefreshTranscript();
            }
        });
    }

    private void OnRunActivityChanged(Guid sessionId, AgentRunActivityUpdate activity)
    {
        if (_isInitialized)
        {
            RunOnUiThread(() => ApplyRunActivityChanged(sessionId, activity));
        }
    }

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

        var parentSession = _sessionService.GetSession(turn.SessionId)
                            ?? _snapshotSessions.GetValueOrDefault(turn.SessionId)?.Session;
        if (parentSession is null)
        {
            return [];
        }

        var workspaceSessions = _sessionService.ListSessionsForWorkspace(
            parentSession.WorkspaceId ?? string.Empty);
        if (workspaceSessions.Count == 0)
        {
            workspaceSessions = _snapshotSessions.Values
                .Select(static item => item.Session)
                .Where(session => string.Equals(
                    session.WorkspaceId,
                    parentSession.WorkspaceId,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        return workspaceSessions
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
            : Profiles.FirstOrDefault(profile => string.Equals(
                profile.ProfileId,
                childSession.ProfileId,
                StringComparison.OrdinalIgnoreCase));
        return new AgentChildSessionLinkViewModel(
            childSession.SessionId,
            childSession.Title,
            FormatChildSessionSubtitle(childProfile?.DisplayName, childSession.AgentKind),
            (_sessionService.GetLatestCheckpoint(childSession.SessionId)
             ?? _snapshotSessions.GetValueOrDefault(childSession.SessionId)?.Checkpoint)?.Status
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
        var session = _sessionService.GetSession(turn.SessionId)
                      ?? _snapshotSessions.GetValueOrDefault(turn.SessionId)?.Session;
        var profileId = session?.ProfileId;
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            var profile = Profiles.FirstOrDefault(profile => string.Equals(
                profile.ProfileId,
                profileId,
                StringComparison.OrdinalIgnoreCase));
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

    private Task<AgentTranscriptPage> LoadTranscriptPageAsync(
        AgentTranscriptPageRequest request,
        CancellationToken cancellationToken)
    {
        if (_transcriptPageGateway is not null)
        {
            return _transcriptPageGateway.LoadTranscriptPageAsync(request, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var limit = Math.Clamp(request.Limit, 1, 500);
        IReadOnlyList<AgentTurnRecord> turns = request.Direction switch
        {
            AgentTranscriptPageDirection.Recent =>
                _sessionService.ListRecentTurns(request.SessionId, limit + 1),
            AgentTranscriptPageDirection.Before when request.AnchorCreatedAtUtc is { } createdAt
                                                     && request.AnchorTurnId is { } turnId =>
                _sessionService.ListTurnsBefore(request.SessionId, createdAt, turnId, limit + 1),
            AgentTranscriptPageDirection.After when request.AnchorCreatedAtUtc is { } createdAt
                                                    && request.AnchorTurnId is { } turnId =>
                _sessionService.ListTurnsAfter(request.SessionId, createdAt, turnId, limit + 1),
            AgentTranscriptPageDirection.Turn when request.AnchorTurnId is { } turnId =>
                _sessionService.GetTurn(turnId) is { } turn ? [turn] : [],
            _ => throw new InvalidOperationException("The transcript page anchor is invalid."),
        };
        var hasMore = turns.Count > limit;
        var pageTurns = !hasMore
            ? turns
            : request.Direction is AgentTranscriptPageDirection.Recent or AgentTranscriptPageDirection.Before
                ? turns.Skip(turns.Count - limit).ToArray()
                : turns.Take(limit).ToArray();
        return Task.FromResult(new AgentTranscriptPage(
            0,
            pageTurns,
            hasMore));
    }
}
