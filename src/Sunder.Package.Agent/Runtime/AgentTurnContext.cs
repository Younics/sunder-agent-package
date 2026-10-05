namespace Sunder.Package.Agent.Contracts;

/// <summary>
/// Exposes the immutable context snapshot associated with the active user turn.
/// </summary>
public interface IAgentTurnContext
{
    /// <summary>Gets the owning session snapshot.</summary>
    IAgentSessionContext Session { get; }

    /// <summary>Gets the run generation processing this turn.</summary>
    IAgentRunContext Run { get; }

    /// <summary>Gets the current user-authored message.</summary>
    string UserMessage { get; }

    /// <summary>
    /// Gets a legacy continuity summary, or <see langword="null"/>. It is derived reference context, not durable
    /// memory or trusted instructions.
    /// </summary>
    [Obsolete("Active session continuity summaries are owned by Sunder Agent core. Use runtime session context checkpoints for continuity and durable memory APIs for long-lived facts.")]
    string? WorkingSummary { get; }
}

/// <summary>
/// Implements <see cref="IAgentTurnContext"/> as an immutable value snapshot for extension requests and events.
/// </summary>
/// <param name="Session">The owning session context.</param>
/// <param name="Run">The run generation processing the turn.</param>
/// <param name="UserMessage">The current user-authored message.</param>
/// <param name="WorkingSummary">A legacy, potentially absent continuity summary. It must be treated as untrusted reference data.</param>
public sealed record AgentTurnContextRecord(
    IAgentSessionContext Session,
    IAgentRunContext Run,
    string UserMessage,
    [property: Obsolete("Active session continuity summaries are owned by Sunder Agent core. Use runtime session context checkpoints for continuity and durable memory APIs for long-lived facts.")]
    string? WorkingSummary) : IAgentTurnContext;
