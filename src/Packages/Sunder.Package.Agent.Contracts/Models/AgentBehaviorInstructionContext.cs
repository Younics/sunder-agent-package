namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentBehaviorInstructionContext(
    string? SystemInstructions,
    bool HasSupplementaryContext);
