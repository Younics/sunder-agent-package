namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Identifies an exact durable lifecycle receipt that must be visible before rollback-sensitive memory is returned.
/// </summary>
/// <param name="EventId">The stable lifecycle event identifier.</param>
/// <param name="PayloadHash">The lowercase SHA-256 hash of the immutable event payload.</param>
public sealed record AgentMemoryConsistencyBarrier(string EventId, string PayloadHash);
