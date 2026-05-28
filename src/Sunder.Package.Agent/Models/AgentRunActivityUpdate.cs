namespace Sunder.Package.Agent.Models;

public enum AgentRunActivityKind
{
    Thinking = 0,
    Reasoning = 1,
    Tool = 2,
    Processing = 3,
}

public sealed record AgentRunActivityUpdate(
    long RunRevision,
    AgentRunActivityKind Kind,
    string Text,
    DateTimeOffset CreatedAtUtc);
