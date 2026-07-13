using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts;

/// <summary>
/// Session-scoped runtime context contract.
/// </summary>
public interface IAgentSessionContext
{
    Guid SessionId { get; }

    string ProfileId { get; }

    string ProfileDisplayName { get; }

    string SessionTitle { get; }

    AgentSessionState SessionState { get; }

    [Obsolete("Active session continuity summaries are owned by Sunder Agent core. Use runtime session context checkpoints for continuity and durable memory APIs for long-lived facts.")]
    string? WorkingSummary { get; }
}

public sealed record AgentSessionContextRecord(
    Guid SessionId,
    string ProfileId,
    string ProfileDisplayName,
    string SessionTitle,
    AgentSessionState SessionState,
    [property: Obsolete("Active session continuity summaries are owned by Sunder Agent core. Use runtime session context checkpoints for continuity and durable memory APIs for long-lived facts.")]
    string? WorkingSummary) : IAgentSessionContext;
