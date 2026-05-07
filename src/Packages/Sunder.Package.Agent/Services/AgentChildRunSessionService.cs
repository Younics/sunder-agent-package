using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services;

public sealed class AgentChildRunSessionService(
    AgentSessionService sessionService,
    AgentProfileService profileService)
{
    private readonly AgentSessionService _sessionService = sessionService;
    private readonly AgentProfileService _profileService = profileService;

    internal AgentChildRunSessionContext PrepareChildSession(AgentChildRunRequest request)
    {
        var parentSession = _sessionService.GetSession(request.ParentSessionId)
            ?? throw new InvalidOperationException($"Parent session '{request.ParentSessionId}' was not found.");
        var childProfile = request.ChildProfile with
        {
            IsInternal = true,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        _profileService.SaveRuntimeProfile(childProfile);

        var childSession = ResolveChildSession(parentSession, request)
                           ?? _sessionService.CreateSession(
                               string.IsNullOrWhiteSpace(request.Title) ? childProfile.DisplayName : request.Title.Trim(),
                               parentSessionId: parentSession.SessionId,
                               rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
                               parentRunId: request.ParentRunId,
                               parentRunRevision: request.ParentRunRevision,
                               parentToolCallId: request.ParentToolCallId,
                               taskId: NormalizeTaskId(request.TaskId),
                               profileId: childProfile.ProfileId,
                               behaviorLoopId: childProfile.BehaviorLoopId,
                               agentKind: request.AgentKind);
        if (childSession.ParentRunId != request.ParentRunId
            || childSession.ParentRunRevision != request.ParentRunRevision
            || !string.Equals(childSession.ParentToolCallId, request.ParentToolCallId, StringComparison.Ordinal))
        {
            childSession = childSession with
            {
                ParentRunId = request.ParentRunId,
                ParentRunRevision = request.ParentRunRevision,
                ParentToolCallId = request.ParentToolCallId,
                ProfileId = childProfile.ProfileId,
                BehaviorLoopId = childProfile.BehaviorLoopId,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            _sessionService.UpdateSession(childSession);
        }

        return new AgentChildRunSessionContext(childSession, childProfile);
    }

    internal AgentChildRunResult BuildResult(AgentSessionRecord childSession, AgentRunCheckpointRecord checkpoint)
        => new(
            childSession.SessionId,
            checkpoint.Status,
            checkpoint.Summary ?? checkpoint.Status.ToString(),
            RenderLastAssistantText(childSession.SessionId),
            childSession.Title);

    internal string? RenderLastAssistantText(Guid sessionId)
        => _sessionService.ListTurns(sessionId)
            .Where(turn => turn.Role == AgentMessageRole.Assistant && turn.Kind == AgentTurnKind.Message)
            .OrderByDescending(turn => turn.CreatedAtUtc)
            .ThenByDescending(turn => turn.TurnId)
            .Select(turn => string.Join("\n\n", turn.Items
                .Where(item => item.Kind == AgentTurnItemKind.Text && !string.IsNullOrWhiteSpace(item.TextContent))
                .Select(item => item.TextContent!.Trim())))
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

    private AgentSessionRecord? ResolveChildSession(AgentSessionRecord parentSession, AgentChildRunRequest request)
    {
        var taskId = NormalizeTaskId(request.TaskId);
        if (taskId is null)
        {
            return null;
        }

        return _sessionService.ListSessions()
            .FirstOrDefault(session => session.ParentSessionId == parentSession.SessionId
                                       && string.Equals(session.TaskId, taskId, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeTaskId(string? taskId)
        => string.IsNullOrWhiteSpace(taskId) ? null : taskId.Trim();
}

internal sealed record AgentChildRunSessionContext(
    AgentSessionRecord ChildSession,
    AgentProfileRecord ChildProfile);
