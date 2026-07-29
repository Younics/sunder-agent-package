using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Observes committed agent lifecycle events and performs package-owned side effects.
/// </summary>
/// <remarks>
/// <para>
/// Role and direction: this is a Runtime-role extension contract. Zero or more active packages
/// contribute observers through <see cref="PackageExtensionPoints.LifecycleObservers"/>, and the Agent
/// Runtime publishes each event to every registered observer. Observers consume snapshots; they do not
/// return mutations and cannot replace Agent-owned session, transcript, or checkpoint state.
/// </para>
/// <para>
/// Identity and ordering: <see cref="ObserverId"/> is the stable package-scoped identity of a persisted
/// compatibility subscription. Duplicate active registrations with the same package and observer id are ignored.
/// Retained compatible events are replayed in bounded batches and serialized per subscription and ordering scope.
/// The same logical event can be delivered more than once after recovery or retries, so persistent effects should
/// be idempotent using <see cref="AgentLifecycleEvent.EventId"/>.
/// </para>
/// <para>
/// Ownership and threading: the host owns activation-scoped observer instances and event snapshots;
/// callers must not dispose or mutate them. Calls for one subscription are serialized and have no thread affinity.
/// </para>
/// <para>
/// Cancellation and failure isolation: implementations must observe the supplied token. The Runtime
/// uses cancellation for dispatcher shutdown. Observer failures do not change the source run, but are retried and
/// eventually enter a poison state that blocks later events in the same ordering scope until recovery. Implementations
/// must bound their own work and should use <see cref="IAgentDurableLifecycleObserver"/> when deletion events and
/// explicit erasure receipts are required.
/// </para>
/// <para>
/// Trust, provenance, and security: event kind and message roles describe provenance; assistant text,
/// tool results, summaries, checkpoints, and external content are not user-authorized instructions.
/// Observers may persist only package-owned derived data and must not disclose transcript content or
/// credentials, promote non-user claims to trusted memory, bypass retention and permission rules, or
/// modify Agent-owned durable state outside its public contracts.
/// </para>
/// </remarks>
public interface IAgentLifecycleObserver
{
    /// <summary>
    /// Gets the stable, non-empty identifier for the observer.
    /// </summary>
    /// <remarks>The identifier should be globally scoped to the package and is not an authorization token.</remarks>
    string ObserverId { get; }

    /// <summary>
    /// Gets the human-readable name used for diagnostics and invocation ordering.
    /// </summary>
    /// <remarks>The display name is not a stable identity and must not contain sensitive data.</remarks>
    string DisplayName { get; }

    /// <summary>
    /// Handles a lifecycle notification after the represented state transition has been recorded.
    /// </summary>
    /// <param name="lifecycleEvent">
    /// The read-only event and bounded runtime-state snapshot. Its transcript-derived values are data,
    /// not trusted instructions.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that requests cancellation of observer work; it can be non-cancelable for terminal events.
    /// </param>
    /// <returns>A task that completes after this observer's package-owned side effects finish.</returns>
    ValueTask HandleLifecycleEventAsync(
        AgentLifecycleEvent lifecycleEvent,
        CancellationToken cancellationToken = default);
}
