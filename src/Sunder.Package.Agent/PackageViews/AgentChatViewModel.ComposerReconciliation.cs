using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel
{
    private bool CommitSubmittedUserTurn(AgentTurnRecord turn)
    {
        var submission = _composer.CommitAuthoritativeUserTurn(turn);
        if (submission is null)
        {
            return false;
        }

        if (DisplayedTranscriptSessionId == turn.SessionId
            && !_timeline.IsFollowingLatest)
        {
            RequestTranscriptTailFollow(turn.SessionId);
        }

        if (DisplayedTranscriptSessionId == turn.SessionId)
        {
            _runActivity.TrackTurn(turn, scheduleQuietTimer: false);
        }

        var submittedSession = Sessions.FirstOrDefault(item => item.SessionId == turn.SessionId)
                               ?? (SelectedSession?.SessionId == turn.SessionId ? SelectedSession : null);
        ClearSubmittedComposerState(submittedSession, submission);

        if (submission.IsComplete)
        {
            EndPendingSend(submission);
        }
        return true;
    }

    private bool ReconcileAuthoritativeSubmissionTurn(
        AgentTurnRecord turn,
        AgentComposerSubmission submission)
    {
        if (DisplayedTranscriptSessionId != turn.SessionId)
        {
            CommitSubmittedUserTurn(turn);
            return submission.IsCommitted;
        }
        if (Messages.Any(row => row.RowId == turn.TurnId))
        {
            CommitSubmittedUserTurn(turn);
            return submission.IsCommitted;
        }

        PrepareForSubmittedUserTurn(turn);
        RunTranscriptNotificationBatch(() =>
        {
            var result = _timeline.ApplyAuthoritativeTurn(turn);
            if (result == TranscriptLiveTurnResult.OutsideWindow)
            {
                CommitSubmittedUserTurn(turn);
            }
        });
        return submission.IsCommitted;
    }

    private void PrepareForSubmittedUserTurn(AgentTurnRecord turn)
    {
        if (DisplayedTranscriptSessionId == turn.SessionId
            && !_timeline.IsFollowingLatest
            && _composer.IsPendingAuthoritativeUserTurn(turn))
        {
            RequestTranscriptTailFollow(turn.SessionId);
        }
    }

    private void ClearSubmittedComposerState(
        AgentSessionListItemViewModel? submittedSession,
        AgentComposerSubmission submission
    )
    {
        if (submittedSession is not null
            && string.Equals(submittedSession.DraftMessage, submission.Text, StringComparison.Ordinal))
        {
            submittedSession.DraftMessage = string.Empty;
        }
        if (_sessionDrafts.TryGetValue(submission.SessionId, out var cachedDraft)
            && string.Equals(cachedDraft, submission.Text, StringComparison.Ordinal))
        {
            _sessionDrafts.Remove(submission.SessionId);
        }

        if (SelectedSession?.SessionId != submission.SessionId)
        {
            return;
        }

        _composer.ClearSubmitted(submission);
        NotifyComposerStateChanged();
    }

    private void RestoreUncommittedComposerState(
        AgentSessionListItemViewModel submittedSession,
        AgentComposerSubmission submission)
    {
        if (submission.IsCommitted)
        {
            return;
        }

        if (!string.IsNullOrEmpty(submission.Text)
            && string.IsNullOrEmpty(submittedSession.DraftMessage))
        {
            submittedSession.DraftMessage = submission.Text;
        }

        if (_composer.RestoreUncommitted(submission, SelectedSession?.SessionId))
        {
            NotifyComposerStateChanged();
        }
    }

    private async Task<bool> CompleteOrRestoreComposerSubmissionAsync(
        AgentSessionListItemViewModel submittedSession,
        AgentComposerSubmission submission,
        bool restoreWhenMissing = true)
    {
        if (submission.IsCommitted)
        {
            return true;
        }

        var transcriptRequest = submission.UseUserTurnCorrelation
            ? new AgentTranscriptPageRequest(
                submission.SessionId,
                AgentTranscriptPageDirection.Turn,
                1,
                AnchorTurnId: submission.UserTurnId)
            : new AgentTranscriptPageRequest(
                submission.SessionId,
                AgentTranscriptPageDirection.Recent,
                500);
        var transcript = await LoadTranscriptPageAsync(
            transcriptRequest,
            _lifetimeCancellation.Token);
        var authoritativeTurn = transcript.Turns.FirstOrDefault(submission.Matches);
        if (authoritativeTurn is not null)
        {
            return await EnqueueTranscriptBoundaryAsync(() =>
                    ReconcileAuthoritativeSubmissionTurn(authoritativeTurn, submission))
                .ConfigureAwait(false);
        }
        if (!restoreWhenMissing)
        {
            return false;
        }

        await InvokeOnUiThreadAsync(() =>
            RestoreUncommittedComposerState(submittedSession, submission));
        return true;
    }

    private void ScheduleCompletedSubmissionReconciliation()
    {
        if (_disposed || _composer.GetCompletedUncommittedSubmissions().Count == 0)
        {
            return;
        }

        _backgroundTasks.Run(ReconcileCompletedComposerSubmissionsAsync);
    }

    private async Task ReconcileCompletedComposerSubmissionsAsync(CancellationToken cancellationToken)
    {
        await _composerReconciliationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var retryDelay = TimeSpan.FromMilliseconds(100);
            while (true)
            {
                var submissions = _composer.GetCompletedUncommittedSubmissions();
                if (submissions.Count == 0)
                {
                    return;
                }

                var hasUnresolvedSubmission = false;
                foreach (var submission in submissions)
                {
                    AgentSessionListItemViewModel? submittedSession = null;
                    await InvokeOnUiThreadAsync(() =>
                        submittedSession = Sessions.FirstOrDefault(item => item.SessionId == submission.SessionId)
                                           ?? (SelectedSession?.SessionId == submission.SessionId
                                               ? SelectedSession
                                               : null));
                    if (submittedSession is null)
                    {
                        hasUnresolvedSubmission = true;
                        continue;
                    }

                    try
                    {
                        if (await CompleteOrRestoreComposerSubmissionAsync(
                                submittedSession,
                                submission,
                                restoreWhenMissing: false).ConfigureAwait(false))
                        {
                            await InvokeOnUiThreadAsync(() => EndPendingSend(submission));
                            continue;
                        }

                        var commandStatus = _runCommandStatusGateway is null
                            ? AgentRunCommandStatus.Absent
                            : await _runCommandStatusGateway.GetRunCommandStatusAsync(
                                submission.SessionId,
                                submission.UserTurnId,
                                cancellationToken).ConfigureAwait(false);
                        if (commandStatus == AgentRunCommandStatus.Absent)
                        {
                            await InvokeOnUiThreadAsync(() =>
                            {
                                RestoreUncommittedComposerState(submittedSession, submission);
                                EndPendingSend(submission);
                            });
                            continue;
                        }
                        if (commandStatus == AgentRunCommandStatus.Committed)
                        {
                            await InvokeOnUiThreadAsync(() =>
                            {
                                submission.Commit();
                                ClearSubmittedComposerState(submittedSession, submission);
                                EndPendingSend(submission);
                            });
                            continue;
                        }

                        hasUnresolvedSubmission = true;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        hasUnresolvedSubmission = true;
                    }
                }

                if (!hasUnresolvedSubmission)
                {
                    return;
                }

                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromMilliseconds(Math.Min(2000, retryDelay.TotalMilliseconds * 2));
            }
        }
        finally
        {
            _composerReconciliationGate.Release();
        }
    }

    private bool IsSelectedSessionSendPending() =>
        SelectedSession is not null && IsSendPending(SelectedSession.SessionId);

    private bool IsSendPending(Guid sessionId)
        => _composer.IsSendPending(sessionId);

    private void EndPendingSend(AgentComposerSubmission submission)
    {
        if (_composer.EndSubmission(submission))
        {
            NotifySendPendingStateChanged(submission.SessionId);
        }
    }

    private void NotifySendPendingStateChanged(Guid sessionId)
    {
        if (SelectedSession?.SessionId == sessionId)
        {
            NotifySelectedSessionRunStateChanged();
        }
    }

    private void NotifyComposerStateChanged()
    {
        OnPropertyChanged(nameof(DraftMessage));
        OnPropertyChanged(nameof(PendingRollbackTurnId));
        OnPendingRollbackTurnIdChanged(PendingRollbackTurnId);
        OnDraftMessageChanged(DraftMessage);
        OnPropertyChanged(nameof(HasPendingAttachments));
        OnPropertyChanged(nameof(PendingAttachmentSummaryText));
    }
}
