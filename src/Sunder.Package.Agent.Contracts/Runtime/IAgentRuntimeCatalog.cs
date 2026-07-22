using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Exposes persisted Agent catalog snapshots and invalidation notifications to runtime extensions.
/// </summary>
/// <remarks>
/// Returned records and collections are point-in-time values, not live objects; callers must treat collections as
/// immutable and re-query after a notification. A missing entity is represented by <see langword="null"/> or an empty
/// collection. Persistence and deserialization failures may still propagate and must not be interpreted as absence.
/// </remarks>
public interface IAgentRuntimeCatalog
{
    /// <summary>Occurs after a session or its persisted transcript/checkpoint projection changes; the argument is the session identifier to re-query.</summary>
    event Action<Guid>? SessionChanged;

    /// <summary>Occurs after a turn is persisted; arguments are the owning session identifier and the newest turn snapshot.</summary>
    event Action<Guid, AgentTurnRecord>? TurnChanged;

    /// <summary>Occurs after a profile changes; the argument is the opaque profile identifier to re-query.</summary>
    event Action<string>? ProfileChanged;

    /// <summary>Lists persisted session snapshots, ordered by most recent update in the base host.</summary>
    /// <returns>An immutable-by-contract snapshot collection.</returns>
    IReadOnlyList<AgentSessionRecord> ListSessions();

    /// <summary>Lists sessions currently associated with a profile.</summary>
    /// <param name="profileId">The opaque profile identifier; blank input returns an empty collection in the base host.</param>
    /// <returns>Matching snapshots in the catalog's session order.</returns>
    IReadOnlyList<AgentSessionRecord> ListSessionsForProfile(string profileId);

    /// <summary>Lists sessions assigned to a workspace.</summary>
    /// <param name="workspaceId">The opaque workspace identifier; blank input returns an empty collection in the base host.</param>
    /// <returns>Matching snapshots in the catalog's session order.</returns>
    IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(string workspaceId);

    /// <summary>Gets a persisted session snapshot.</summary>
    /// <param name="sessionId">The stable session identifier.</param>
    /// <returns>The snapshot, or <see langword="null"/> when no session has that identifier.</returns>
    AgentSessionRecord? GetSession(Guid sessionId);

    /// <summary>Lists hydrated workspace snapshots, including configured paths and documents.</summary>
    /// <returns>An immutable-by-contract snapshot collection.</returns>
    IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces();

    /// <summary>Gets a hydrated workspace snapshot.</summary>
    /// <param name="workspaceId">The opaque workspace identifier.</param>
    /// <returns>The snapshot, or <see langword="null"/> when no workspace has that identifier.</returns>
    AgentWorkspaceRecord? GetWorkspace(string workspaceId);

    /// <summary>Resolves the profile currently referenced by a session.</summary>
    /// <param name="sessionId">The stable session identifier.</param>
    /// <returns>The profile snapshot, or <see langword="null"/> when the session, profile reference, or profile is absent.</returns>
    AgentProfileRecord? GetSessionProfile(Guid sessionId);

    /// <summary>Gets the session's legacy working-summary snapshot.</summary>
    /// <param name="sessionId">The stable session identifier.</param>
    /// <returns>The summary, or <see langword="null"/> when none is persisted.</returns>
    AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId);

    /// <summary>Gets the newest durable context checkpoint for omitted transcript history.</summary>
    /// <param name="sessionId">The stable session identifier.</param>
    /// <returns>The newest context checkpoint, or <see langword="null"/> when none is persisted.</returns>
    AgentSessionContextCheckpointRecord? GetLatestSessionContextCheckpoint(Guid sessionId);

    /// <summary>Gets the newest checkpoint from the highest persisted run revision for a session.</summary>
    /// <param name="sessionId">The stable session identifier.</param>
    /// <returns>The checkpoint, or <see langword="null"/> when the session has no checkpoint.</returns>
    AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId);

    /// <summary>Lists up to a bounded number of the newest turns, returned in ascending transcript order.</summary>
    /// <param name="sessionId">The stable session identifier.</param>
    /// <param name="limit">The maximum number of turns; the base host treats non-positive values as zero.</param>
    /// <returns>Detached turn snapshots whose item collections must be treated as immutable.</returns>
    IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit);

    /// <summary>Pages backward from an exclusive composite transcript boundary.</summary>
    /// <param name="sessionId">The stable session identifier.</param>
    /// <param name="beforeCreatedAtUtc">The creation-time component of the exclusive boundary.</param>
    /// <param name="beforeTurnId">The turn-identifier tie-breaker for equal creation times.</param>
    /// <param name="limit">The maximum number of turns; the base host treats non-positive values as zero.</param>
    /// <returns>Earlier turn snapshots in ascending transcript order.</returns>
    IReadOnlyList<AgentTurnRecord> ListTurnsBefore(Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit);

    /// <summary>Pages forward from an exclusive composite transcript boundary.</summary>
    /// <param name="sessionId">The stable session identifier.</param>
    /// <param name="afterCreatedAtUtc">The creation-time component of the exclusive boundary.</param>
    /// <param name="afterTurnId">The turn-identifier tie-breaker for equal creation times.</param>
    /// <param name="limit">The maximum number of turns; the base host treats non-positive values as zero.</param>
    /// <returns>Later turn snapshots in ascending transcript order.</returns>
    IReadOnlyList<AgentTurnRecord> ListTurnsAfter(Guid sessionId, DateTimeOffset afterCreatedAtUtc, Guid afterTurnId, int limit);

    /// <summary>Lists user-visible persisted profile snapshots.</summary>
    /// <returns>An immutable-by-contract snapshot collection; runtime-managed internal profiles may be omitted.</returns>
    IReadOnlyList<AgentProfileRecord> ListProfiles();

    /// <summary>Gets a profile snapshot by opaque identifier.</summary>
    /// <param name="profileId">The profile identifier.</param>
    /// <returns>The snapshot, or <see langword="null"/> when no profile has that identifier.</returns>
    AgentProfileRecord? GetProfile(string profileId);

    /// <summary>Resolves one capability-specific model binding through a session's current profile.</summary>
    /// <param name="sessionId">The stable session identifier.</param>
    /// <param name="capabilityKind">The extension-defined capability key.</param>
    /// <returns>The binding snapshot, or <see langword="null"/> when the session, profile, or binding is absent.</returns>
    AgentProfileModelBindingRecord? GetSessionModelBinding(Guid sessionId, string capabilityKind);

    /// <summary>Gets one capability-specific model binding from a profile.</summary>
    /// <param name="profileId">The opaque profile identifier.</param>
    /// <param name="capabilityKind">The extension-defined capability key.</param>
    /// <returns>The binding snapshot, or <see langword="null"/> when the profile or binding is absent.</returns>
    AgentProfileModelBindingRecord? GetModelBinding(string profileId, string capabilityKind);
}
