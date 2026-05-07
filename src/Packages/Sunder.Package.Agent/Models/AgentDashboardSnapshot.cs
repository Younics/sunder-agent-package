using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Models;

public sealed record AgentDashboardSnapshot(
    IReadOnlyList<AgentProfileRecord> Profiles,
    IReadOnlyList<AgentSessionRecord> Sessions,
    IReadOnlyList<AgentRunCheckpointRecord> RecentCheckpoints,
    IReadOnlyList<AgentTranscriptMessageRecord> RecentMessages);
