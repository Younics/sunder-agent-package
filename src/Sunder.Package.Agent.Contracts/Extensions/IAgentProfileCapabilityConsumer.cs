using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Declares profile-level model capabilities consumed by an installed feature.
/// </summary>
/// <remarks>
/// <para>
/// Role and direction: this is a Runtime-role RPC adapter contract. Zero or more packages provide
/// the <c>sunder.agent.profile.capability.consumer</c> contract, and the Agent Runtime consumes their
/// declarations to decide which profile capability selectors are relevant.
/// </para>
/// <para>
/// Identity, cardinality, and ordering: <see cref="ConsumerId"/> identifies the contributor, while each
/// returned descriptor identifies a capability kind. The catalog and Runtime do not deduplicate
/// consumers or descriptors and define no descriptor ordering; current capability checks use
/// case-insensitive kind matching and stop when any declaration matches.
/// </para>
/// <para>
/// Ownership and threading: the host owns activation-scoped consumer instances. Returned descriptors
/// are snapshots and must remain unchanged after return. The synchronous method can be called repeatedly
/// or concurrently and must be fast, thread-safe, and free of UI-thread assumptions.
/// </para>
/// <para>
/// Cancellation and failure isolation: discovery has no cancellation token and no per-consumer failure
/// isolation; an exception can fail the caller's profile query. Implementations should return cached
/// metadata and avoid I/O.
/// </para>
/// <para>
/// Trust, provenance, and security: package ownership is supplied by the RPC catalog, while
/// <see cref="ConsumerId"/> and descriptor strings are self-declared metadata. A declaration neither
/// grants access to a provider nor authorizes use of credentials or profile data. Display text must not
/// contain secrets, and consumers must use the selected capability only through its governing contracts.
/// </para>
/// </remarks>
public interface IAgentProfileCapabilityConsumer
{
    /// <summary>
    /// Gets the stable, package-scoped identity of the consuming feature.
    /// </summary>
    /// <remarks>The value is used as metadata and is not deduplicated or treated as proof of package ownership.</remarks>
    string ConsumerId { get; }

    /// <summary>
    /// Gets the human-readable name of the consuming feature.
    /// </summary>
    /// <remarks>The value is presentation text, not a stable identity, and must not contain secrets.</remarks>
    string DisplayName { get; }

    /// <summary>
    /// Lists the profile capability kinds this feature consumes.
    /// </summary>
    /// <returns>
    /// A read-only snapshot containing zero or more declarations. Duplicate capability kinds have no
    /// additional meaning.
    /// </returns>
    IReadOnlyList<AgentProfileCapabilityConsumerDescriptor> ListConsumedCapabilities();
}
