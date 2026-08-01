using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Contributes trusted package instructions to the privileged system prompt.
/// </summary>
/// <remarks>
/// <para>
/// Role and direction: this is a Runtime-role RPC adapter contract. Zero or more active packages
/// provide the <c>sunder.agent.system.prompt.contributor</c> contract, and the Agent Runtime consumes all active
/// providers when composing each model request.
/// </para>
/// <para>
/// Identity, ordering, and deduplication: the catalog does not deduplicate contributors by
/// <see cref="ContributorId"/>. Contributors are invoked sequentially by <see cref="DisplayName"/>,
/// using ordinal case-insensitive ordering. Blocks are deduplicated case-insensitively by the pair
/// (<see cref="AgentSystemPromptBlock.SourceId"/>, <see cref="AgentSystemPromptBlock.BlockId"/>), then
/// ordered by required status, descending priority, title, source, and block identifier.
/// </para>
/// <para>
/// Ownership and threading: the host owns each activation-scoped instance; consumers must not dispose
/// or retain it after catalog removal. Request and result objects are call snapshots and returned
/// collections must not be changed after completion. A contributor can be called concurrently for
/// independent runs and must not assume a UI thread or other thread affinity.
/// </para>
/// <para>
/// Cancellation and failure isolation: implementations must observe the supplied token. The Runtime
/// propagates <see cref="OperationCanceledException"/> and suppresses other contributor exceptions so
/// an optional instruction source cannot stop the base chat flow.
/// </para>
/// <para>
/// Trust, provenance, and security: this contract is only for policy and capability instructions
/// authored or controlled by the installed package. Package ownership supplies code provenance, but
/// values in the request do not become trusted merely because trusted code can read them. Never copy,
/// summarize, or interpolate user messages, transcript text, recalled memory, tool output, workspace
/// file content, remote content, or model output into a privileged block. Put such data in an
/// <see cref="IAgentPromptContextContributor"/> instead. Do not include credentials or use system
/// instructions to bypass permission, workspace, execution-target, or user-consent boundaries.
/// </para>
/// </remarks>
public interface IAgentSystemPromptContributor
{
    /// <summary>
    /// Gets the stable, non-empty identifier for this contributor.
    /// </summary>
    /// <remarks>
    /// The identifier should be globally scoped to the package and should normally be copied to each
    /// block's <see cref="AgentSystemPromptBlock.SourceId"/>. The Runtime does not use it to suppress
    /// duplicate contributor registrations.
    /// </remarks>
    string ContributorId { get; }

    /// <summary>
    /// Gets the human-readable name used for diagnostics and contributor invocation ordering.
    /// </summary>
    /// <remarks>The display name is not an identity and must not contain sensitive data.</remarks>
    string DisplayName { get; }

    /// <summary>
    /// Builds trusted, package-controlled system-prompt blocks for the current request.
    /// </summary>
    /// <param name="request">
    /// The model, workspace, tool, transcript, and run snapshot. User- and environment-derived fields
    /// remain untrusted data and must not be elevated into returned instructions.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that requests cancellation of contribution work.
    /// </param>
    /// <returns>
    /// A read-only list of trusted blocks. Return an empty list when no instruction applies; do not
    /// return <see langword="null"/>.
    /// </returns>
    ValueTask<IReadOnlyList<AgentSystemPromptBlock>> ContributeAsync(
        AgentSystemPromptRequest request,
        CancellationToken cancellationToken = default);
}
