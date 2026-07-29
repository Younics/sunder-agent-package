using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Services;

public sealed partial class AgentSessionService
{
    public AgentTurnRecord AppendTextTurn(Guid sessionId, AgentMessageRole role, string content)
    {
        var turn = _store.AppendTextTurn(sessionId, role, content);
        NotifyTurnAndSessionChanged(sessionId, turn);
        return turn;
    }

    internal AgentTurnRecord AppendTextTurn(
        AgentDurableRunLease lease,
        AgentMessageRole role,
        string content)
    {
        AgentTurnRecord? turn;
        lock (lease.SyncRoot)
        {
            turn = _store.TryAppendTextTurn(lease.Key, lease.Epoch, role, content);
            if (turn is not null)
            {
                EnqueueLeaseNotification(
                    lease,
                    () => NotifyTurnAndSessionChanged(lease.Key.SessionId, turn));
            }
        }

        if (turn is null)
        {
            throw new AgentRunTranscriptWriteRejectedException();
        }

        DrainLeaseNotifications(lease);
        return turn;
    }

    public AgentTurnRecord AppendUserTurn(
        Guid sessionId,
        AgentMessageRole role,
        string content,
        IReadOnlyList<AgentStoredAttachment> attachments)
    {
        var turn = _store.AppendUserTurn(sessionId, role, content, attachments);
        NotifyTurnAndSessionChanged(sessionId, turn);
        return turn;
    }

    public AgentTranscriptRollbackResult RollbackTranscript(Guid sessionId, Guid anchorTurnId)
    {
        var result = _store.RollbackTranscript(
            sessionId,
            anchorTurnId,
            SnapshotSessionDataCleaners());
        NotifyTranscriptResetAndSessionChanged(sessionId);
        foreach (var deletedSessionId in result.DeletedSessionIds)
        {
            NotifySessionChanged(deletedSessionId);
        }
        if (result.DeletedSessionIds.Count > 0)
        {
            DispatchPendingSessionCleanup();
        }
        return result;
    }
}
