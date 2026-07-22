using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Services;

public sealed partial class AgentSessionService
{
    public IReadOnlyList<AgentSessionRecord> ListSessions() => _store.ListSessions();

    public IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(string workspaceId)
        => _store.ListSessionsForWorkspace(workspaceId);

    public AgentSessionRecord? GetSession(Guid sessionId) => _store.GetSession(sessionId);

    public IReadOnlyList<AgentTurnRecord> ListTurns(Guid sessionId) => _store.ListTurns(sessionId);

    public IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit)
        => _store.ListRecentTurns(sessionId, limit);

    public IReadOnlyList<AgentTurnRecord> ListTurnsBefore(
        Guid sessionId,
        DateTimeOffset beforeCreatedAtUtc,
        Guid beforeTurnId,
        int limit)
        => _store.ListTurnsBefore(sessionId, beforeCreatedAtUtc, beforeTurnId, limit);

    public IReadOnlyList<AgentTurnRecord> ListTurnsAfter(
        Guid sessionId,
        DateTimeOffset afterCreatedAtUtc,
        Guid afterTurnId,
        int limit)
        => _store.ListTurnsAfter(sessionId, afterCreatedAtUtc, afterTurnId, limit);

    public AgentTurnRecord? GetTurn(Guid turnId) => _store.GetTurn(turnId);

    public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId)
        => _store.GetLatestCheckpoint(sessionId);

    internal AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId, long runRevision)
        => _store.GetLatestCheckpoint(sessionId, runRevision);

    internal AgentDurableRunRecord? GetRun(Guid runId) => _store.GetRun(runId);

    internal AgentDurableRunRecord? GetLatestRun(Guid sessionId) => _store.GetLatestRun(sessionId);

    internal AgentDurableRunLease? GetRunLease(Guid runId)
        => _store.GetRun(runId) is { } run ? new AgentDurableRunLease(run) : null;

    internal IReadOnlyList<AgentParentContinuationWorkRecord> ListDispatchableParentContinuationWork()
        => _store.ListDispatchableParentContinuationWork();

    public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId)
    {
        var contextCheckpoint = _store.GetLatestSessionContextCheckpoint(sessionId);
        return contextCheckpoint is null
            ? null
            : new AgentWorkingSummaryRecord(
                sessionId,
                contextCheckpoint.SummaryText,
                contextCheckpoint.CreatedAtUtc);
    }

    public AgentSessionContextCheckpointRecord? GetLatestSessionContextCheckpoint(Guid sessionId)
        => _store.GetLatestSessionContextCheckpoint(sessionId);

    public long GetNextRunRevision(Guid sessionId) => _store.GetNextRunRevision(sessionId);
}
