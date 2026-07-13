using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.PackageViews;

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
                return await transcriptReader.ListTurnsBeforeAsync(
                    sessionId,
                    beforeCreatedAt,
                    beforeTurnId,
                    limit,
                    linkedCancellation.Token);
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
                return await transcriptReader.ListTurnsAfterAsync(
                    sessionId,
                    afterCreatedAt,
                    afterTurnId,
                    limit,
                    linkedCancellation.Token);
            },
            protectedAnchorKey);
        if (loaded)
        {
            _runActivity.NotifyFollowStateChanged();
            ApplyRunActivityState();
        }
        return loaded;
    }

    private void LoadTranscript(Guid? sessionId)
    {
        _runActivity.Reset();
        if (_transcriptReader is null || sessionId is null)
        {
            _timeline.ClearSession();
            return;
        }

        var ticket = _timeline.BeginInitialLoad(sessionId.Value);
        _tasks.Run(_ => LoadTranscriptAsync(ticket));
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

            var turns = await transcriptReader.ListRecentTurnsAsync(
                ticket.SessionId,
                InitialTranscriptTurnLimit + 1,
                ticket.Generation.CancellationToken);
            await RunOnUiThreadAsync(
                () => CompleteTranscriptLoad(ticket, turns),
                ticket.Generation.CancellationToken);
        }
        catch (OperationCanceledException) when (ticket.Generation.CancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            await RunOnUiThreadAsync(
                () => _timeline.TryFailInitialLoad(ticket),
                CancellationToken.None);
        }
    }

    private void CompleteTranscriptLoad(TranscriptLoadTicket ticket, IReadOnlyList<AgentTurnRecord> turns)
    {
        if (!_timeline.TryCompleteInitialLoad(ticket, turns))
        {
            return;
        }

        _knownCheckpoints.TryGetValue(ticket.SessionId, out var checkpoint);
        _runActivity.TrackCheckpoint(checkpoint);
        ApplyRunActivityState();
    }
}
