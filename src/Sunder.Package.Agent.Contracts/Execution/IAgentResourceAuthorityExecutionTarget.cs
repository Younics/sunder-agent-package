using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Optionally validates that transient resource authority required by an invocation is still current.
/// </summary>
public interface IAgentResourceAuthorityExecutionTarget
{
    /// <summary>
    /// Validates invocation-bound authority without redeeming it or accessing the claimed target.
    /// </summary>
    ValueTask<AgentResourceAuthorityValidation> ValidateResourceAuthorityAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Releases any unredeemed transient capabilities after the governed operation ends.</summary>
    /// <remarks>Implementations must be idempotent, non-blocking, and must not throw.</remarks>
    void ReleaseResourceAuthority(IReadOnlyList<string> resourceCapabilities);
}

/// <summary>Reports whether execution may proceed to its authoritative resource checkout.</summary>
public sealed record AgentResourceAuthorityValidation(
    bool IsValid,
    string? ErrorCode = null,
    string? ErrorMessage = null);
