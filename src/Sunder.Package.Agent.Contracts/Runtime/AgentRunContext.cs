using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts;

/// <summary>
/// Exposes the immutable identity and lifecycle snapshot of one Agent run generation.
/// </summary>
public interface IAgentRunContext
{
    /// <summary>Gets the globally unique durable-run identifier.</summary>
    Guid RunId { get; }

    /// <summary>
    /// Gets the monotonically increasing run generation within the session. It remains stable across lifecycle
    /// transitions and is distinct from the host's internal lease epoch.
    /// </summary>
    long Revision { get; }

    /// <summary>Gets the run status represented by this context snapshot.</summary>
    AgentRunStatus Status { get; }

    /// <summary>Gets whether the surrounding lifecycle event represents interrupted execution.</summary>
    bool IsInterrupted { get; }

    /// <summary>Gets the UTC time at which this run generation started.</summary>
    DateTimeOffset StartedAtUtc { get; }
}

/// <summary>
/// Implements <see cref="IAgentRunContext"/> as an immutable value snapshot for extension requests and events.
/// </summary>
/// <param name="RunId">The globally unique durable-run identifier.</param>
/// <param name="Revision">The monotonically increasing run generation within its session.</param>
/// <param name="Status">The lifecycle status at snapshot creation time.</param>
/// <param name="IsInterrupted">Whether the associated operation or lifecycle event was interrupted.</param>
/// <param name="StartedAtUtc">The UTC time at which the run generation started.</param>
public sealed record AgentRunContextRecord(
    Guid RunId,
    long Revision,
    AgentRunStatus Status,
    bool IsInterrupted,
    DateTimeOffset StartedAtUtc) : IAgentRunContext;
