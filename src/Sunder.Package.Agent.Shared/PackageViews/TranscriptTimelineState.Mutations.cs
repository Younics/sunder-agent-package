using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptTimelineState<TRow>
    where TRow : class
{
    public TranscriptLiveTurnResult ApplyLiveMutation(AgentTurnMutation mutation)
    {
        if (SessionId != mutation.SessionId)
        {
            return TranscriptLiveTurnResult.Ignored;
        }

        if (mutation.Turn is { } turn)
        {
            return ApplyLiveTurn(turn);
        }

        AgentTurnRecord? currentTurn = null;
        if (_pendingTurnsById.TryGetValue(mutation.TurnId, out var pendingTurn))
        {
            currentTurn = pendingTurn;
        }

        if (!TranscriptRowProjector<TRow>.TryApplyMutation(
                currentTurn,
                mutation,
                out var updatedTurn)
            && !_projector.TryApplyMutation(mutation, out updatedTurn))
        {
            return TranscriptLiveTurnResult.ReloadRequired;
        }

        return ApplyLiveTurn(updatedTurn);
    }

    public void ApplyActivity(string text, bool isReasoning, bool isVisible)
    {
        if (!IsFollowingLatest && !IsReplacingRows)
        {
            return;
        }

        if (!_projector.SetActivity(text, isReasoning, isVisible))
        {
            return;
        }

        ApplyTrim(AgentTranscriptTrimDirection.Oldest);
        RowsChanged?.Invoke();
    }

    public void RefreshRelatedRows()
    {
        if (_projector.RefreshRelatedRows())
        {
            RowsChanged?.Invoke();
        }
    }
}
