using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal interface ITranscriptActivityGroupRowFactory<TRow> where TRow : class
{
    TRow CreateGroup(AgentTranscriptActivityGroup group, TranscriptActivityGroupState state);
}

internal sealed partial class TranscriptRowProjector<TRow> where TRow : class
{
    private readonly Dictionary<TRow, AgentTurnRecord[]> _compactBackingByRow = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Guid, CompactBackingTurn> _compactTurnsById = [];
    private bool _compactBackingActive;
    internal long CompactBackingTurnsRebuilt { get; private set; }

    internal void UpdateCompactGroup(TranscriptActivityGroupState state, AgentTranscriptActivityGroup group)
    {
        RowsChanging?.Invoke();
        state.UpdateSummary(group);
    }

    internal TRow CreateCompactGroup(AgentTranscriptActivityGroup group, TranscriptActivityGroupState state)
    {
        var row = ((ITranscriptActivityGroupRowFactory<TRow>)_factory).CreateGroup(group, state);
        RowCreated?.Invoke(row);
        return row;
    }

    internal TRow? CreateCompactMessage(AgentTurnRecord turn)
    {
        var row = _factory.CreateMessage(turn, DescribeMessage(turn));
        if (row is not null) RowCreated?.Invoke(row);
        return row;
    }

    internal void UpdateCompactMessage(TRow row, AgentTurnRecord turn)
    {
        RowsChanging?.Invoke();
        _factory.UpdateMessage(row, turn, DescribeMessage(turn));
    }

    internal TRow CreateCompactTool(AgentTurnRecord turn)
    {
        var item = turn.Items.Single();
        var row = _factory.CreateTool(turn, item, DescribeCompactTool(turn, item));
        RowCreated?.Invoke(row);
        return row;
    }

    internal void UpdateCompactTool(TRow row, AgentTurnRecord turn)
    {
        RowsChanging?.Invoke();
        var item = turn.Items.Single();
        if (item.Kind == AgentTurnItemKind.ToolResult) _factory.ApplyToolResult(row, turn, item, DescribeCompactTool(turn, item));
        else _factory.UpdateTool(row, turn, item, DescribeCompactTool(turn, item));
    }

    private static TranscriptToolProjection DescribeCompactTool(AgentTurnRecord turn, AgentTurnItemRecord item)
        => DescribeTool(turn, item);

    internal void PublishCompactRows(IReadOnlyList<TRow> rows, IReadOnlyList<(TRow Row, AgentTurnRecord Turn)> backing)
    {
        RowsChanging?.Invoke();
        if (!_compactBackingActive)
        {
            _messageRowsByTurnId.Clear();
            _toolRowsByKey.Clear();
            _turnWindow.Reset();
            _compactBackingActive = true;
        }
        var desired = rows.ToHashSet(ReferenceEqualityComparer.Instance);
        UpdateCompactBacking(backing, _compactBackingByRow.Keys.Where(row => !desired.Contains(row)).ToArray());
        ReconcileRows(rows);
    }

    internal void PublishCompactMembers(TRow groupRow, int previousCount, IReadOnlyList<TRow> members,
        IReadOnlyList<(TRow Row, AgentTurnRecord Turn)> backing)
    {
        RowsChanging?.Invoke();
        var start = _rows.IndexOf(groupRow) + 1;
        var desired = members.ToHashSet(ReferenceEqualityComparer.Instance);
        var removed = _rows.Skip(start).Take(previousCount).Where(row => !desired.Contains(row)).ToArray();
        UpdateCompactBacking(backing, removed);
        if (_rows is TranscriptObservableCollection<TRow> collection)
        {
            foreach (var row in removed) _factory.DisposeRow(row);
            collection.ReconcileSlice(start, previousCount, members);
        }
        else ReconcileRows(_rows.Take(start).Concat(members).Concat(_rows.Skip(start + previousCount)).ToArray());
    }

    internal void UpdateCompactBacking(IReadOnlyList<(TRow Row, AgentTurnRecord Turn)> backing,
        IReadOnlyList<TRow>? removedRows = null)
    {
        var dirty = new HashSet<Guid>();
        foreach (var row in removedRows ?? []) RemoveBacking(row);
        IEqualityComparer<TRow> comparer = ReferenceEqualityComparer.Instance;
        foreach (var group in backing.GroupBy(item => item.Row, comparer))
        {
            var row = group.Key;
            var turns = group.Select(item => item.Turn).ToArray();
            if (_compactBackingByRow.TryGetValue(row, out var current)
                && current.SequenceEqual(turns, ReferenceEqualityComparer.Instance)) continue;
            RemoveBacking(row);
            _compactBackingByRow[row] = turns;
            foreach (var turn in turns)
            {
                dirty.Add(turn.TurnId);
                if (!_compactTurnsById.TryGetValue(turn.TurnId, out var retained))
                    _compactTurnsById[turn.TurnId] = retained = new(turn);
                retained.Header = turn;
                foreach (var item in turn.Items)
                {
                    retained.Items[item.ItemId] = item;
                    if (item.Kind is AgentTurnItemKind.ToolCall or AgentTurnItemKind.ToolResult)
                        _toolRowsByKey[CreateToolKey(turn, item)] = row;
                }
                if (turn.Kind is not (AgentTurnKind.ToolCall or AgentTurnKind.ToolResult))
                    _messageRowsByTurnId[turn.TurnId] = row;
            }
        }
        if (dirty.Count == 0) return;
        var updates = new List<AgentTurnRecord>();
        var removedTurns = new List<Guid>();
        foreach (var id in dirty)
        {
            var retained = _compactTurnsById[id];
            if (retained.Items.Count == 0)
            {
                _compactTurnsById.Remove(id);
                removedTurns.Add(id);
            }
            else updates.Add(retained.Header with { Items = retained.Items.Values.OrderBy(item => item.SequenceNumber).ToArray() });
        }
        _turnWindow.ApplyBatch(updates, removedTurns);
        CompactBackingTurnsRebuilt += updates.Count;

        void RemoveBacking(TRow row)
        {
            if (!_compactBackingByRow.Remove(row, out var previous)) return;
            foreach (var turn in previous)
            {
                dirty.Add(turn.TurnId);
                var retained = _compactTurnsById[turn.TurnId];
                foreach (var item in turn.Items)
                {
                    retained.Items.Remove(item.ItemId);
                    if (item.Kind is AgentTurnItemKind.ToolCall or AgentTurnItemKind.ToolResult
                        && _toolRowsByKey.TryGetValue(CreateToolKey(turn, item), out var owner) && ReferenceEquals(owner, row))
                        _toolRowsByKey.Remove(CreateToolKey(turn, item));
                }
                if (_messageRowsByTurnId.TryGetValue(turn.TurnId, out var message) && ReferenceEquals(message, row))
                    _messageRowsByTurnId.Remove(turn.TurnId);
            }
        }
    }

    private sealed class CompactBackingTurn(AgentTurnRecord header)
    {
        internal AgentTurnRecord Header = header;
        internal Dictionary<Guid, AgentTurnItemRecord> Items { get; } = [];
    }

    private void ClearCompactBacking()
    {
        _compactBackingActive = false;
        _compactBackingByRow.Clear();
        _compactTurnsById.Clear();
    }
}
