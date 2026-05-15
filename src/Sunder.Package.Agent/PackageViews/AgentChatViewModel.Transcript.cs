using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Threading;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel
{
    private void RefreshTranscript()
    {
        CancelPendingTranscriptRefresh();
        ResetTranscriptWindow();
        var displayedSession = DisplayedSession;
        if (displayedSession is null)
        {
            IsTranscriptLoading = false;
            StatusText = string.IsNullOrWhiteSpace(_globalStatusText)
                ? GetSetupStatusText()
                : _globalStatusText;
            TranscriptChanged?.Invoke();
            return;
        }

        if (Application.Current is null)
        {
            var turns = _sessionService.ListRecentTurns(displayedSession.SessionId, InitialTranscriptTurnLimit + 1);
            ApplyInitialTranscriptTurns(displayedSession, turns);
            return;
        }

        IsTranscriptLoading = true;
        StatusText = "Loading transcript...";
        TranscriptChanged?.Invoke();

        var refreshCts = new CancellationTokenSource();
        _transcriptRefreshCts = refreshCts;
        _ = RefreshTranscriptAsync(displayedSession, refreshCts);
    }

    private async Task RefreshTranscriptAsync(AgentSessionListItemViewModel displayedSession, CancellationTokenSource refreshCts)
    {
        var sessionId = displayedSession.SessionId;
        var cancellationToken = refreshCts.Token;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            cancellationToken.ThrowIfCancellationRequested();

            var turns = await Task.Run(
                () => _sessionService.ListRecentTurns(sessionId, InitialTranscriptTurnLimit + 1),
                cancellationToken).ConfigureAwait(false);
            var orderedTurns = turns
                .OrderBy(turn => turn.CreatedAtUtc)
                .ThenBy(turn => turn.TurnId)
                .ToArray();
            var turnsToApply = SelectTranscriptWindow(orderedTurns, InitialTranscriptTurnLimit);

            for (var index = 0; index < turnsToApply.Length; index += TranscriptHydrationBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = turnsToApply
                    .Skip(index)
                    .Take(TranscriptHydrationBatchSize)
                    .ToArray();

                await Dispatcher.UIThread.InvokeAsync(
                    () =>
                    {
                        if (!IsCurrentTranscriptRefresh(sessionId, refreshCts))
                        {
                            return;
                        }

                        foreach (var turn in batch)
                        {
                            ApplyTurnToTranscript(turn, InsertMode.Append, trackRunActivity: true, scheduleQuietTimer: false);
                        }
                    },
                    DispatcherPriority.Background);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }

            await Dispatcher.UIThread.InvokeAsync(
                () =>
                {
                    if (IsCurrentTranscriptRefresh(sessionId, refreshCts))
                    {
                        CompleteTranscriptRefresh(displayedSession, orderedTurns.Length > InitialTranscriptTurnLimit);
                    }
                },
                DispatcherPriority.Background);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(
                () =>
                {
                    if (!IsCurrentTranscriptRefresh(sessionId, refreshCts))
                    {
                        return;
                    }

                    IsTranscriptLoading = false;
                    StatusText = $"Unable to load transcript: {ex.Message}";
                },
                DispatcherPriority.Background);
        }
        finally
        {
            if (ReferenceEquals(_transcriptRefreshCts, refreshCts))
            {
                _transcriptRefreshCts = null;
            }

            refreshCts.Dispose();
        }
    }

    private bool IsCurrentTranscriptRefresh(Guid sessionId, CancellationTokenSource refreshCts)
        => ReferenceEquals(_transcriptRefreshCts, refreshCts)
           && DisplayedSession?.SessionId == sessionId;

    private void CancelPendingTranscriptRefresh()
    {
        var refreshCts = _transcriptRefreshCts;
        if (refreshCts is null)
        {
            return;
        }

        _transcriptRefreshCts = null;
        refreshCts.Cancel();
    }

    private void ApplyInitialTranscriptTurns(AgentSessionListItemViewModel displayedSession, IReadOnlyList<AgentTurnRecord> turns)
    {
        var orderedTurns = turns.OrderBy(turn => turn.CreatedAtUtc).ThenBy(turn => turn.TurnId).ToArray();
        foreach (var turn in SelectTranscriptWindow(orderedTurns, InitialTranscriptTurnLimit))
        {
            ApplyTurnToTranscript(turn, InsertMode.Append, trackRunActivity: true, scheduleQuietTimer: false);
        }

        CompleteTranscriptRefresh(displayedSession, orderedTurns.Length > InitialTranscriptTurnLimit);
    }

    private static AgentTurnRecord[] SelectTranscriptWindow(AgentTurnRecord[] orderedTurns, int pageSize)
    {
        if (orderedTurns.Length <= pageSize)
        {
            return orderedTurns;
        }

        return orderedTurns.Skip(orderedTurns.Length - pageSize).ToArray();
    }

    private void CompleteTranscriptRefresh(AgentSessionListItemViewModel displayedSession, bool hasOlderRows)
    {
        ApplyPendingTranscriptTurns(displayedSession.SessionId);
        HasOlderTranscriptRows = hasOlderRows;
        ReloadPendingPermissionRequests();
        UpdateActivityRowForCurrentState();
        IsTranscriptLoading = false;
        TranscriptChanged?.Invoke();
        UpdateSessionState(displayedSession.SessionId, markUnread: false);
        displayedSession.ClearUnreadActivity();
        StatusText = displayedSession.StatusText;
    }

    private void ApplyPendingTranscriptTurns(Guid sessionId)
    {
        if (_pendingTranscriptTurnsByTurnId.Count == 0)
        {
            return;
        }

        var turns = _pendingTranscriptTurnsByTurnId.Values
            .Where(turn => turn.SessionId == sessionId)
            .OrderBy(turn => turn.CreatedAtUtc)
            .ThenBy(turn => turn.TurnId)
            .ToArray();
        _pendingTranscriptTurnsByTurnId.Clear();

        foreach (var turn in turns)
        {
            ApplyTurnToTranscript(turn, InsertMode.Append, trackRunActivity: true, scheduleQuietTimer: true);
        }
    }

    public async Task<bool> LoadOlderTranscriptRowsAsync()
    {
        var displayedSession = DisplayedSession;
        if (!CanLoadOlderTranscriptRows || _oldestLoadedTurnCreatedAtUtc is null || _oldestLoadedTurnId is null || displayedSession is null)
        {
            return false;
        }

        IsLoadingOlderTranscriptRows = true;
        try
        {
            var sessionId = displayedSession.SessionId;
            var beforeCreatedAtUtc = _oldestLoadedTurnCreatedAtUtc.Value;
            var beforeTurnId = _oldestLoadedTurnId.Value;
            var turns = await Task.Run(() => _sessionService.ListTurnsBefore(
                sessionId,
                beforeCreatedAtUtc,
                beforeTurnId,
                OlderTranscriptTurnPageSize + 1));
            if (DisplayedSession?.SessionId != sessionId)
            {
                return false;
            }

            if (turns.Count == 0)
            {
                HasOlderTranscriptRows = false;
                return false;
            }

            var insertIndex = 0;
            var orderedTurns = turns.OrderBy(turn => turn.CreatedAtUtc).ThenBy(turn => turn.TurnId).ToArray();
            foreach (var turn in SelectTranscriptWindow(orderedTurns, OlderTranscriptTurnPageSize))
            {
                insertIndex += ApplyTurnToTranscript(turn, InsertMode.Prepend, insertIndex);
            }

            HasOlderTranscriptRows = orderedTurns.Length > OlderTranscriptTurnPageSize;
            return insertIndex > 0;
        }
        finally
        {
            IsLoadingOlderTranscriptRows = false;
        }
    }

    private void OnTurnChanged(Guid sessionId, AgentTurnRecord turn)
        => RunOnUiThread(() => ApplyTurnChanged(sessionId, turn));

    private void ApplyTurnChanged(Guid sessionId, AgentTurnRecord turn)
    {
        if (DisplayedSession?.SessionId != sessionId)
        {
            return;
        }

        if (IsTranscriptLoading)
        {
            _pendingTranscriptTurnsByTurnId[turn.TurnId] = turn;
            return;
        }

        ApplyTurnToTranscript(turn, InsertMode.Append, trackRunActivity: true, scheduleQuietTimer: true);
        UpdateActivityRowForCurrentState();
        TranscriptChanged?.Invoke();
    }

    private void RefreshVisibleChildSessionLinks()
    {
        foreach (var row in _toolRowsByCallId.Values)
        {
            row.RefreshChildSessionLink();
        }
    }

    private IReadOnlyList<AgentChildSessionLinkViewModel> ResolveChildSessionLinksFromStore(AgentTurnRecord turn, AgentTurnItemRecord item)
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
                              && string.Equals(session.ParentToolCallId, item.CallId, StringComparison.Ordinal))
            .OrderBy(session => session.CreatedAtUtc)
            .Select(CreateChildSessionLink)
            .ToArray();
    }

    private AgentChildSessionLinkViewModel CreateChildSessionLink(AgentSessionRecord childSession)
    {
        var childProfile = string.IsNullOrWhiteSpace(childSession.ProfileId) ? null : _profileService.GetProfile(childSession.ProfileId);
        var subtitle = FormatChildSessionSubtitle(childProfile?.DisplayName, childSession.AgentKind);
        return new AgentChildSessionLinkViewModel(
            childSession.SessionId,
            childSession.Title,
            subtitle,
            _sessionService.GetLatestCheckpoint(childSession.SessionId)?.Status ?? AgentRunStatus.Idle);
    }

    private static bool IsSubagentTool(string? toolId)
        => string.Equals(toolId, "task", StringComparison.OrdinalIgnoreCase)
           || string.Equals(toolId, "delegate_tasks", StringComparison.OrdinalIgnoreCase);

    private static string FormatChildSessionSubtitle(string? profileDisplayName, string? agentKind)
    {
        var name = string.IsNullOrWhiteSpace(profileDisplayName) ? agentKind : profileDisplayName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Subagent";
        }

        return string.Equals(agentKind, "subagent", StringComparison.OrdinalIgnoreCase)
               && !name.EndsWith("subagent", StringComparison.OrdinalIgnoreCase)
            ? $"{name} subagent"
            : name;
    }

    private void ResetTranscriptWindow()
    {
        _activityRow?.Dispose();
        _activityRow = null;
        _textRowsByTurnId.Clear();
        _toolRowsByCallId.Clear();
        _loadedTurnIds.Clear();
        _pendingTranscriptTurnsByTurnId.Clear();
        _oldestLoadedTurnCreatedAtUtc = null;
        _oldestLoadedTurnId = null;
        _activityTextBase = "Thinking";
        _hasVisibleRunActivity = false;
        _showActivityAfterQuiet = false;
        _activityQuietTimer.Stop();
        HasOlderTranscriptRows = false;
        IsLoadingOlderTranscriptRows = false;
        IsTranscriptLoading = false;
        Messages.Clear();
        OnPropertyChanged(nameof(CanLoadOlderTranscriptRows));
    }

    private int ApplyTurnToTranscript(
        AgentTurnRecord turn,
        InsertMode insertMode,
        int prependIndex = 0,
        bool trackRunActivity = false,
        bool scheduleQuietTimer = true)
    {
        var insertedRows = 0;
        var isNewTurn = _loadedTurnIds.Add(turn.TurnId);
        TrackOldestLoadedTurn(turn);
        if (insertMode == InsertMode.Append && trackRunActivity)
        {
            TrackVisibleRunActivity(turn, scheduleQuietTimer);
        }

        switch (turn.Kind)
        {
            case AgentTurnKind.ToolCall:
                foreach (var item in turn.Items.Where(item => item.Kind == AgentTurnItemKind.ToolCall))
                {
                    if (!string.IsNullOrWhiteSpace(item.CallId) && _toolRowsByCallId.ContainsKey(item.CallId))
                    {
                        continue;
                    }

                    var row = new AgentToolInvocationRowViewModel(turn, item, _toolPresentationService, ResolveChildSessionLinksFromStore);
                    if (!string.IsNullOrWhiteSpace(item.CallId))
                    {
                        _toolRowsByCallId[item.CallId] = row;
                    }

                    InsertTranscriptRow(row, insertMode, prependIndex + insertedRows);
                    insertedRows++;
                }

                break;

            case AgentTurnKind.ToolResult:
                foreach (var item in turn.Items.Where(item => item.Kind == AgentTurnItemKind.ToolResult))
                {
                    if (!string.IsNullOrWhiteSpace(item.CallId) && _toolRowsByCallId.TryGetValue(item.CallId, out var existingToolRow))
                    {
                        existingToolRow.ApplyResult(turn, item);
                        continue;
                    }

                    if (!isNewTurn && !string.IsNullOrWhiteSpace(item.CallId) && _toolRowsByCallId.ContainsKey(item.CallId))
                    {
                        continue;
                    }

                    var row = new AgentToolInvocationRowViewModel(turn, item, _toolPresentationService, ResolveChildSessionLinksFromStore);
                    if (!string.IsNullOrWhiteSpace(item.CallId))
                    {
                        _toolRowsByCallId[item.CallId] = row;
                    }

                    InsertTranscriptRow(row, insertMode, prependIndex + insertedRows);
                    insertedRows++;
                }

                break;

            default:
                var textContent = ExtractTextContent(turn);
                var attachmentRows = ExtractAttachmentViewModels(turn);
                if (string.IsNullOrWhiteSpace(textContent) && attachmentRows.Count == 0)
                {
                    break;
                }

                if (_textRowsByTurnId.TryGetValue(turn.TurnId, out var existingTextRow))
                {
                    existingTextRow.UpdateContent(textContent);
                    existingTextRow.ReplaceAttachments(attachmentRows);
                    break;
                }

                var textRow = new AgentTextTranscriptRowViewModel(turn, textContent, attachmentRows);
                _textRowsByTurnId[turn.TurnId] = textRow;
                InsertTranscriptRow(textRow, insertMode, prependIndex + insertedRows);
                insertedRows++;
                break;
        }

        return insertedRows;
    }

    private void InsertTranscriptRow(AgentTranscriptRowViewModel row, InsertMode insertMode, int prependIndex)
    {
        if (insertMode == InsertMode.Prepend)
        {
            Messages.Insert(Math.Clamp(prependIndex, 0, Messages.Count), row);
            return;
        }

        var insertIndex = _activityRow is null ? Messages.Count : Math.Max(0, Messages.IndexOf(_activityRow));
        Messages.Insert(insertIndex, row);
    }

    private void TrackOldestLoadedTurn(AgentTurnRecord turn)
    {
        if (_oldestLoadedTurnCreatedAtUtc is null
            || turn.CreatedAtUtc < _oldestLoadedTurnCreatedAtUtc
            || turn.CreatedAtUtc == _oldestLoadedTurnCreatedAtUtc && string.CompareOrdinal(turn.TurnId.ToString(), _oldestLoadedTurnId?.ToString()) < 0)
        {
            _oldestLoadedTurnCreatedAtUtc = turn.CreatedAtUtc;
            _oldestLoadedTurnId = turn.TurnId;
        }
    }

    private void UpdateActivityRowForCurrentState()
    {
        if (!IsDisplayedSessionRunActive)
        {
            _activityQuietTimer.Stop();
            _showActivityAfterQuiet = false;
        }

        if (IsDisplayedSessionRunActive && (!_hasVisibleRunActivity || _showActivityAfterQuiet))
        {
            if (_activityRow is null)
            {
                _activityRow = new AgentActivityTranscriptRowViewModel(_activityTextBase);
                Messages.Add(_activityRow);
                return;
            }

            _activityRow.SetActivityTextBase(_activityTextBase);

            var index = Messages.IndexOf(_activityRow);
            if (index >= 0 && index != Messages.Count - 1)
            {
                Messages.Move(index, Messages.Count - 1);
            }
            else if (index < 0)
            {
                Messages.Add(_activityRow);
            }

            return;
        }

        if (_activityRow is null)
        {
            return;
        }

        var activityIndex = Messages.IndexOf(_activityRow);
        if (activityIndex >= 0)
        {
            Messages.RemoveAt(activityIndex);
        }

        _activityRow.Dispose();
        _activityRow = null;
    }

    private void TrackVisibleRunActivity(AgentTurnRecord turn, bool scheduleQuietTimer)
    {
        if (turn.Role == AgentMessageRole.User)
        {
            _activityTextBase = "Thinking";
            _hasVisibleRunActivity = false;
            _showActivityAfterQuiet = false;
            _activityQuietTimer.Stop();
            return;
        }

        if (HasVisibleRunActivity(turn))
        {
            _hasVisibleRunActivity = true;
            _showActivityAfterQuiet = !scheduleQuietTimer;
            SetActivityTextBase(ResolveActivityTextBase(turn));
            if (scheduleQuietTimer)
            {
                RestartActivityQuietTimer();
            }
        }
    }

    private void RestartActivityQuietTimer()
    {
        _activityQuietTimer.Stop();
        if (!IsDisplayedSessionRunActive)
        {
            return;
        }

        if (_activityQuietDelay <= TimeSpan.Zero)
        {
            ShowActivityAfterQuietPeriod();
            return;
        }

        _activityQuietTimer.Start();
    }

    private void OnActivityQuietTimerTick(object? sender, EventArgs e)
    {
        _activityQuietTimer.Stop();
        ShowActivityAfterQuietPeriod();
    }

    private void ShowActivityAfterQuietPeriod()
    {
        if (!IsDisplayedSessionRunActive)
        {
            return;
        }

        _showActivityAfterQuiet = true;
        UpdateActivityRowForCurrentState();
        TranscriptChanged?.Invoke();
    }

    private void SetActivityTextBase(string textBase)
    {
        var normalized = string.IsNullOrWhiteSpace(textBase) ? "Processing" : textBase.Trim();
        if (string.Equals(_activityTextBase, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _activityTextBase = normalized;
        _activityRow?.SetActivityTextBase(normalized);
    }

    private void TrackCheckpointActivity(AgentRunCheckpointRecord? checkpoint)
    {
        if (checkpoint?.Status != AgentRunStatus.Running)
        {
            _activityQuietTimer.Stop();
            _showActivityAfterQuiet = false;
            return;
        }

        SetActivityTextBase(ResolveActivityTextBase(checkpoint));
    }

    private static bool HasVisibleRunActivity(AgentTurnRecord turn)
        => turn.Kind switch
        {
            AgentTurnKind.ToolCall => turn.Items.Any(item => item.Kind == AgentTurnItemKind.ToolCall),
            AgentTurnKind.ToolResult => turn.Items.Any(item => item.Kind == AgentTurnItemKind.ToolResult),
            _ => turn.Role == AgentMessageRole.Assistant && !string.IsNullOrWhiteSpace(ExtractTextContent(turn)),
        };

    private static string ResolveActivityTextBase(AgentTurnRecord turn)
        => turn.Kind switch
        {
            AgentTurnKind.ToolCall => ResolveToolCallActivityText(turn),
            AgentTurnKind.ToolResult => "Processing result",
            _ => turn.Role == AgentMessageRole.Assistant ? "Processing" : "Thinking",
        };

    private static string ResolveToolCallActivityText(AgentTurnRecord turn)
    {
        var toolId = turn.Items.FirstOrDefault(item => item.Kind == AgentTurnItemKind.ToolCall)?.ToolId;
        return string.IsNullOrWhiteSpace(toolId)
            ? "Running tool"
            : $"Running {HumanizeActivityToolName(toolId)}";
    }

    private static string ResolveActivityTextBase(AgentRunCheckpointRecord checkpoint)
    {
        var summary = checkpoint.Summary ?? string.Empty;
        if (TryExtractQuotedToolId(summary, "Executing approved tool '", out var toolId)
            || TryExtractQuotedToolId(summary, "Executing tool '", out toolId))
        {
            return $"Running {HumanizeActivityToolName(toolId ?? string.Empty)}";
        }

        if (summary.Contains("completed. Continuing provider execution", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("continuing provider execution", StringComparison.OrdinalIgnoreCase))
        {
            return "Processing result";
        }

        if (summary.Contains("Provider execution is starting", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("User message queued", StringComparison.OrdinalIgnoreCase))
        {
            return "Thinking";
        }

        return "Processing";
    }

    private static bool TryExtractQuotedToolId(string text, string prefix, out string? toolId)
    {
        toolId = null;
        var start = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return false;
        }

        start += prefix.Length;
        var end = text.IndexOf('\'', start);
        if (end <= start)
        {
            return false;
        }

        toolId = text[start..end];
        return !string.IsNullOrWhiteSpace(toolId);
    }

    private static string HumanizeActivityToolName(string toolId)
    {
        var parts = toolId.Split(['_', '-', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "tool";
        }

        return string.Join(" ", parts.Select(part => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(part.ToLowerInvariant())));
    }

    private static string ExtractTextContent(AgentTurnRecord turn)
        => string.Join("\n\n", turn.Items
            .Where(item => item.Kind == AgentTurnItemKind.Text && !string.IsNullOrWhiteSpace(item.TextContent))
            .Select(item => item.TextContent!.Trim()));

    private static IReadOnlyList<AgentTranscriptAttachmentViewModel> ExtractAttachmentViewModels(AgentTurnRecord turn)
        => turn.Items
            .Where(item => item.Kind == AgentTurnItemKind.Attachment)
            .Select(TryCreateAttachmentViewModel)
            .Where(attachment => attachment is not null)
            .Cast<AgentTranscriptAttachmentViewModel>()
            .ToArray();

    private static AgentTranscriptAttachmentViewModel? TryCreateAttachmentViewModel(AgentTurnItemRecord item)
    {
        if (string.IsNullOrWhiteSpace(item.StructuredPayloadJson))
        {
            return null;
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<AgentAttachmentMetadata>(item.StructuredPayloadJson);
            return metadata is null ? null : new AgentTranscriptAttachmentViewModel(metadata);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private enum InsertMode
    {
        Append,
        Prepend,
    }
}
