using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptRowProjector<TRow> where TRow : class
{
    private readonly Dictionary<(Guid Session, string Reference), OperationHeader> _operationHeaders = [];

    // Bounded, display-only correlation also runs when a detached reader buffers
    // the new check. Neither the retained turn nor its original receipt is changed.
    internal bool ApplyOperationObservation(AgentTurnRecord turn, bool force = false)
    {
        if (turn.Kind != AgentTurnKind.ToolResult) return false;
        var changed = false;
        foreach (var item in turn.Items.Where(item => item.Kind == AgentTurnItemKind.ToolResult))
        {
            if (ToolOperationPresentation.Reference(item.PresentationPayloadJson) is not { } reference) continue;
            var peers = _turnWindow.OrderedTurns().Where(peer => peer.SessionId == turn.SessionId)
                .SelectMany(peer => peer.Items.Where(value => value.Kind == AgentTurnItemKind.ToolResult
                    && ToolOperationPresentation.Reference(value.PresentationPayloadJson) == reference).Select(value => (Turn: peer, Item: value)))
                .Where(peer => _toolRowsByKey.ContainsKey(CreateToolKey(peer.Turn, peer.Item))).ToArray();
            if (peers.Length == 0) continue;
            var key = (turn.SessionId, reference);
            var incoming = new OperationHeader(AgentTranscriptTime.Read(turn.CreatedAtUtc), turn.TurnId, item.SequenceNumber, turn.ContentRevision,
                item.PresentationPayloadJson!, item.ToolErrorSummary ?? item.ToolHeaderHint ?? item.ResultSummary);
            if (!_operationHeaders.TryGetValue(key, out var current) || incoming.IsNewerThan(current))
            {
                _operationHeaders[key] = current = incoming;
            }
            else if (!force) continue;
            if (!changed) RowsChanging?.Invoke();
            foreach (var peer in peers)
            {
                var projected = ProjectOperationHeader(peer.Item, current);
                _factory.ApplyToolResult(_toolRowsByKey[CreateToolKey(peer.Turn, peer.Item)], peer.Turn, projected,
                    DescribeCorrelatedTool(peer.Turn, projected));
            }
            changed = true;
        }
        return changed;
    }

    private static AgentTurnItemRecord ProjectOperationHeader(AgentTurnItemRecord item, OperationHeader observed)
    {
        var failure = ToolOperationPresentation.Read(observed.Payload) is "Failed" or "Cancelled" or "Unconfirmed";
        var unavailableCheck = item.ToolId == "maker_operation" && ToolOperationPresentation.ObservationUnavailable(item.PresentationPayloadJson);
        return item with
        {
            PresentationPayloadJson = ToolOperationPresentation.Correlate(item.PresentationPayloadJson, observed.Payload),
            ToolHeaderHint = unavailableCheck ? item.ToolHeaderHint : failure ? null : observed.Summary,
            ToolErrorSummary = unavailableCheck ? item.ToolErrorSummary : failure ? observed.Summary : null,
        };
    }

    private void PruneOperationHeaders()
    {
        var retained = _turnWindow.OrderedTurns().SelectMany(turn => turn.Items.Select(item =>
            (turn.SessionId, Reference: ToolOperationPresentation.Reference(item.PresentationPayloadJson))))
            .Where(key => key.Reference is not null).ToHashSet();
        foreach (var key in _operationHeaders.Keys.Where(key => !retained.Contains(key)).ToArray()) _operationHeaders.Remove(key);
    }

    private sealed record OperationHeader(DateTimeOffset Created, Guid TurnId, long Sequence, long Revision, string Payload, string? Summary)
    {
        internal bool IsNewerThan(OperationHeader other)
        {
            // Losing access to a later check cannot undo an already observed,
            // definite effect. A later definite observation may still supersede it.
            if (ToolOperationPresentation.IsTerminal(Payload) != ToolOperationPresentation.IsTerminal(other.Payload))
                return ToolOperationPresentation.IsTerminal(Payload);
            return Created > other.Created || Created == other.Created && (TurnId.CompareTo(other.TurnId) > 0
                || TurnId == other.TurnId && (Sequence > other.Sequence || Sequence == other.Sequence && Revision > other.Revision));
        }
    }
}
