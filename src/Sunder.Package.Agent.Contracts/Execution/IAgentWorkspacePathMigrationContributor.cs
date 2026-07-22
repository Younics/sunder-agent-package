using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Migrates contributor-owned legacy workspace paths into canonical Agent Runtime workspace-path records.
/// </summary>
/// <remarks>
/// The Agent Runtime orchestrates migration and owns persistence of the new records. A contributor only reads configuration it owns, returns
/// host-path candidates, and removes or rewrites legacy fields after Runtime confirms persistence. Methods run in Runtime during workspace
/// initialization, not in App presentation code. Implementations should be idempotent because interrupted cleanup can be retried.
/// </remarks>
public interface IAgentWorkspacePathMigrationContributor
{
    /// <summary>
    /// Gets the stable identity of this migration implementation.
    /// </summary>
    /// <remarks>The value is an opaque diagnostic identity, not a workspace, binding, or target identity.</remarks>
    string ContributorId { get; }

    /// <summary>
    /// Determines whether the contributor owns legacy path configuration for a binding.
    /// </summary>
    /// <param name="context">The workspace and enabled execution binding being considered for migration.</param>
    /// <returns><see langword="true"/> when this contributor recognizes and can inspect the binding's legacy configuration.</returns>
    /// <remarks>This check should be fast, side-effect free, and should normally rely on stable binding contribution identity.</remarks>
    bool CanMigrate(AgentWorkspacePathMigrationContext context);

    /// <summary>
    /// Reads legacy configuration without modifying it and returns host paths for Runtime-owned persistence.
    /// </summary>
    /// <param name="context">The workspace and binding whose contributor-owned legacy configuration is read.</param>
    /// <param name="cancellationToken">Signals that storage reads and path translation should be canceled.</param>
    /// <returns>
    /// Legacy host-path candidates, or an empty collection when no valid migration input exists. Paths remain untrusted until Runtime normalizes them.
    /// </returns>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    Task<IReadOnlyList<AgentWorkspacePathMigrationItem>> GetLegacyWorkspacePathsAsync(
        AgentWorkspacePathMigrationContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finalizes contributor-owned legacy configuration after Runtime has persisted the canonical workspace paths.
    /// </summary>
    /// <param name="context">The workspace and binding whose migration was persisted.</param>
    /// <param name="cancellationToken">Signals that legacy cleanup should be canceled.</param>
    /// <returns>A task that completes after cleanup or format rewriting is durable.</returns>
    /// <remarks>
    /// The method must preserve unrelated binding settings and be safe to repeat. Failure does not roll back canonical Runtime path records and
    /// should leave enough legacy state for a later retry.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The cleanup was canceled.</exception>
    Task CompleteWorkspacePathMigrationAsync(
        AgentWorkspacePathMigrationContext context,
        CancellationToken cancellationToken = default);
}
