namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentMemoryRecallResult(
    IReadOnlyList<AgentMemoryRecallEntry> Entries);
