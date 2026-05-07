using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services;

public sealed class AgentRuntimeCatalog : IAgentRuntimeCatalog
{
    private readonly AgentSessionService _sessionService;
    private readonly AgentProfileService _profileService;
    private readonly AgentWorkspaceService _workspaceService;

    public AgentRuntimeCatalog(
        AgentSessionService sessionService,
        AgentProfileService profileService,
        AgentWorkspaceService workspaceService)
    {
        _sessionService = sessionService;
        _profileService = profileService;
        _workspaceService = workspaceService;
        _sessionService.SessionChanged += sessionId => SessionChanged?.Invoke(sessionId);
        _sessionService.TurnChanged += (sessionId, turn) => TurnChanged?.Invoke(sessionId, turn);
        _profileService.ProfileChanged += profileId => ProfileChanged?.Invoke(profileId);
    }

    public event Action<Guid>? SessionChanged;

    public event Action<Guid, AgentTurnRecord>? TurnChanged;

    public event Action<string>? ProfileChanged;

    public IReadOnlyList<AgentSessionRecord> ListSessions() => _sessionService.ListSessions();

    public IReadOnlyList<AgentSessionRecord> ListSessionsForProfile(string profileId)
        => string.IsNullOrWhiteSpace(profileId)
            ? []
            : _sessionService.ListSessions()
                .Where(session => string.Equals(session.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
                .ToArray();

    public AgentSessionRecord? GetSession(Guid sessionId) => _sessionService.GetSession(sessionId);

    public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => _workspaceService.ListWorkspaces();

    public AgentWorkspaceRecord? GetWorkspace(string workspaceId) => _workspaceService.GetWorkspace(workspaceId);

    public AgentProfileRecord? GetSessionProfile(Guid sessionId)
    {
        var profileId = _sessionService.GetSession(sessionId)?.ProfileId;
        return string.IsNullOrWhiteSpace(profileId) ? null : _profileService.GetProfile(profileId);
    }

    public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId) => _sessionService.GetWorkingSummary(sessionId);

    public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => _sessionService.GetLatestCheckpoint(sessionId);

    public IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit) => _sessionService.ListRecentTurns(sessionId, limit);

    public IReadOnlyList<AgentTurnRecord> ListTurnsBefore(Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit)
        => _sessionService.ListTurnsBefore(sessionId, beforeCreatedAtUtc, beforeTurnId, limit);

    public IReadOnlyList<AgentProfileRecord> ListProfiles() => _profileService.ListProfiles();

    public AgentProfileRecord? GetProfile(string profileId) => _profileService.GetProfile(profileId);

    public AgentProfileModelBindingRecord? GetSessionModelBinding(Guid sessionId, string capabilityKind)
    {
        var profileId = _sessionService.GetSession(sessionId)?.ProfileId;
        return string.IsNullOrWhiteSpace(profileId)
            ? null
            : _profileService.GetModelBinding(profileId, capabilityKind);
    }

    public AgentProfileModelBindingRecord? GetModelBinding(string profileId, string capabilityKind)
        => _profileService.GetModelBinding(profileId, capabilityKind);
}
