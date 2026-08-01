using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Models;

public sealed record AgentRunActivityUpdate(
    long RunRevision,
    AgentRunActivityKind Kind,
    string Text,
    DateTimeOffset CreatedAtUtc);
