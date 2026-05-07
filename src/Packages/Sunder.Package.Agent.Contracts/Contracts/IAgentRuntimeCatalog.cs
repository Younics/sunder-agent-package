using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

public interface IAgentRuntimeCatalog
{
    event Action<Guid>? SessionChanged;

    event Action<Guid, AgentTurnRecord>? TurnChanged;

    event Action<string>? ProfileChanged;

    IReadOnlyList<AgentSessionRecord> ListSessions();

    IReadOnlyList<AgentSessionRecord> ListSessionsForProfile(string profileId);

    AgentSessionRecord? GetSession(Guid sessionId);

    IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces();

    AgentWorkspaceRecord? GetWorkspace(string workspaceId);

    AgentProfileRecord? GetSessionProfile(Guid sessionId);

    AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId);

    AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId);

    IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit);

    IReadOnlyList<AgentTurnRecord> ListTurnsBefore(Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit);

    IReadOnlyList<AgentProfileRecord> ListProfiles();

    AgentProfileRecord? GetProfile(string profileId);

    AgentProfileModelBindingRecord? GetSessionModelBinding(Guid sessionId, string capabilityKind);

    AgentProfileModelBindingRecord? GetModelBinding(string profileId, string capabilityKind);
}
