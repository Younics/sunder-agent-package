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

    string? WorkingSummary { get; }
}

public sealed record AgentSessionContextRecord(
    Guid SessionId,
    string ProfileId,
    string ProfileDisplayName,
    string SessionTitle,
    AgentSessionState SessionState,
    string? WorkingSummary) : IAgentSessionContext;
