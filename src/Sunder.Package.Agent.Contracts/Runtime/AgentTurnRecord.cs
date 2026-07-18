namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentTurnRecord(
    Guid TurnId,
    Guid SessionId,
    AgentMessageRole Role,
    AgentTurnKind Kind,
    IReadOnlyList<AgentTurnItemRecord> Items,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public long ContentRevision { get; init; } = 1;

    public bool IsStreaming { get; init; }
}
