using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>Consumes ordered, durable Agent lifecycle envelopes with at-least-once delivery.</summary>
/// <remarks>
/// Observer ids are persisted subscription identities and must remain stable across package upgrades. The Runtime
/// incrementally backfills retained payloads from a persisted replay watermark, serializes delivery for a subscription
/// and ordering key, retries bounded transient failures, and can redeliver after lease recovery. Implementations must
/// atomically deduplicate by event id before applying package-owned effects. A content-erasure receipt reuses the event
/// id, carries the original payload hash, contains no source content, and must be acknowledged as a no-op. Shutdown
/// cancellation is not a delivery failure.
/// </remarks>
public interface IAgentDurableLifecycleObserver
{
    /// <summary>Gets the globally stable, non-empty observer subscription identifier.</summary>
    string ObserverId { get; }

    /// <summary>Gets the bounded human-readable name used for diagnostics.</summary>
    string DisplayName { get; }

    /// <summary>Handles one durable source envelope or canonical content-erasure receipt.</summary>
    /// <param name="lifecycleEvent">The event identity, hash, ordering metadata, and bounded source payload.</param>
    /// <param name="cancellationToken">A token requesting prompt cancellation of observer work.</param>
    /// <returns>A task that completes after package-owned effects have committed.</returns>
    ValueTask HandleDurableLifecycleEventAsync(
        AgentDurableLifecycleEventEnvelope lifecycleEvent,
        CancellationToken cancellationToken = default);
}
