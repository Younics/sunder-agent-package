namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Reports the durable state and control-flow outcome produced by one behavior-loop invocation.
/// </summary>
/// <remarks>
/// The host may reconcile this value with a newer terminal checkpoint when the run is stopped, superseded, or
/// interrupted concurrently. Consumers should use the returned checkpoint for persisted state and the completion
/// kind to decide how loop execution ended.
/// </remarks>
/// <param name="Checkpoint">The checkpoint persisted or observed when the loop stopped executing.</param>
/// <param name="CompletionKind">The loop's completion classification, including resumable approval suspension.</param>
public sealed record AgentBehaviorLoopResult(
    AgentRunCheckpointRecord Checkpoint,
    AgentBehaviorLoopCompletionKind CompletionKind);
