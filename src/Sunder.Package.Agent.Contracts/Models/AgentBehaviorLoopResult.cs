namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentBehaviorLoopResult(
    AgentRunCheckpointRecord Checkpoint,
    AgentBehaviorLoopCompletionKind CompletionKind);
