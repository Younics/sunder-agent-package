namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Maintains a bounded-by-policy, mutable working set of immutable turn snapshots for transcript presentation.
/// </summary>
/// <remarks>
/// Adding a turn does not trim automatically; call <see cref="Trim"/> after a batch so the caller can choose which
/// edge to discard. Ordering is deterministic by creation time and then turn identifier. This utility owns its
/// dictionary but not the immutable turn records supplied to it.
/// </remarks>
/// <param name="turnLimit">The maximum number of turns to retain when trimming. Use a non-negative value; zero retains no turns and the constructor performs no validation.</param>
public sealed class AgentTranscriptTurnWindow(int turnLimit)
{
    private readonly Dictionary<Guid, AgentTurnRecord> _turnsById = new();

    /// <summary>Gets the number of distinct turn identifiers currently held, including any untrimmed overflow.</summary>
    public int Count => _turnsById.Count;

    /// <summary>Gets the creation time at the oldest ordered boundary, or <see langword="null"/> when empty.</summary>
    public DateTimeOffset? OldestCreatedAtUtc { get; private set; }

    /// <summary>Gets the turn identifier that completes the oldest composite boundary, or <see langword="null"/> when empty.</summary>
    public Guid? OldestTurnId { get; private set; }

    /// <summary>Gets the creation time at the newest ordered boundary, or <see langword="null"/> when empty.</summary>
    public DateTimeOffset? NewestCreatedAtUtc { get; private set; }

    /// <summary>Gets the turn identifier that completes the newest composite boundary, or <see langword="null"/> when empty.</summary>
    public Guid? NewestTurnId { get; private set; }

    /// <summary>Determines whether a snapshot with the specified stable turn identifier is held.</summary>
    /// <param name="turnId">The turn identifier to locate.</param>
    /// <returns><see langword="true"/> when the window contains that identifier; otherwise <see langword="false"/>.</returns>
    public bool Contains(Guid turnId) => _turnsById.ContainsKey(turnId);

    /// <summary>
    /// Adds a snapshot or replaces the existing snapshot with the same turn identifier, then recalculates boundaries.
    /// </summary>
    /// <param name="turn">The immutable turn snapshot to retain. This method does not verify session identity or trim overflow.</param>
    public void AddOrUpdate(AgentTurnRecord turn)
    {
        _turnsById[turn.TurnId] = turn;
        RecalculateBoundaries();
    }

    /// <summary>Removes all turns and clears both paging boundaries.</summary>
    public void Reset()
    {
        _turnsById.Clear();
        OldestCreatedAtUtc = null;
        OldestTurnId = null;
        NewestCreatedAtUtc = null;
        NewestTurnId = null;
    }

    /// <summary>
    /// Enforces the configured turn limit by discarding overflow from the selected edge.
    /// </summary>
    /// <param name="direction">The edge from which overflow turns are removed.</param>
    /// <returns>A snapshot of retained and removed turns, each in ascending transcript order.</returns>
    public AgentTranscriptWindowTrimResult Trim(AgentTranscriptTrimDirection direction)
    {
        if (_turnsById.Count <= turnLimit)
        {
            return new AgentTranscriptWindowTrimResult(false, OrderedTurns(), []);
        }

        var orderedTurns = OrderedTurns();
        var overflowCount = orderedTurns.Length - turnLimit;
        var trimmedTurns = direction == AgentTranscriptTrimDirection.Oldest
            ? orderedTurns.Take(overflowCount).ToArray()
            : orderedTurns.Skip(turnLimit).ToArray();
        var retainedTurns = direction == AgentTranscriptTrimDirection.Oldest
            ? orderedTurns.Skip(overflowCount).ToArray()
            : orderedTurns.Take(turnLimit).ToArray();

        Reset();
        foreach (var turn in retainedTurns)
        {
            _turnsById[turn.TurnId] = turn;
        }

        RecalculateBoundaries();
        return new AgentTranscriptWindowTrimResult(true, retainedTurns, trimmedTurns);
    }

    /// <summary>Creates a new array containing all held turns in ascending transcript order.</summary>
    /// <returns>A caller-owned array ordered by creation time and then turn identifier.</returns>
    public AgentTurnRecord[] OrderedTurns()
        => _turnsById.Values
            .OrderBy(turn => turn.CreatedAtUtc)
            .ThenBy(turn => turn.TurnId)
            .ToArray();

    private void RecalculateBoundaries()
    {
        var orderedTurns = OrderedTurns();
        if (orderedTurns.Length == 0)
        {
            OldestCreatedAtUtc = null;
            OldestTurnId = null;
            NewestCreatedAtUtc = null;
            NewestTurnId = null;
            return;
        }

        var oldestTurn = orderedTurns[0];
        OldestCreatedAtUtc = oldestTurn.CreatedAtUtc;
        OldestTurnId = oldestTurn.TurnId;

        var newestTurn = orderedTurns[^1];
        NewestCreatedAtUtc = newestTurn.CreatedAtUtc;
        NewestTurnId = newestTurn.TurnId;
    }
}

/// <summary>
/// Reports the immutable outcome of enforcing a transcript turn-window bound.
/// </summary>
/// <param name="Trimmed">Whether the window exceeded its configured limit and a trim operation was applied.</param>
/// <param name="RetainedTurns">The snapshots left in the window, in ascending transcript order.</param>
/// <param name="TrimmedTurns">The snapshots removed from the selected edge, in ascending transcript order.</param>
public readonly record struct AgentTranscriptWindowTrimResult(
    bool Trimmed,
    IReadOnlyList<AgentTurnRecord> RetainedTurns,
    IReadOnlyList<AgentTurnRecord> TrimmedTurns);

/// <summary>
/// Selects the transcript edge from which a bounded window removes overflow.
/// </summary>
public enum AgentTranscriptTrimDirection
{
    /// <summary>Removes the earliest turns and retains the most recent tail.</summary>
    Oldest,

    /// <summary>Removes the latest turns and retains the earliest head.</summary>
    Newest,
}
