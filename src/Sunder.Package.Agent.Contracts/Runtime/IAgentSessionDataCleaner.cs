namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Deletes extension-owned data after the Agent host durably removes a session or rolled-back child session.
/// </summary>
/// <remarks>
/// Cleaners should be idempotent and scope all deletion to the supplied session. The callback is synchronous and has
/// no cancellation token, so implementations should keep work bounded. The base host records an ids-only cleanup job
/// in the same transaction as core deletion, then invokes the exact package-owned contribution under an activation
/// lease. Failed or unavailable jobs remain durable and retry after delay, Runtime restart, or package reactivation.
/// </remarks>
public interface IAgentSessionDataCleaner
{
    /// <summary>Gets a stable, diagnostic identifier for this cleanup contribution.</summary>
    string CleanerId { get; }

    /// <summary>Deletes all extension-owned state associated with a session, tolerating already-absent data.</summary>
    /// <param name="sessionId">The stable identifier of the session whose core data has been removed.</param>
    /// <exception cref="Exception">An implementation failure leaves the durable cleanup job pending for retry.</exception>
    void DeleteSessionData(Guid sessionId);
}
