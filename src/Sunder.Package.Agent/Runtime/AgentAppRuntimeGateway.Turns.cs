using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Runtime;

internal sealed partial class AgentAppRuntimeGateway
{
    private void CacheTurns(IEnumerable<AgentTurnRecord> turns)
    {
        lock (_cacheLock)
        {
            foreach (var turn in turns)
            {
                CacheTurnCore(turn);
            }
        }
    }

    private void CacheTurn(AgentTurnRecord turn)
    {
        lock (_cacheLock)
        {
            CacheTurnCore(turn);
        }
    }

    private bool CacheTurnCore(AgentTurnRecord turn)
    {
        if (_knownTurns.TryGetValue(turn.TurnId, out var current)
            && (current.ContentRevision > turn.ContentRevision
                || current.ContentRevision == turn.ContentRevision
                && current.UpdatedAtUtc > turn.UpdatedAtUtc))
        {
            return false;
        }

        if (turn.IsStreaming)
        {
            _knownTurns[turn.TurnId] = turn;
        }
        else
        {
            _knownTurns.Remove(turn.TurnId);
        }
        return true;
    }

    private AgentTurnRecord? ApplyTurnMutationToCache(AgentTurnMutation mutation)
    {
        lock (_cacheLock)
        {
            if (mutation.Turn is { } replacement)
            {
                return CacheTurnCore(replacement) ? replacement : null;
            }

            if (!_knownTurns.TryGetValue(mutation.TurnId, out var current)
                || current.SessionId != mutation.SessionId
                || current.ContentRevision + 1 != mutation.ContentRevision)
            {
                return null;
            }

            var items = current.Items.ToArray();
            var textIndex = Array.FindIndex(items, item => item.Kind == AgentTurnItemKind.Text);
            if (textIndex < 0)
            {
                return null;
            }

            var currentContent = items[textIndex].TextContent ?? string.Empty;
            if (currentContent.Length != mutation.BaseContentLength)
            {
                return null;
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
                return null;
            }

            var updated = current with
            {
                Items = items,
                UpdatedAtUtc = mutation.UpdatedAtUtc,
                ContentRevision = mutation.ContentRevision,
                IsStreaming = mutation.Kind != AgentTurnMutationKind.Complete,
            };
            if (updated.IsStreaming)
            {
                _knownTurns[updated.TurnId] = updated;
            }
            else
            {
                _knownTurns.Remove(updated.TurnId);
            }
            return updated;
        }
    }

    private void RemoveCachedTurns(Guid sessionId)
    {
        lock (_cacheLock)
        {
            RemoveCachedTurnsCore(sessionId);
        }
    }

    private void RemoveCachedTurnsCore(Guid sessionId)
    {
        foreach (var turnId in _knownTurns.Values
                     .Where(turn => turn.SessionId == sessionId)
                     .Select(turn => turn.TurnId)
                     .ToArray())
        {
            _knownTurns.Remove(turnId);
        }
    }
}
