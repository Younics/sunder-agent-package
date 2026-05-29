using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services;

public sealed class AgentSessionService(AgentLocalStore store, IPackageExtensionCatalog? extensionCatalog = null)
{
    private readonly AgentLocalStore _store = store;
    private readonly IPackageExtensionCatalog? _extensionCatalog = extensionCatalog;

    public event Action<Guid>? SessionChanged;

    public event Action<Guid, AgentTurnRecord>? TurnChanged;

    public event Action<Guid>? TranscriptReset;

    public event Action<Guid, AgentRunActivityUpdate>? RunActivityChanged;

    public IReadOnlyList<AgentSessionRecord> ListSessions() => _store.ListSessions();

    public IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(string workspaceId)
        => _store.ListSessionsForWorkspace(workspaceId);

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
        string? agentKind = null,
        string? workspaceId = null)
    {
        workspaceId = ResolveWorkspaceId(parentSessionId, workspaceId);
        var session = _store.CreateSession(title, parentSessionId, rootSessionId, parentRunId, parentRunRevision, parentToolCallId, taskId, profileId, behaviorLoopId, agentKind, workspaceId);
        NotifySessionChanged(session.SessionId);
        return session;
    }

    public AgentSessionRecord? GetSession(Guid sessionId) => _store.GetSession(sessionId);

    public void UpdateSession(AgentSessionRecord session)
    {
        session = NormalizeSessionWorkspaceForUpdate(session);
        _store.UpdateSession(session);
        NotifySessionChanged(session.SessionId);
    }

    public void DeleteSession(Guid sessionId)
    {
        var deletedSessionIds = _store.DeleteSessionTree(sessionId);
        CompleteSessionDeletion(deletedSessionIds);
    }

    public void DeleteSessionsForWorkspace(string workspaceId)
    {
        var deletedSessionIds = _store.DeleteSessionTreesForWorkspace(workspaceId);
        CompleteSessionDeletion(deletedSessionIds);
    }

    private void CompleteSessionDeletion(IReadOnlyList<Guid> deletedSessionIds)
    {
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

    private string ResolveWorkspaceId(Guid? parentSessionId, string? workspaceId)
    {
        var normalizedWorkspaceId = NormalizeWorkspaceId(workspaceId);
        if (parentSessionId is not null)
        {
            var parentWorkspaceId = NormalizeWorkspaceId(_store.GetSession(parentSessionId.Value)?.WorkspaceId);
            if (parentWorkspaceId is null)
            {
                throw new InvalidOperationException("Child sessions must have a parent session with an assigned workspace.");
            }

            if (normalizedWorkspaceId is not null
                && !string.Equals(normalizedWorkspaceId, parentWorkspaceId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Child sessions must use their parent session workspace.");
            }

            return parentWorkspaceId;
        }

        if (normalizedWorkspaceId is null)
        {
            throw new InvalidOperationException("Root sessions must be created with an explicit workspace id.");
        }

        if (IsUnassignedSessionsWorkspace(normalizedWorkspaceId))
        {
            throw new InvalidOperationException("Root sessions cannot be created in Unassigned Sessions.");
        }

        return normalizedWorkspaceId;
    }

    private AgentSessionRecord NormalizeSessionWorkspaceForUpdate(AgentSessionRecord session)
    {
        var workspaceId = NormalizeWorkspaceId(session.WorkspaceId)
            ?? throw new InvalidOperationException("Sessions must have an assigned workspace.");
        var current = _store.GetSession(session.SessionId);
        var currentWorkspaceId = NormalizeWorkspaceId(current?.WorkspaceId);
        if (currentWorkspaceId is null)
        {
            return session with { WorkspaceId = workspaceId };
        }

        if (IsUnassignedSessionsWorkspace(workspaceId) && !IsUnassignedSessionsWorkspace(currentWorkspaceId))
        {
            throw new InvalidOperationException("Sessions cannot be moved to Unassigned Sessions.");
        }

        if (!IsUnassignedSessionsWorkspace(workspaceId)
            && !IsUnassignedSessionsWorkspace(currentWorkspaceId)
            && !string.Equals(workspaceId, currentWorkspaceId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Sessions cannot be moved between workspaces.");
        }

        if (!IsUnassignedSessionsWorkspace(workspaceId)
            && IsUnassignedSessionsWorkspace(currentWorkspaceId)
            && current?.ParentSessionId is null)
        {
            throw new InvalidOperationException("Unassigned root sessions cannot be moved into a workspace.");
        }

        return session with { WorkspaceId = workspaceId };
    }

    private static string? NormalizeWorkspaceId(string? workspaceId)
        => string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId.Trim();

    private static bool IsUnassignedSessionsWorkspace(string workspaceId)
        => string.Equals(workspaceId, AgentLocalStore.UnassignedSessionsWorkspaceId, StringComparison.OrdinalIgnoreCase);

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

    public IReadOnlyList<AgentTurnRecord> ListTurnsAfter(Guid sessionId, DateTimeOffset afterCreatedAtUtc, Guid afterTurnId, int limit)
        => _store.ListTurnsAfter(sessionId, afterCreatedAtUtc, afterTurnId, limit);

    public AgentTurnRecord? GetTurn(Guid turnId) => _store.GetTurn(turnId);

    public IReadOnlyList<AgentTranscriptMessageRecord> ListMessages(Guid sessionId) => _store.ListMessages(sessionId);

    public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => _store.GetLatestCheckpoint(sessionId);

    public void ReportRunActivity(Guid sessionId, long runRevision, AgentRunActivityKind kind, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        NotifyRunActivityChanged(
            sessionId,
            new AgentRunActivityUpdate(runRevision, kind, text.Trim(), DateTimeOffset.UtcNow));
    }

    public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId)
    {
        var contextCheckpoint = _store.GetLatestSessionContextCheckpoint(sessionId);
        if (contextCheckpoint is not null)
        {
            return new AgentWorkingSummaryRecord(
                sessionId,
                contextCheckpoint.SummaryText,
                contextCheckpoint.CreatedAtUtc);
        }

        return _store.GetWorkingSummary(sessionId);
    }

    public AgentSessionContextCheckpointRecord? GetLatestSessionContextCheckpoint(Guid sessionId)
        => _store.GetLatestSessionContextCheckpoint(sessionId);

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

    public AgentTranscriptRollbackResult RollbackTranscript(Guid sessionId, Guid anchorTurnId)
    {
        var result = _store.RollbackTranscript(sessionId, anchorTurnId);
        var cleanupFailures = DeleteExternalSessionData(result.DeletedSessionIds);

        NotifyTranscriptReset(sessionId);
        NotifySessionChanged(sessionId);
        foreach (var deletedSessionId in result.DeletedSessionIds)
        {
            NotifySessionChanged(deletedSessionId);
        }

        if (cleanupFailures.Count > 0)
        {
            throw new AggregateException("Transcript was rolled back, but one or more external cleanup steps failed.", cleanupFailures);
        }

        return result;
    }

    public AgentTurnRecord UpdateTextTurn(Guid turnId, string content)
    {
        var turn = _store.UpdateTextTurn(turnId, content);
        NotifyTurnChanged(turn.SessionId, turn);
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
        string? backendId,
        string? presentationPayloadJson = null)
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
            backendId,
            presentationPayloadJson);
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

    public AgentSessionContextCheckpointRecord SaveSessionContextCheckpoint(
        Guid sessionId,
        Guid? firstOmittedTurnId,
        Guid? lastOmittedTurnId,
        int omittedTurnCount,
        string summaryText,
        string? detailsJson)
    {
        var checkpoint = _store.SaveSessionContextCheckpoint(
            sessionId,
            firstOmittedTurnId,
            lastOmittedTurnId,
            omittedTurnCount,
            summaryText,
            detailsJson);
        NotifySessionChanged(sessionId);
        return checkpoint;
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

    private void NotifyTranscriptReset(Guid sessionId)
    {
        var handlers = TranscriptReset;
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

    private void NotifyRunActivityChanged(Guid sessionId, AgentRunActivityUpdate activity)
    {
        var handlers = RunActivityChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<Guid, AgentRunActivityUpdate> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(sessionId, activity);
            }
            catch
            {
                // Live activity listeners must not break agent execution.
            }
        }
    }
}
