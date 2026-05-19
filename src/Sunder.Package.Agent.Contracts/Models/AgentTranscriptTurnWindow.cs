namespace Sunder.Package.Agent.Contracts.Models;

public sealed class AgentTranscriptTurnWindow(int turnLimit)
{
    private readonly Dictionary<Guid, AgentTurnRecord> _turnsById = new();

    public int Count => _turnsById.Count;

    public DateTimeOffset? OldestCreatedAtUtc { get; private set; }

    public Guid? OldestTurnId { get; private set; }

    public DateTimeOffset? NewestCreatedAtUtc { get; private set; }

    public Guid? NewestTurnId { get; private set; }

    public bool Contains(Guid turnId) => _turnsById.ContainsKey(turnId);

    public void AddOrUpdate(AgentTurnRecord turn)
    {
        _turnsById[turn.TurnId] = turn;
        RecalculateBoundaries();
    }

    public void Reset()
    {
        _turnsById.Clear();
        OldestCreatedAtUtc = null;
        OldestTurnId = null;
        NewestCreatedAtUtc = null;
        NewestTurnId = null;
    }

    public AgentTranscriptWindowTrimResult Trim(AgentTranscriptTrimDirection direction)
    {
        if (_turnsById.Count <= turnLimit)
        {
            return new AgentTranscriptWindowTrimResult(false, OrderedTurns());
        }

        var orderedTurns = OrderedTurns();
        var overflowCount = orderedTurns.Length - turnLimit;
        var retainedTurns = direction == AgentTranscriptTrimDirection.Oldest
            ? orderedTurns.Skip(overflowCount).ToArray()
            : orderedTurns.Take(turnLimit).ToArray();

        Reset();
        foreach (var turn in retainedTurns)
        {
            _turnsById[turn.TurnId] = turn;
        }

        RecalculateBoundaries();
        return new AgentTranscriptWindowTrimResult(true, retainedTurns);
    }

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

public readonly record struct AgentTranscriptWindowTrimResult(
    bool Trimmed,
    IReadOnlyList<AgentTurnRecord> RetainedTurns);

public enum AgentTranscriptTrimDirection
{
    Oldest,
    Newest,
}
