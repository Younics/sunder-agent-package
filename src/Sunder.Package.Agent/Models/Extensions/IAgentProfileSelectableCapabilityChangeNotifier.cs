namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Optionally notifies consumers that a selectable-capability provider's current catalog is stale.
/// </summary>
/// <remarks>
/// <para>
/// Role and direction: this Runtime-role adjunct is implemented by an
/// <see cref="IAgentProfileSelectableCapabilityProvider"/> that contributes invalidation signals; Agent
/// Runtime consumers subscribe and then call <see cref="IAgentProfileSelectableCapabilityProvider.ListCapabilitiesAsync"/>
/// to consume a fresh snapshot.
/// </para>
/// <para>
/// Cardinality, identity, and ordering: each provider instance exposes one multicast event with zero or
/// more subscribers. Subscriptions are tracked by provider object identity. Notifications carry no
/// provider id, revision, ordering, or delta, and are neither deduplicated nor coalesced; one signal means
/// only that consumers should refresh.
/// </para>
/// <para>
/// Ownership, lifetime, and threading: the host owns the provider. Subscribers own their event
/// subscriptions and must remove them before their own disposal or when the provider leaves the catalog.
/// The provider may raise the event on any thread, and handlers run synchronously on that thread, so
/// publishers must not hold locks required by capability discovery and consumers must marshal UI work.
/// </para>
/// <para>
/// Cancellation and failure isolation: events have no cancellation channel and no intrinsic subscriber
/// failure isolation. A subscriber exception can escape the event raise, so providers should make state
/// changes durable before notification and must not depend on every handler completing.
/// </para>
/// <para>
/// Trust, provenance, and security: the event contains no data and conveys no authorization or trust
/// change. Consumers must re-query descriptors and reapply normal package provenance, validation, and
/// permission checks rather than trusting previously cached values.
/// </para>
/// </remarks>
public interface IAgentProfileSelectableCapabilityChangeNotifier
{
    /// <summary>
    /// Occurs after a change that can affect the provider's selectable capability descriptors.
    /// </summary>
    /// <remarks>
    /// The notification is synchronous, can occur on any thread, and may be raised more than once for a
    /// logical change. Subscribers should return promptly and perform a full refresh.
    /// </remarks>
    event Action? SelectableCapabilitiesChanged;
}
