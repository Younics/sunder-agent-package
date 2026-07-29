using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

internal interface IAgentTranscriptCatalog
{
    IReadOnlyList<AgentTurnRecord> ListRecentTranscriptHeaders(Guid sessionId, int limit);

    IReadOnlyList<AgentTurnRecord> ListTranscriptHeadersBefore(
        Guid sessionId,
        DateTimeOffset beforeCreatedAtUtc,
        Guid beforeTurnId,
        int limit);

    IReadOnlyList<AgentTurnRecord> ListTranscriptHeadersAfter(
        Guid sessionId,
        DateTimeOffset afterCreatedAtUtc,
        Guid afterTurnId,
        int limit);

    AgentTranscriptToolDetailRecord? GetTranscriptToolDetail(
        AgentTranscriptToolDetailRequest request);
}
