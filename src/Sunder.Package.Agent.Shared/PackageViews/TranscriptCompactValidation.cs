using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal static class TranscriptCompactValidation
{
    internal static void Page(AgentCompactTranscriptPage page, Guid sessionId)
    {
        var ids = new HashSet<Guid>();
        AgentCompactTranscriptEntry? previous = null;
        foreach (var entry in page.Entries)
        {
            if (entry.EntryId == Guid.Empty || !ids.Add(entry.EntryId)
                || (entry.Message is null) == (entry.Group is null)
                || previous is not null && (previous.TurnSequence != entry.TurnSequence
                    ? previous.TurnSequence >= entry.TurnSequence : Compare(AgentTranscriptTime.Read(previous.CreatedAtUtc), previous.TurnId,
                    AgentTranscriptTime.Read(entry.CreatedAtUtc), entry.TurnId) >= 0))
                throw new PresentationOperationException("The Compact transcript page contains invalid entry boundaries.");
            if (entry.Message is { } message && (message.SessionId != sessionId || message.TurnId != entry.TurnId
                || entry.EntryId != message.TurnId || AgentTranscriptTime.Read(message.CreatedAtUtc) != AgentTranscriptTime.Read(entry.CreatedAtUtc)))
                throw new PresentationOperationException("The Compact message does not match the requested session and position.");
            if (entry.Group is { } group && (group.SessionId != sessionId || group.First.ItemId != entry.EntryId
                || group.First.TurnId != entry.TurnId || AgentTranscriptTime.Read(group.First.CreatedAtUtc) != AgentTranscriptTime.Read(entry.CreatedAtUtc)
                || Compare(group.First, group.Last) > 0 || group.Tools.Count == 0 || group.Tools.Any(tool => tool.Count <= 0)
                || group.TranscriptEpoch <= 0 || group.Revision <= 0))
                throw new PresentationOperationException("The activity group does not match the requested session and position.");
            previous = entry;
        }
    }

    internal static void Members(AgentTranscriptActivityMembersPage page, AgentTranscriptActivityGroup group,
        AgentTranscriptInvocationCursor? after)
    {
        var previous = after;
        foreach (var turn in page.Headers)
        {
            if (turn.SessionId != group.SessionId || turn.RunId != group.RunId || turn.RunRevision != group.RunRevision
                || turn.Items.Count != 1 || turn.Kind is not (AgentTurnKind.ToolCall or AgentTurnKind.ToolResult))
                throw new PresentationOperationException("Activity headers do not match the requested group.");
            var item = turn.Items[0];
            var position = TranscriptCompactPayload.Cursor(turn, item);
            if (!item.IsToolHeaderProjection || item.Kind is not (AgentTurnItemKind.ToolCall or AgentTurnItemKind.ToolResult)
                || item.TurnId != turn.TurnId || item.ItemId == Guid.Empty || item.ArgumentsJson is not null
                || item.TextContent is not null || item.StructuredPayloadJson is not null || item.SourcesJson is not null
                || Compare(position, group.First) < 0 || Compare(position, group.Last) > 0
                || previous is null && Compare(position, group.First) != 0
                || previous is not null && Compare(position, previous) <= 0)
                throw new PresentationOperationException("Activity header pagination returned an invalid invocation position.");
            previous = position;
        }
        if (page.Headers.Count is 0 or > 100 || page.Continuation is null || previous is null
            || Compare(page.Continuation, previous) != 0
            || page.HasMore != (Compare(previous, group.Last) < 0))
            throw new PresentationOperationException("Activity header pagination did not return a complete, advancing chunk.");
    }

    private static int Compare(DateTimeOffset leftTime, Guid leftTurn, DateTimeOffset rightTime, Guid rightTurn)
    {
        var time = leftTime.CompareTo(rightTime);
        return time != 0 ? time : string.CompareOrdinal(leftTurn.ToString("D"), rightTurn.ToString("D"));
    }

    internal static int Compare(AgentTranscriptInvocationCursor left, AgentTranscriptInvocationCursor right)
    {
        var order = left.TurnSequence.CompareTo(right.TurnSequence);
        if (order != 0) return order;
        var turn = Compare(AgentTranscriptTime.Read(left.CreatedAtUtc), left.TurnId, AgentTranscriptTime.Read(right.CreatedAtUtc), right.TurnId);
        if (turn != 0) return turn;
        var sequence = left.SequenceNumber.CompareTo(right.SequenceNumber);
        return sequence != 0 ? sequence : string.CompareOrdinal(left.ItemId.ToString("D"), right.ItemId.ToString("D"));
    }
}
