namespace Sunder.Package.Agent.Contracts;

/// <summary>
/// Turn-scoped runtime context contract.
/// </summary>
public interface IAgentTurnContext
{
    IAgentSessionContext Session { get; }

    IAgentRunContext Run { get; }

    string UserMessage { get; }

    string? WorkingSummary { get; }
}

public sealed record AgentTurnContextRecord(
    IAgentSessionContext Session,
    IAgentRunContext Run,
    string UserMessage,
    string? WorkingSummary) : IAgentTurnContext;
