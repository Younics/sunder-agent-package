namespace Sunder.Package.Agent.Contracts;

/// <summary>
/// Exposes the immutable context snapshot associated with the active user turn.
/// </summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public interface IAgentTurnContext
{
    /// <summary>Gets the owning session snapshot.</summary>
    IAgentSessionContext Session { get; }

    /// <summary>Gets the run generation processing this turn.</summary>
    IAgentRunContext Run { get; }

    /// <summary>Gets the current user-authored message.</summary>
    string UserMessage { get; }

}

/// <summary>
/// Implements <see cref="IAgentTurnContext"/> as an immutable value snapshot for extension requests and events.
/// </summary>
/// <param name="Session">The owning session context.</param>
/// <param name="Run">The run generation processing the turn.</param>
/// <param name="UserMessage">The current user-authored message.</param>
public sealed record AgentTurnContextRecord(
    AgentSessionContextRecord Session,
    AgentRunContextRecord Run,
    string UserMessage) : IAgentTurnContext
{
    IAgentSessionContext IAgentTurnContext.Session => Session;
    IAgentRunContext IAgentTurnContext.Run => Run;
}
