namespace Sunder.Package.Agent.Contracts.Models;

public enum AgentTurnMutationKind
{
    Add,
    Append,
    Replace,
    Complete,
}

public sealed record AgentTurnMutation(
    Guid SessionId,
    Guid TurnId,
    long ContentRevision,
    AgentTurnMutationKind Kind,
    int BaseContentLength,
    string? Text,
    DateTimeOffset UpdatedAtUtc,
    AgentTurnRecord? Turn = null,
    long RuntimeRevision = 0);
