namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Reports the runtime control-flow outcome of processing one tool call.
/// </summary>
/// <remarks>
/// This outcome is distinct from <see cref="AgentToolResult.IsError" />: an executed tool can return
/// an error result and still allow the provider loop to continue.
/// </remarks>
/// <param name="Kind">The control-flow disposition of the call.</param>
/// <param name="Checkpoint">The durable run checkpoint associated with suspension or termination, when one was written.</param>
/// <param name="Result">The tool result when execution or a recorded denial produced one.</param>
public sealed record AgentToolCallOutcome(
    AgentToolCallOutcomeKind Kind,
    AgentRunCheckpointRecord? Checkpoint = null,
    AgentToolResult? Result = null);
