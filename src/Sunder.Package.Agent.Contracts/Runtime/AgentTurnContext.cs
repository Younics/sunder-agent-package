namespace Sunder.Package.Agent.Contracts;

/// <summary>
/// Turn-scoped runtime context contract.
/// </summary>
public interface IAgentTurnContext
{
    IAgentSessionContext Session { get; }

    IAgentRunContext Run { get; }

    string UserMessage { get; }

    [Obsolete("Active session continuity summaries are owned by Sunder Agent core. Use runtime session context checkpoints for continuity and durable memory APIs for long-lived facts.")]
    string? WorkingSummary { get; }
}

public sealed record AgentTurnContextRecord(
    IAgentSessionContext Session,
    IAgentRunContext Run,
    string UserMessage,
    [property: Obsolete("Active session continuity summaries are owned by Sunder Agent core. Use runtime session context checkpoints for continuity and durable memory APIs for long-lived facts.")]
    string? WorkingSummary) : IAgentTurnContext;
