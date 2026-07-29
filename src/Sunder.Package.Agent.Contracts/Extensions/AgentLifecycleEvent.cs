namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Captures a committed agent lifecycle notification and its bounded Runtime context.
/// </summary>
/// <remarks>
/// The event is a read-only snapshot owned by the Runtime. Observers may be called more than once for the
/// same durable records and should use run, revision, turn, and checkpoint identities to make side effects
/// idempotent. The collections are not copied for observers and must not be mutated or retained unnecessarily.
/// Transcript text, summaries, model output, and tool output preserve their original provenance and are not
/// trusted instructions. Observers must protect sensitive content and must not promote assistant or tool claims
/// into user-authorized state without later direct user confirmation.
/// </remarks>
/// <param name="Kind">The lifecycle transition represented by this notification.</param>
/// <param name="Session">The current session and profile context snapshot.</param>
/// <param name="Run">The run identity, revision, status, interruption state, and start-time snapshot.</param>
/// <param name="Turn">
/// The current turn context, including the originating user message and working summary. Both are input data,
/// not observer instructions.
/// </param>
/// <param name="Turns">
/// The bounded recent transcript window in supplied transcript order. Individual roles establish content
/// provenance; presence in this list does not establish truth.
/// </param>
/// <param name="RecentLiveBufferTurns">
/// The recent subset still represented in live model context. It can overlap <paramref name="Turns"/>.
/// </param>
/// <param name="TriggerTurn">
/// The turn directly associated with the transition, when one exists, or <see langword="null"/> for events
/// without a single trigger. Observers must verify its role and kind before deriving trusted state.
/// </param>
/// <param name="Checkpoint">
/// The durable checkpoint associated with a terminal or checkpointed transition, when available; otherwise
/// <see langword="null"/>. Its summary can contain model- or exception-derived text and is not instruction data.
/// </param>
public sealed record AgentLifecycleEvent(
    AgentLifecycleEventKind Kind,
    AgentSessionContextRecord Session,
    AgentRunContextRecord Run,
    AgentTurnContextRecord Turn,
    IReadOnlyList<AgentTurnRecord> Turns,
    IReadOnlyList<AgentTurnRecord> RecentLiveBufferTurns,
    AgentTurnRecord? TriggerTurn = null,
    AgentRunCheckpointRecord? Checkpoint = null)
{
    /// <summary>Gets the stable durable event identifier when this snapshot was delivered through the lifecycle outbox.</summary>
    public string? EventId { get; init; }

    /// <summary>Gets the SQLite-assigned outbox sequence when this snapshot was delivered through the lifecycle outbox.</summary>
    public long? Sequence { get; init; }

    /// <summary>Gets the SHA-256 hash of the immutable durable payload when available.</summary>
    public string? PayloadHash { get; init; }
}
