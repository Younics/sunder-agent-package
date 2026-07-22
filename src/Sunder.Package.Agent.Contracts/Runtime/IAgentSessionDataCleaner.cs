namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Deletes extension-owned data after the Agent host durably removes a session or rolled-back child session.
/// </summary>
/// <remarks>
/// Cleaners should be idempotent and scope all deletion to the supplied session. The callback is synchronous and has
/// no cancellation token, so implementations should keep work bounded. The base host invokes cleaners after its core
/// persistence transaction; it aggregates cleaner exceptions for the caller without restoring already-deleted data.
/// </remarks>
public interface IAgentSessionDataCleaner
{
    /// <summary>Gets a stable, diagnostic identifier for this cleanup contribution.</summary>
    string CleanerId { get; }

    /// <summary>Deletes all extension-owned state associated with a session, tolerating already-absent data.</summary>
    /// <param name="sessionId">The stable identifier of the session whose core data has been removed.</param>
    /// <exception cref="Exception">An implementation failure is collected by the host and may be surfaced in an <see cref="AggregateException"/> after deletion commits.</exception>
    void DeleteSessionData(Guid sessionId);
}
