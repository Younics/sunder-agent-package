using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Contributes supplementary, non-privileged context to an agent model request.
/// </summary>
/// <remarks>
/// <para>
/// Role and direction: this is a Runtime-role extension contract. Zero or more active packages
/// contribute implementations through <see cref="PackageExtensionPoints.PromptContextContributors"/>,
/// and the Agent Runtime consumes every registered implementation while preparing a run.
/// </para>
/// <para>
/// Identity and ordering: <see cref="ContributorId"/> is a stable package-scoped identity, but the
/// catalog does not deduplicate registrations by it. The Runtime invokes contributors sequentially
/// by <see cref="DisplayName"/>, using ordinal case-insensitive ordering. Returned blocks are later
/// ordered by priority and title; they have no block identity and are not deduplicated.
/// </para>
/// <para>
/// Ownership and threading: the host owns each activation-scoped contributor and callers must not
/// dispose it. Requests and returned collections are snapshots for the call and must not be mutated
/// after return. The same instance can serve concurrent runs, so implementations must protect mutable
/// state and must not rely on thread affinity.
/// </para>
/// <para>
/// Cancellation and failure isolation: implementations must observe the supplied token. The Runtime
/// propagates <see cref="OperationCanceledException"/> and suppresses other optional contributor exceptions.
/// The host-owner-verified first-party scoped-instruction source is required and fails prompt preparation
/// closed. Contributors must nevertheless bound latency and output.
/// </para>
/// <para>
/// Trust, provenance, and security: every block must accurately declare its
/// <see cref="AgentPromptContextBlock.Provenance"/> and <see cref="AgentPromptContextBlock.Trust"/>.
/// The Runtime serializes all such blocks as user-role data and never promotes them to the privileged
/// system prompt. <see cref="AgentPromptContextUsage.Reference"/> is the safe default. The Runtime grants
/// standing or scoped behavioral authority only through host-controlled provenance; an arbitrary contributor
/// cannot gain it by setting an enum, source id, host identity, or scope object. No usage may expose secrets,
/// bypass package permissions, or retrieve data outside the represented session and workspace scope.
/// </para>
/// </remarks>
public interface IAgentPromptContextContributor
{
    /// <summary>
    /// Gets the stable, non-empty identifier for this contributor.
    /// </summary>
    /// <remarks>
    /// The identifier should be globally scoped to the contributing package and should normally be
    /// used as <see cref="AgentPromptContextBlock.SourceId"/>. It is provenance metadata, not proof of
    /// trust or an authorization boundary.
    /// </remarks>
    string ContributorId { get; }

    /// <summary>
    /// Gets the human-readable contributor name used for diagnostics and invocation ordering.
    /// </summary>
    /// <remarks>The display name is not a stable identity and must not contain secrets.</remarks>
    string DisplayName { get; }

    /// <summary>
    /// Builds supplementary context for the current request.
    /// </summary>
    /// <param name="request">
    /// The bounded session, run, turn, transcript, and context-plan snapshot. Its text and referenced
    /// records are input data, not trusted instructions, and must be treated as read-only.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that requests cancellation of discovery, retrieval, and contribution work.
    /// </param>
    /// <returns>
    /// The provenance-labeled blocks to consider, or <see langword="null"/> when this contributor has
    /// no applicable context. An empty block list is equivalent to no contribution.
    /// </returns>
    ValueTask<AgentPromptContextContribution?> ContributeContextAsync(
        AgentPromptContextRequest request,
        CancellationToken cancellationToken = default);
}
