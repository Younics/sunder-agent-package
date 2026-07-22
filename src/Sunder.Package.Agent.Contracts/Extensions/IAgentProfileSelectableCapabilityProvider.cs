using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Supplies capabilities that a user can assign to an agent profile.
/// </summary>
/// <remarks>
/// <para>
/// Role and direction: this is a Runtime-role extension contract. Zero or more packages contribute
/// providers through <see cref="PackageExtensionPoints.ProfileSelectableCapabilityProviders"/>, and
/// the Agent Runtime consumes their descriptors for profile and subagent editors.
/// </para>
/// <para>
/// Identity, ordering, and deduplication: <see cref="ProviderId"/> identifies the provider and should be
/// used as descriptor source identity. Providers are invoked sequentially by <see cref="DisplayName"/>,
/// using ordinal case-insensitive ordering. The Runtime accepts descriptors with non-empty kind,
/// capability identifier, and display name, deduplicates case-insensitively by (kind, source identifier,
/// capability identifier) with the first descriptor winning, and finally orders descriptors by display
/// name. Provider registrations themselves are not deduplicated.
/// </para>
/// <para>
/// Ownership and threading: the host owns each activation-scoped provider. Requests and returned lists
/// are snapshots and must not be mutated after return. Listing can occur repeatedly and concurrently
/// with runs or change notifications, so providers must be thread-safe and must not assume UI affinity.
/// </para>
/// <para>
/// Cancellation and failure isolation: providers must observe the supplied token. Listing is sequential;
/// cancellation and provider exceptions propagate and can fail the aggregate capability query. Providers
/// should keep discovery bounded and use <see cref="IAgentProfileSelectableCapabilityChangeNotifier"/>
/// when cached availability changes.
/// </para>
/// <para>
/// Trust, provenance, and security: owning-package provenance comes from the catalog, not from
/// <see cref="ProviderId"/> or a descriptor's source strings. Descriptors and status text are display
/// metadata, not authorization decisions. Do not include secrets, infer permission from selection, or
/// expose capabilities the active package cannot safely resolve and enforce at use time.
/// </para>
/// </remarks>
public interface IAgentProfileSelectableCapabilityProvider
{
    /// <summary>
    /// Gets the stable, globally package-scoped provider identifier.
    /// </summary>
    /// <remarks>
    /// Use this value as <see cref="AgentProfileSelectableCapabilityDescriptor.SourceId"/> so persisted
    /// assignments remain attributable when other providers publish the same kind and capability id.
    /// </remarks>
    string ProviderId { get; }

    /// <summary>
    /// Gets the human-readable provider name used for diagnostics and invocation ordering.
    /// </summary>
    /// <remarks>The name is not identity and must not contain sensitive data.</remarks>
    string DisplayName { get; }

    /// <summary>
    /// Lists capabilities applicable to the requested profile context.
    /// </summary>
    /// <param name="request">
    /// The read-only profile selection context. A <see langword="null"/> profile requests a general
    /// catalog rather than capabilities tailored to one persisted profile.
    /// </param>
    /// <param name="cancellationToken">A token that requests cancellation of capability discovery.</param>
    /// <returns>A read-only snapshot containing zero or more selectable capability descriptors.</returns>
    ValueTask<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListCapabilitiesAsync(
        AgentProfileSelectableCapabilityRequest request,
        CancellationToken cancellationToken = default);
}
