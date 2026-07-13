namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentTranscriptRollbackResult(
    Guid SessionId,
    Guid AnchorTurnId,
    IReadOnlyList<Guid> DeletedTurnIds,
    IReadOnlyList<Guid> DeletedSessionIds);
