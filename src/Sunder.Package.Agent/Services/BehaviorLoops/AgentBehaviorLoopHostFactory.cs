using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

public sealed class AgentBehaviorLoopHostFactory(
    AgentSessionService sessionService,
    AgentToolService toolService,
    AgentPermissionService permissionService,
    AgentMemoryCoordinator memoryCoordinator,
    AgentRunEventLogger runEventLogger,
    AgentActiveRunRegistry activeRunRegistry)
{
    private readonly AgentSessionService _sessionService = sessionService;
    private readonly AgentToolService _toolService = toolService;
    private readonly AgentPermissionService _permissionService = permissionService;
    private readonly AgentMemoryCoordinator _memoryCoordinator = memoryCoordinator;
    private readonly AgentRunEventLogger _runEventLogger = runEventLogger;
    private readonly AgentActiveRunRegistry _activeRunRegistry = activeRunRegistry;

    internal AgentBehaviorLoopHost Create(
        IAgentChatProvider provider,
        AgentSessionRecord session,
        AgentProfileRecord profile,
        AgentWorkspaceRecord workspace,
        Guid runId,
        long runRevision,
        DateTimeOffset runStartedAtUtc,
        string userMessage,
        Guid userTurnId)
        => new(
            _sessionService,
            _toolService,
            _permissionService,
            _memoryCoordinator,
            _runEventLogger.EventLogger,
            provider,
            session,
            profile,
            workspace,
            runId,
            runRevision,
            runStartedAtUtc,
            userMessage,
            userTurnId,
            () => _activeRunRegistry.IsCurrent(session.SessionId, runRevision));
}
