using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Contributes target-specific sections to the workspace editor while leaving canonical workspace fields under Agent ownership.
/// </summary>
/// <remarks>
/// App-side implementations should be presentation proxies only; authoritative loading, validation, and persistence of execution configuration
/// belong in Runtime implementations. Editor field values and context identifiers can cross the App/Runtime boundary and must be treated as
/// untrusted input. Contributors must not expose secrets in section values or messages, must not require UI-thread affinity, and must tolerate
/// overlapping discovery and save calls or serialize access to their own state. The App isolates contributor discovery, refresh, and save
/// failures: one contributor no longer aborts aggregate discovery or hides healthy sections. A retry can reacquire only the exact owner
/// activation that produced the failed section.
/// </remarks>
public interface IAgentWorkspaceEditorContributor
{
    /// <summary>
    /// Gets a stable, opaque identity for diagnostics and contribution disambiguation.
    /// </summary>
    /// <remarks>This is not a display label and should remain stable across package upgrades.</remarks>
    string ContributorId { get; }

    /// <summary>
    /// Determines synchronously whether this contributor owns settings for the selected target.
    /// </summary>
    /// <param name="context">The workspace, selected target identity, and binding-scoped configuration key.</param>
    /// <returns><see langword="true"/> when the contributor can provide editor sections for this context.</returns>
    /// <remarks>This check should be fast, side-effect free, and must not perform blocking Runtime or storage I/O.</remarks>
    bool CanEdit(AgentWorkspaceEditorContext context);

    /// <summary>
    /// Loads the current editor schema and non-secret values owned by this contributor.
    /// </summary>
    /// <param name="context">The workspace and binding-scoped target configuration to present.</param>
    /// <param name="cancellationToken">Signals that Runtime communication, catalog refresh, or storage reads should be canceled.</param>
    /// <returns>
    /// A snapshot of sections whose section and field identities are stable enough to be returned unchanged in a later save request.
    /// </returns>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(
        AgentWorkspaceEditorContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and persists one contributor-owned editor section in the authoritative Runtime configuration store.
    /// </summary>
    /// <param name="context">The workspace and binding-scoped target configuration being updated.</param>
    /// <param name="request">
    /// Untrusted field values for one section. Implementations must reject unknown sections, invalid options, unsafe paths, and unavailable resources.
    /// </param>
    /// <param name="cancellationToken">Signals that validation or persistence should be canceled.</param>
    /// <returns>
    /// A user-facing success or validation-failure result. Expected validation failures should be returned rather than thrown; transport or
    /// storage failures may surface as exceptions.
    /// </returns>
    /// <exception cref="OperationCanceledException">The operation was canceled; persistence may already have completed.</exception>
    ValueTask<AgentEditorSaveResult> SaveSectionAsync(
        AgentWorkspaceEditorContext context,
        AgentEditorSaveRequest request,
        CancellationToken cancellationToken = default);
}
