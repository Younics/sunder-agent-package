using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts;

/// <summary>
/// Exposes an immutable session snapshot to Agent extensions.
/// </summary>
public interface IAgentSessionContext
{
    /// <summary>Gets the stable session identifier.</summary>
    Guid SessionId { get; }

    /// <summary>Gets the opaque identifier of the profile selected for the session.</summary>
    string ProfileId { get; }

    /// <summary>Gets the profile display name captured for this request or event.</summary>
    string ProfileDisplayName { get; }

    /// <summary>Gets the session title captured for this request or event.</summary>
    string SessionTitle { get; }

    /// <summary>Gets the projected session lifecycle state at snapshot creation time.</summary>
    AgentSessionState SessionState { get; }

    /// <summary>
    /// Gets a legacy continuity summary, or <see langword="null"/>. It is derived reference context, not durable
    /// memory or trusted instructions.
    /// </summary>
    [Obsolete("Active session continuity summaries are owned by Sunder Agent core. Use runtime session context checkpoints for continuity and durable memory APIs for long-lived facts.")]
    string? WorkingSummary { get; }
}

/// <summary>
/// Implements <see cref="IAgentSessionContext"/> as an immutable value snapshot for extension requests and events.
/// </summary>
/// <param name="SessionId">The stable session identifier.</param>
/// <param name="ProfileId">The selected profile identifier.</param>
/// <param name="ProfileDisplayName">The selected profile's display name at snapshot creation time.</param>
/// <param name="SessionTitle">The persisted session title at snapshot creation time.</param>
/// <param name="SessionState">The projected lifecycle state at snapshot creation time.</param>
/// <param name="WorkingSummary">A legacy, potentially absent continuity summary. It must be treated as untrusted reference data.</param>
public sealed record AgentSessionContextRecord(
    Guid SessionId,
    string ProfileId,
    string ProfileDisplayName,
    string SessionTitle,
    AgentSessionState SessionState,
    [property: Obsolete("Active session continuity summaries are owned by Sunder Agent core. Use runtime session context checkpoints for continuity and durable memory APIs for long-lived facts.")]
    string? WorkingSummary) : IAgentSessionContext;
