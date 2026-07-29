using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.PackageViews;
using Sunder.Package.Agent.Subagents.Runtime;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public sealed partial class SubsessionsViewModel
{
    public async Task<bool> LoadOlderTranscriptRowsAsync(
        object? protectedAnchorKey = null,
        CancellationToken cancellationToken = default)
    {
        var transcriptReader = _transcriptReader;
        if (transcriptReader is null)
        {
            return false;
        }

        var loaded = await _timeline.LoadOlderAsync(
            async (sessionId, beforeCreatedAt, beforeTurnId, limit, pageCancellationToken) =>
            {
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    pageCancellationToken);
                var page = await transcriptReader.ListTurnsBeforeAsync(
                    sessionId,
                    beforeCreatedAt,
                    beforeTurnId,
                    limit,
                    linkedCancellation.Token).ConfigureAwait(false);
                return new TranscriptTurnPage(page.Turns, page.HasMore, page.Continuation);
            },
            protectedAnchorKey,
            cancellationToken);
        if (loaded)
        {
            ApplyRunActivityState();
        }
        return loaded;
    }

    public async Task<bool> LoadNewerTranscriptRowsAsync(
        object? protectedAnchorKey = null,
        CancellationToken cancellationToken = default,
        bool resumeFollowingWhenCaughtUp = true)
    {
        var transcriptReader = _transcriptReader;
        if (transcriptReader is null)
        {
            return false;
        }

        var loaded = await _timeline.LoadNewerAsync(
            async (sessionId, afterCreatedAt, afterTurnId, limit, pageCancellationToken) =>
            {
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    pageCancellationToken);
                var page = await transcriptReader.ListTurnsAfterAsync(
                    sessionId,
                    afterCreatedAt,
                    afterTurnId,
                    limit,
                    linkedCancellation.Token).ConfigureAwait(false);
                return new TranscriptTurnPage(page.Turns, page.HasMore, page.Continuation);
            },
            protectedAnchorKey,
            cancellationToken);
        if (loaded)
        {
            if (resumeFollowingWhenCaughtUp)
            {
                _timeline.ResumeFollowingLatestIfCaughtUp();
            }
            _runActivity.NotifyFollowStateChanged();
            ApplyRunActivityState();
        }
        return loaded;
    }

    private void LoadTranscript(Guid? sessionId, bool forceReplacement = false)
    {
        _runActivity.Reset();
        if (_transcriptReader is null || sessionId is null)
        {
            _timeline.ClearSession();
            Volatile.Write(ref _transcriptLoadOperation, Task.CompletedTask);
            return;
        }

        var ticket = _timeline.BeginInitialLoad(sessionId.Value, forceReplacement);
        var operation = LoadTranscriptAsync(ticket);
        Volatile.Write(ref _transcriptLoadOperation, operation);
        _tasks.Run(operation);
    }

    private async Task LoadTranscriptAsync(TranscriptLoadTicket ticket)
    {
        try
        {
            var transcriptReader = _transcriptReader;
            if (transcriptReader is null)
            {
                _timeline.TryFailInitialLoad(ticket);
                return;
            }

            var page = await transcriptReader.ListRecentTurnsAsync(
                ticket.SessionId,
                InitialTranscriptTurnLimit,
                ticket.Generation.CancellationToken).ConfigureAwait(false);
            await RunOnUiThreadAsync(
                () => CompleteTranscriptLoad(ticket, page),
                ticket.Generation.CancellationToken);
        }
        catch (OperationCanceledException) when (ticket.Generation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await RunOnUiThreadAsync(
                () =>
                {
                    if (_timeline.TryFailInitialLoad(ticket))
                    {
                        StatusText = $"Unable to load subsession transcript: {ex.Message}";
                    }
                },
                CancellationToken.None);
        }
    }

    private void CompleteTranscriptLoad(TranscriptLoadTicket ticket, SubsessionTranscriptPage page)
    {
        if (!_timeline.TryCompleteInitialLoad(
                ticket,
                page.Turns,
                page.HasMore,
                page.Continuation))
        {
            return;
        }

        _knownCheckpoints.TryGetValue(ticket.SessionId, out var checkpoint);
        _runActivity.TrackCheckpoint(checkpoint);
        ApplyRunActivityState();
    }
}
