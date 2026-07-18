using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptRowProjector<TRow>
    where TRow : class
{
    public bool TryApplyMutation(
        AgentTurnMutation mutation,
        out AgentTurnRecord updatedTurn)
    {
        var currentTurn = _turnWindow.OrderedTurns()
            .FirstOrDefault(item => item.TurnId == mutation.TurnId);
        return TryApplyMutation(currentTurn, mutation, out updatedTurn);
    }

    public static bool TryApplyMutation(
        AgentTurnRecord? currentTurn,
        AgentTurnMutation mutation,
        out AgentTurnRecord updatedTurn)
    {
        if (mutation.Turn is { } replacement)
        {
            updatedTurn = replacement;
            return replacement.TurnId == mutation.TurnId
                   && replacement.SessionId == mutation.SessionId
                   && replacement.ContentRevision == mutation.ContentRevision;
        }

        if (currentTurn is null
            || currentTurn.TurnId != mutation.TurnId
            || currentTurn.SessionId != mutation.SessionId
            || currentTurn.ContentRevision + 1 != mutation.ContentRevision)
        {
            updatedTurn = null!;
            return false;
        }

        var items = currentTurn.Items.ToArray();
        var textIndex = Array.FindIndex(
            items,
            item => item.Kind == AgentTurnItemKind.Text);
        if (textIndex < 0)
        {
            updatedTurn = null!;
            return false;
        }

        var currentContent = items[textIndex].TextContent ?? string.Empty;
        if (currentContent.Length != mutation.BaseContentLength)
        {
            updatedTurn = null!;
            return false;
        }

        if (mutation.Kind == AgentTurnMutationKind.Append)
        {
            items[textIndex] = items[textIndex] with
            {
                TextContent = currentContent + (mutation.Text ?? string.Empty),
            };
        }
        else if (mutation.Kind != AgentTurnMutationKind.Complete)
        {
            updatedTurn = null!;
            return false;
        }

        updatedTurn = currentTurn with
        {
            Items = items,
            UpdatedAtUtc = mutation.UpdatedAtUtc,
            ContentRevision = mutation.ContentRevision,
            IsStreaming = mutation.Kind != AgentTurnMutationKind.Complete,
        };
        return true;
    }
}
