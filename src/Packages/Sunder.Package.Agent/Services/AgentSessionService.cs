using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services;

public sealed class AgentSessionService(AgentLocalStore store, IPackageExtensionCatalog? extensionCatalog = null)
{
    private readonly AgentLocalStore _store = store;
    private readonly IPackageExtensionCatalog? _extensionCatalog = extensionCatalog;

    public event Action<Guid>? SessionChanged;

    public event Action<Guid, AgentTurnRecord>? TurnChanged;

    public IReadOnlyList<AgentSessionRecord> ListSessions() => _store.ListSessions();

    public AgentSessionRecord CreateSession(
        string title,
        Guid? parentSessionId = null,
        Guid? rootSessionId = null,
        Guid? parentRunId = null,
        long? parentRunRevision = null,
        string? parentToolCallId = null,
        string? taskId = null,
        string? profileId = null,
        string? behaviorLoopId = null,
        string? agentKind = null)
    {
        var session = _store.CreateSession(title, parentSessionId, rootSessionId, parentRunId, parentRunRevision, parentToolCallId, taskId, profileId, behaviorLoopId, agentKind);
        NotifySessionChanged(session.SessionId);
        return session;
    }

    public AgentSessionRecord? GetSession(Guid sessionId) => _store.GetSession(sessionId);

    public void UpdateSession(AgentSessionRecord session)
    {
        _store.UpdateSession(session);
        NotifySessionChanged(session.SessionId);
    }

    public void DeleteSession(Guid sessionId)
    {
        var deletedSessionIds = _store.DeleteSessionTree(sessionId);
        var cleanupFailures = DeleteExternalSessionData(deletedSessionIds);
        foreach (var deletedSessionId in deletedSessionIds)
        {
            NotifySessionChanged(deletedSessionId);
        }

        if (cleanupFailures.Count > 0)
        {
            throw new AggregateException("Session was deleted, but one or more external cleanup steps failed.", cleanupFailures);
        }
    }

    private IReadOnlyList<Exception> DeleteExternalSessionData(IReadOnlyList<Guid> deletedSessionIds)
    {
        if (_extensionCatalog is null || deletedSessionIds.Count == 0)
        {
            return [];
        }

        var cleaners = _extensionCatalog.GetExtensions(PackageExtensionPoints.SessionDataCleaners);
        if (cleaners.Count == 0)
        {
            return [];
        }

        var failures = new List<Exception>();
        foreach (var deletedSessionId in deletedSessionIds)
        {
            foreach (var cleaner in cleaners)
            {
                try
                {
                    cleaner.DeleteSessionData(deletedSessionId);
                }
                catch (Exception ex)
                {
                    failures.Add(new InvalidOperationException($"Session data cleaner '{cleaner.CleanerId}' failed for session '{deletedSessionId}'.", ex));
                }
            }
        }

        return failures;
    }

    public IReadOnlyList<AgentTurnRecord> ListTurns(Guid sessionId) => _store.ListTurns(sessionId);

    public IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit) => _store.ListRecentTurns(sessionId, limit);

    public IReadOnlyList<AgentTurnRecord> ListTurnsBefore(Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit)
        => _store.ListTurnsBefore(sessionId, beforeCreatedAtUtc, beforeTurnId, limit);

    public IReadOnlyList<AgentTranscriptMessageRecord> ListMessages(Guid sessionId) => _store.ListMessages(sessionId);

    public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => _store.GetLatestCheckpoint(sessionId);

    public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId) => _store.GetWorkingSummary(sessionId);

    public AgentTranscriptMessageRecord AppendMessage(Guid sessionId, AgentMessageRole role, string content)
    {
        var message = _store.AppendMessage(sessionId, role, content);
        NotifySessionChanged(sessionId);
        return message;
    }

    public AgentTurnRecord AppendTextTurn(Guid sessionId, AgentMessageRole role, string content)
    {
        var turn = _store.AppendTextTurn(sessionId, role, content);
        NotifyTurnChanged(sessionId, turn);
        NotifySessionChanged(sessionId);
        return turn;
    }

    public AgentTurnRecord AppendUserTurn(Guid sessionId, AgentMessageRole role, string content, IReadOnlyList<AgentStoredAttachment> attachments)
    {
        var turn = _store.AppendUserTurn(sessionId, role, content, attachments);
        NotifyTurnChanged(sessionId, turn);
        NotifySessionChanged(sessionId);
        return turn;
    }

    public AgentTranscriptMessageRecord UpdateMessageContent(Guid messageId, string content)
    {
        var message = _store.UpdateMessageContent(messageId, content);
        NotifySessionChanged(message.SessionId);
        return message;
    }

    public AgentTurnRecord UpdateTextTurn(Guid turnId, string content)
    {
        var turn = _store.UpdateTextTurn(turnId, content);
        NotifyTurnChanged(turn.SessionId, turn);
        NotifySessionChanged(turn.SessionId);
        return turn;
    }

    public AgentTurnRecord AppendToolCallTurn(Guid sessionId, AgentMessageRole role, string callId, string toolId, string argumentsJson)
    {
        var turn = _store.AppendToolCallTurn(sessionId, role, callId, toolId, argumentsJson);
        NotifyTurnChanged(sessionId, turn);
        NotifySessionChanged(sessionId);
        return turn;
    }

    public AgentTurnRecord AppendToolResultTurn(
        Guid sessionId,
        string callId,
        string toolId,
        string? argumentsJson,
        string? content,
        string? resultSummary,
        string? structuredPayloadJson,
        string? sourcesJson,
        bool wasTruncated,
        bool isError,
        string? errorCode,
        string? backendId)
    {
        var turn = _store.AppendToolResultTurn(
            sessionId,
            callId,
            toolId,
            argumentsJson,
            content,
            resultSummary,
            structuredPayloadJson,
            sourcesJson,
            wasTruncated,
            isError,
            errorCode,
            backendId);
        NotifyTurnChanged(sessionId, turn);
        NotifySessionChanged(sessionId);
        return turn;
    }

    public AgentRunCheckpointRecord SaveCheckpoint(Guid sessionId, long runRevision, AgentRunStatus status, string? summary)
    {
        var checkpoint = _store.SaveCheckpoint(sessionId, runRevision, status, summary);
        NotifySessionChanged(sessionId);
        return checkpoint;
    }

    public AgentWorkingSummaryRecord? SaveWorkingSummary(Guid sessionId, string? summaryText)
    {
        var summary = _store.SaveWorkingSummary(sessionId, summaryText);
        NotifySessionChanged(sessionId);
        return summary;
    }

    public long GetNextRunRevision(Guid sessionId) => _store.GetNextRunRevision(sessionId);

    private void NotifySessionChanged(Guid sessionId)
    {
        var handlers = SessionChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<Guid> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(sessionId);
            }
            catch
            {
                // UI or extension listeners must not break persisted agent state changes.
            }
        }
    }

    private void NotifyTurnChanged(Guid sessionId, AgentTurnRecord turn)
    {
        var handlers = TurnChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<Guid, AgentTurnRecord> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(sessionId, turn);
            }
            catch
            {
                // UI or extension listeners must not break persisted agent turn changes.
            }
        }
    }
}
