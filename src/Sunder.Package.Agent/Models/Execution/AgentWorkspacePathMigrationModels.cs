namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Identifies a workspace and its selected execution binding while Runtime migrates legacy, contributor-owned path configuration.
/// </summary>
/// <remarks>
/// The Agent Runtime owns the canonical workspace path records and invokes migration contributors only when it needs legacy candidates.
/// Contributors receive snapshots and must not attempt to persist replacements directly through this context.
/// </remarks>
/// <param name="Workspace">The workspace being migrated; its canonical path collection is normally empty when migration begins.</param>
/// <param name="Binding">The enabled execution binding whose legacy configuration may contain host paths.</param>
public sealed record AgentWorkspacePathMigrationContext(
    AgentWorkspaceRecord Workspace,
    AgentWorkspaceBindingRecord Binding);

/// <summary>
/// Describes one host path recovered from legacy execution-binding configuration for Runtime-owned persistence.
/// </summary>
/// <remarks>
/// Values are migration input, not an authorization decision. Runtime normalizes, deduplicates, orders, and assigns identities to the final
/// workspace path records. Contributors should return only paths from configuration they own and must not translate them into container or
/// other execution-target path syntax.
/// </remarks>
/// <param name="HostPath">The recovered host filesystem path, not a target-visible mount path.</param>
/// <param name="IsDefault">
/// Whether this path contains the legacy default working directory. If several items are marked, Runtime selects the first ordered candidate.
/// </param>
/// <param name="SortOrder">The relative legacy order; lower values are considered first before Runtime assigns contiguous final ordering.</param>
public sealed record AgentWorkspacePathMigrationItem(
    string HostPath,
    bool IsDefault = false,
    int SortOrder = 0);
