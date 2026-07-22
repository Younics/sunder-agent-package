namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Records one append-only durable lifecycle checkpoint for a session run revision.
/// </summary>
/// <remarks>
/// Multiple checkpoints can exist for the same run revision. A checkpoint is an observation of persisted state, not
/// a command or mutable run handle. Terminal checkpoints also close any streaming assistant turns owned by that run.
/// </remarks>
/// <param name="CheckpointId">The unique identifier of this persisted transition record.</param>
/// <param name="SessionId">The session that owns the run.</param>
/// <param name="RunRevision">The monotonically increasing per-session run generation; it is not a turn revision or internal lease epoch.</param>
/// <param name="Status">The lifecycle state persisted by this transition.</param>
/// <param name="Summary">Optional human-readable progress or failure detail. Consumers must not parse it as a stable error code.</param>
/// <param name="CreatedAtUtc">The UTC time at which the transition committed.</param>
public sealed record AgentRunCheckpointRecord(
    Guid CheckpointId,
    Guid SessionId,
    long RunRevision,
    AgentRunStatus Status,
    string? Summary,
    DateTimeOffset CreatedAtUtc);
