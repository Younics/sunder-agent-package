using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts;

/// <summary>
/// Run-scoped runtime context contract.
/// </summary>
public interface IAgentRunContext
{
    Guid RunId { get; }

    long Revision { get; }

    AgentRunStatus Status { get; }

    bool IsInterrupted { get; }

    DateTimeOffset StartedAtUtc { get; }
}

public sealed record AgentRunContextRecord(
    Guid RunId,
    long Revision,
    AgentRunStatus Status,
    bool IsInterrupted,
    DateTimeOffset StartedAtUtc) : IAgentRunContext;
