using System.Collections.ObjectModel;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal enum TranscriptProjectedRowKind
{
    Message,
    Reasoning,
    ToolCall,
    ToolResult,
    Permission,
    Activity,
    Error,
}

internal readonly record struct TranscriptMessageProjection(
    TranscriptProjectedRowKind Kind,
    string Content,
    string RoleGlyph,
    object AnchorKey);

internal readonly record struct TranscriptToolProjection(
    TranscriptProjectedRowKind Kind,
    string ToolLabel,
    string StatusText,
    string StatusIconText,
    object AnchorKey);

internal readonly record struct TranscriptActivityProjection(
    TranscriptProjectedRowKind Kind,
    string Text,
    bool IsReasoning,
    object AnchorKey);

internal readonly record struct TranscriptPermissionProjection(
    TranscriptProjectedRowKind Kind,
    string Summary,
    string ActionText,
    object AnchorKey);

internal interface ITranscriptRowFactory<TRow> where TRow : class
{
    TRow? CreateMessage(AgentTurnRecord turn, TranscriptMessageProjection projection);

    void UpdateMessage(TRow row, AgentTurnRecord turn, TranscriptMessageProjection projection);

    TRow CreateTool(
        AgentTurnRecord turn,
        AgentTurnItemRecord item,
        TranscriptToolProjection projection);

    void ApplyToolResult(
        TRow row,
        AgentTurnRecord turn,
        AgentTurnItemRecord item,
        TranscriptToolProjection projection);

    TRow CreateActivity(TranscriptActivityProjection projection);

    void UpdateActivity(TRow row, TranscriptActivityProjection projection);

    Guid GetRowId(TRow row);

    object GetAnchorKey(TRow row);

    Guid? GetResultTurnId(TRow row);

    void SetExpanded(TRow row, bool isExpanded);

    void RefreshRelatedRows(TRow row);

    void DisposeRow(TRow row);
}

internal sealed partial class TranscriptRowProjector<TRow> where TRow : class
{
    private readonly ObservableCollection<TRow> _rows;
    private readonly ITranscriptRowFactory<TRow> _factory;
    private readonly Dictionary<Guid, TRow> _messageRowsByTurnId = [];
    private readonly Dictionary<string, TRow> _toolRowsByKey = new(StringComparer.Ordinal);
    private readonly AgentTranscriptTurnWindow _turnWindow;
    private readonly int _turnCapacity;
    private TRow? _activityRow;

    public TranscriptRowProjector(
        ObservableCollection<TRow> rows,
        ITranscriptRowFactory<TRow> factory,
        int turnCapacity)
    {
        _rows = rows;
        _factory = factory;
        _turnCapacity = turnCapacity;
        _turnWindow = new AgentTranscriptTurnWindow(turnCapacity);
    }

    public event Action? RowsChanging;

    public event Action<TRow>? RowCreated;

    public ObservableCollection<TRow> Rows => _rows;

    public AgentTranscriptTurnWindow TurnWindow => _turnWindow;

    public TRow? ActivityRow => _activityRow;

    public IReadOnlyCollection<TRow> ToolRows => _toolRowsByKey.Values;

    public int ApplyTurn(
        AgentTurnRecord turn,
        TranscriptInsertMode insertMode,
        int prependIndex = 0,
        bool replaceOnEqualTimestamp = true)
    {
        var insertedRows = 0;
        var previousTurn = _turnWindow.OrderedTurns()
            .FirstOrDefault(item => item.TurnId == turn.TurnId);
        if (previousTurn is not null
            && IsStaleTurn(previousTurn, turn, replaceOnEqualTimestamp))
        {
            return 0;
        }

        var previouslyKnownToolKeys = previousTurn?.Items
            .Where(item => item.Kind is AgentTurnItemKind.ToolCall or AgentTurnItemKind.ToolResult)
            .Select(item => CreateToolKey(previousTurn, item))
            .ToHashSet(StringComparer.Ordinal) ?? [];
        _turnWindow.AddOrUpdate(turn);

        switch (turn.Kind)
        {
            case AgentTurnKind.ToolCall:
                foreach (var item in turn.Items.Where(item => item.Kind == AgentTurnItemKind.ToolCall))
                {
                    var key = CreateToolKey(turn, item);
                    if (_toolRowsByKey.ContainsKey(key)
                        || previouslyKnownToolKeys.Contains(key))
                    {
                        continue;
                    }

                    var row = _factory.CreateTool(turn, item, DescribeTool(turn, item));
                    _toolRowsByKey[key] = row;
                    InsertRow(row, insertMode, prependIndex + insertedRows);
                    RowCreated?.Invoke(row);
                    insertedRows++;
                }

                break;

            case AgentTurnKind.ToolResult:
                foreach (var item in turn.Items.Where(item => item.Kind == AgentTurnItemKind.ToolResult))
                {
                    var key = CreateToolKey(turn, item);
                    if (_toolRowsByKey.TryGetValue(key, out var existingToolRow))
                    {
                        RowsChanging?.Invoke();
                        _factory.ApplyToolResult(existingToolRow, turn, item, DescribeTool(turn, item));
                        continue;
                    }

                    if (previouslyKnownToolKeys.Contains(key))
                    {
                        continue;
                    }

                    var row = _factory.CreateTool(turn, item, DescribeTool(turn, item));
                    _toolRowsByKey[key] = row;
                    InsertRow(row, insertMode, prependIndex + insertedRows);
                    RowCreated?.Invoke(row);
                    insertedRows++;
                }

                break;

            default:
                var projection = DescribeMessage(turn);
                if (_messageRowsByTurnId.TryGetValue(turn.TurnId, out var existingMessageRow))
                {
                    RowsChanging?.Invoke();
                    _factory.UpdateMessage(existingMessageRow, turn, projection);
                    break;
                }

                if (_factory.CreateMessage(turn, projection) is not { } messageRow)
                {
                    break;
                }

                _messageRowsByTurnId[turn.TurnId] = messageRow;
                InsertRow(messageRow, insertMode, prependIndex + insertedRows);
                RowCreated?.Invoke(messageRow);
                insertedRows++;
                break;
        }

        TrimTurnWindow(insertMode == TranscriptInsertMode.Append
            ? AgentTranscriptTrimDirection.Oldest
            : AgentTranscriptTrimDirection.Newest);

        return insertedRows;
    }

    public bool CanApplyTurn(AgentTurnRecord turn, bool replaceOnEqualTimestamp = true)
    {
        var previousTurn = _turnWindow.OrderedTurns()
            .FirstOrDefault(item => item.TurnId == turn.TurnId);
        return previousTurn is null || !IsStaleTurn(previousTurn, turn, replaceOnEqualTimestamp);
    }

    public int ApplyTurns(
        IEnumerable<AgentTurnRecord> turns,
        TranscriptInsertMode insertMode)
    {
        var insertedRows = 0;
        foreach (var turn in turns)
        {
            insertedRows += ApplyTurn(turn, insertMode, insertedRows);
        }

        return insertedRows;
    }

    public TranscriptTrimResult EnforceLimit(
        int visibleRowLimit,
        AgentTranscriptTrimDirection trimDirection,
        object? protectedAnchorKey = null)
    {
        var visibleRows = _rows.Where(row => !ReferenceEquals(row, _activityRow)).ToArray();
        var retainedRowLimit = Math.Max(
            0,
            visibleRowLimit - (_activityRow is not null && _rows.Contains(_activityRow) ? 1 : 0));
        if (visibleRows.Length <= retainedRowLimit)
        {
            return TranscriptTrimResult.None;
        }

        var retainedRows = TranscriptRowWindow.SelectRetainedRows(
            visibleRows,
            retainedRowLimit,
            trimDirection,
            protectedAnchorKey,
            _factory.GetAnchorKey);
        var retainedTurnIds = BuildRetainedTurnIdSet(retainedRows);
        var retainedTurns = _turnWindow.OrderedTurns()
            .Where(turn => retainedTurnIds.Contains(turn.TurnId))
            .ToArray();
        var retainedAnchorKeys = retainedRows
            .Select(_factory.GetAnchorKey)
            .ToHashSet();
        var firstRetainedIndex = retainedRows.Length == 0
            ? -1
            : Array.FindIndex(visibleRows, row => ReferenceEquals(row, retainedRows[0]));
        var lastRetainedIndex = retainedRows.Length == 0
            ? -1
            : Array.FindLastIndex(visibleRows, row => ReferenceEquals(row, retainedRows[^1]));
        Rebuild(retainedTurns, retainedAnchorKeys);
        if (retainedRows.Length == 0)
        {
            return trimDirection == AgentTranscriptTrimDirection.Oldest
                ? TranscriptTrimResult.Oldest
                : TranscriptTrimResult.Newest;
        }

        var result = TranscriptTrimResult.None;
        if (firstRetainedIndex > 0)
        {
            result |= TranscriptTrimResult.Oldest;
        }
        if (lastRetainedIndex >= 0 && lastRetainedIndex < visibleRows.Length - 1)
        {
            result |= TranscriptTrimResult.Newest;
        }
        return result;
    }

    public bool SetActivity(string text, bool isReasoning, bool isVisible)
    {
        if (!isVisible)
        {
            return RemoveActivity();
        }

        var projection = DescribeActivity(text, isReasoning);
        if (_activityRow is null)
        {
            RowsChanging?.Invoke();
            _activityRow = _factory.CreateActivity(projection);
            _rows.Add(_activityRow);
            RowCreated?.Invoke(_activityRow);
            return true;
        }

        RowsChanging?.Invoke();
        _factory.UpdateActivity(_activityRow, projection);
        var index = _rows.IndexOf(_activityRow);
        if (index >= 0 && index != _rows.Count - 1)
        {
            RowsChanging?.Invoke();
            _rows.Move(index, _rows.Count - 1);
        }
        else if (index < 0)
        {
            RowsChanging?.Invoke();
            _rows.Add(_activityRow);
        }

        return true;
    }

    public bool RemoveActivity()
    {
        if (_activityRow is null)
        {
            return false;
        }

        if (_rows.IndexOf(_activityRow) is var index && index >= 0)
        {
            RowsChanging?.Invoke();
            _rows.RemoveAt(index);
        }

        _factory.DisposeRow(_activityRow);
        _activityRow = null;
        return true;
    }

    public bool CanApplyHistoricalTurnUpdate(AgentTurnRecord turn)
        => _turnWindow.Contains(turn.TurnId)
           || turn.Items.Any(item =>
               item.Kind is AgentTurnItemKind.ToolCall or AgentTurnItemKind.ToolResult
               && _toolRowsByKey.ContainsKey(CreateToolKey(turn, item)));

    public bool RefreshRelatedRows()
    {
        if (_toolRowsByKey.Count > 0)
        {
            RowsChanging?.Invoke();
        }

        foreach (var row in _toolRowsByKey.Values)
        {
            _factory.RefreshRelatedRows(row);
        }

        return _toolRowsByKey.Count > 0;
    }

    public void ReorderRowsChronologically()
    {
        var turnIndices = _turnWindow.OrderedTurns()
            .Select((turn, index) => (turn.TurnId, index))
            .ToDictionary(item => item.TurnId, item => item.index);
        var currentRows = _rows
            .Where(row => !ReferenceEquals(row, _activityRow))
            .ToArray();
        var desiredRows = currentRows
            .Select((row, index) => (Row: row, CurrentIndex: index))
            .OrderBy(item => turnIndices.TryGetValue(_factory.GetRowId(item.Row), out var turnIndex)
                ? turnIndex
                : _factory.GetResultTurnId(item.Row) is { } resultTurnId
                  && turnIndices.TryGetValue(resultTurnId, out var resultIndex)
                    ? resultIndex
                    : int.MaxValue)
            .ThenBy(item => item.CurrentIndex)
            .Select(item => item.Row)
            .ToArray();
        if (currentRows.SequenceEqual(desiredRows, ReferenceEqualityComparer.Instance))
        {
            return;
        }

        RowsChanging?.Invoke();
        for (var targetIndex = 0; targetIndex < desiredRows.Length; targetIndex++)
        {
            var currentIndex = _rows.IndexOf(desiredRows[targetIndex]);
            if (currentIndex != targetIndex)
            {
                _rows.Move(currentIndex, targetIndex);
            }
        }
    }

    public void SetExpanded(TRow row, bool isExpanded) => _factory.SetExpanded(row, isExpanded);

    public object GetAnchorKey(TRow row) => _factory.GetAnchorKey(row);

    public TRow? FindByAnchorKey(object? anchorKey)
        => anchorKey is null
            ? null
            : _rows.FirstOrDefault(row => Equals(_factory.GetAnchorKey(row), anchorKey));

    public void Reset()
    {
        foreach (var row in _rows)
        {
            _factory.DisposeRow(row);
        }

        _activityRow = null;
        _messageRowsByTurnId.Clear();
        _toolRowsByKey.Clear();
        _turnWindow.Reset();
        _rows.Clear();
    }

    public void ReconcileAuthoritativeTurns(IReadOnlyList<AgentTurnRecord> turns)
    {
        var anchorKeys = turns
            .SelectMany(turn => turn.Kind switch
            {
                AgentTurnKind.ToolCall => turn.Items
                    .Where(item => item.Kind == AgentTurnItemKind.ToolCall)
                    .Select(item => DescribeTool(turn, item).AnchorKey),
                AgentTurnKind.ToolResult => turn.Items
                    .Where(item => item.Kind == AgentTurnItemKind.ToolResult)
                    .Select(item => DescribeTool(turn, item).AnchorKey),
                _ => [DescribeMessage(turn).AnchorKey],
            })
            .ToHashSet();
        Rebuild(turns, anchorKeys);
    }

    public static AgentTurnRecord[] SelectLatestTurns(
        IEnumerable<AgentTurnRecord> turns,
        int pageSize)
    {
        var ordered = OrderTurns(turns);
        return ordered.Length <= pageSize ? ordered : ordered[^pageSize..];
    }

    public static AgentTurnRecord[] OrderTurns(IEnumerable<AgentTurnRecord> turns)
        => turns.OrderBy(turn => turn.CreatedAtUtc).ThenBy(turn => turn.TurnId).ToArray();

    public static TranscriptMessageProjection DescribeMessage(AgentTurnRecord turn)
        => new(
            TranscriptProjectedRowKind.Message,
            ExtractTextContent(turn),
            ResolveRoleGlyph(turn.Role),
            TranscriptRowAnchorKey.Text(turn.TurnId));

    public static TranscriptToolProjection DescribeTool(
        AgentTurnRecord turn,
        AgentTurnItemRecord item)
    {
        var kind = item.IsError
            ? TranscriptProjectedRowKind.Error
            : item.Kind == AgentTurnItemKind.ToolResult
                ? TranscriptProjectedRowKind.ToolResult
                : TranscriptProjectedRowKind.ToolCall;
        var status = item.Kind == AgentTurnItemKind.ToolResult
            ? item.IsError ? "Failed" : "Completed"
            : "Running";
        return new TranscriptToolProjection(
            kind,
            HumanizeToolName(item.ToolId),
            status,
            status == "Completed" ? "✓" : status == "Running" ? "i" : "!",
            TranscriptRowAnchorKey.Tool(turn, item));
    }

    public static TranscriptActivityProjection DescribeActivity(string text, bool isReasoning)
        => new(
            isReasoning ? TranscriptProjectedRowKind.Reasoning : TranscriptProjectedRowKind.Activity,
            string.IsNullOrWhiteSpace(text) ? "Processing" : text.Trim(),
            isReasoning,
            TranscriptRowAnchorKey.Activity());

    public static TranscriptPermissionProjection DescribePermission(
        string requestId,
        string summary,
        string? command,
        string? path)
    {
        var actionText = new[] { command, path }
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?.Trim() ?? string.Empty;
        return new TranscriptPermissionProjection(
            TranscriptProjectedRowKind.Permission,
            summary.Trim(),
            actionText,
            new TranscriptRowAnchorKey($"permission:{requestId}"));
    }

    public static string ExtractTextContent(AgentTurnRecord turn)
        => string.Concat(turn.Items
            .Where(item => item.Kind == AgentTurnItemKind.Text)
            .OrderBy(item => item.SequenceNumber)
            .Select(item => item.TextContent ?? string.Empty));

    private static bool IsStaleTurn(
        AgentTurnRecord previousTurn,
        AgentTurnRecord turn,
        bool replaceOnEqualTimestamp)
    {
        if (previousTurn.ContentRevision != turn.ContentRevision)
        {
            return previousTurn.ContentRevision > turn.ContentRevision;
        }

        return previousTurn.UpdatedAtUtc > turn.UpdatedAtUtc
               || !replaceOnEqualTimestamp && previousTurn.UpdatedAtUtc == turn.UpdatedAtUtc;
    }

    private void InsertRow(TRow row, TranscriptInsertMode insertMode, int prependIndex)
    {
        RowsChanging?.Invoke();
        if (insertMode == TranscriptInsertMode.Prepend)
        {
            _rows.Insert(Math.Clamp(prependIndex, 0, _rows.Count), row);
            return;
        }

        var insertIndex = _activityRow is null
            ? _rows.Count
            : Math.Max(0, _rows.IndexOf(_activityRow));
        _rows.Insert(insertIndex, row);
    }

    private HashSet<Guid> BuildRetainedTurnIdSet(IEnumerable<TRow> retainedRows)
    {
        var retainedTurnIds = new HashSet<Guid>();
        foreach (var row in retainedRows)
        {
            if (_factory.GetRowId(row) is var rowId && rowId != Guid.Empty)
            {
                retainedTurnIds.Add(rowId);
            }

            if (_factory.GetResultTurnId(row) is { } resultTurnId)
            {
                retainedTurnIds.Add(resultTurnId);
            }
        }

        return retainedTurnIds;
    }

    private void TrimTurnWindow(AgentTranscriptTrimDirection direction)
    {
        if (_turnWindow.Count <= _turnCapacity)
        {
            return;
        }

        var orderedTurns = _turnWindow.OrderedTurns();
        var overflowCount = orderedTurns.Length - _turnCapacity;
        var protectedTurnIds = BuildRetainedTurnIdSet(
            _rows.Where(row => !ReferenceEquals(row, _activityRow)));
        var trimCandidates = direction == AgentTranscriptTrimDirection.Oldest
            ? orderedTurns
            : orderedTurns.Reverse();
        var trimmedTurnIds = trimCandidates
            .OrderBy(turn => protectedTurnIds.Contains(turn.TurnId) ? 1 : 0)
            .Take(overflowCount)
            .Select(turn => turn.TurnId)
            .ToHashSet();

        _turnWindow.Reset();
        foreach (var retainedTurn in orderedTurns.Where(turn => !trimmedTurnIds.Contains(turn.TurnId)))
        {
            _turnWindow.AddOrUpdate(retainedTurn);
        }
    }

    private void Rebuild(
        IReadOnlyList<AgentTurnRecord> turns,
        IReadOnlySet<object> retainedAnchorKeys)
    {
        RowsChanging?.Invoke();
        var activityRow = _activityRow;
        if (activityRow is not null)
        {
            _rows.Remove(activityRow);
        }

        var oldMessageRows = new Dictionary<Guid, TRow>(_messageRowsByTurnId);
        var oldToolRows = new Dictionary<string, TRow>(_toolRowsByKey, StringComparer.Ordinal);
        var orderedTurns = OrderTurns(turns);
        var retainedTurnIds = orderedTurns.Select(turn => turn.TurnId).ToHashSet();
        var desiredRows = new List<TRow>();

        _messageRowsByTurnId.Clear();
        _toolRowsByKey.Clear();
        _turnWindow.Reset();

        foreach (var turn in orderedTurns)
        {
            _turnWindow.AddOrUpdate(turn);
            AddRetainedTurnRows(
                turn,
                retainedTurnIds,
                retainedAnchorKeys,
                oldMessageRows,
                oldToolRows,
                desiredRows);
        }

        ReconcileRows(desiredRows);
        _activityRow = activityRow;
        if (activityRow is not null)
        {
            _rows.Add(activityRow);
        }
    }

    private void AddRetainedTurnRows(
        AgentTurnRecord turn,
        IReadOnlySet<Guid> retainedTurnIds,
        IReadOnlySet<object> retainedAnchorKeys,
        IReadOnlyDictionary<Guid, TRow> oldMessageRows,
        IReadOnlyDictionary<string, TRow> oldToolRows,
        ICollection<TRow> desiredRows)
    {
        switch (turn.Kind)
        {
            case AgentTurnKind.ToolCall:
                foreach (var item in turn.Items.Where(item => item.Kind == AgentTurnItemKind.ToolCall))
                {
                    var key = CreateToolKey(turn, item);
                    var toolProjection = DescribeTool(turn, item);
                    if (!retainedAnchorKeys.Contains(toolProjection.AnchorKey)
                        || _toolRowsByKey.ContainsKey(key))
                    {
                        continue;
                    }

                    var reuseExisting = oldToolRows.TryGetValue(key, out var existingRow)
                                        && _factory.GetRowId(existingRow) == turn.TurnId
                                        && (_factory.GetResultTurnId(existingRow) is not { } resultId
                                            || retainedTurnIds.Contains(resultId));
                    var row = reuseExisting
                        ? existingRow!
                        : _factory.CreateTool(turn, item, toolProjection);
                    _toolRowsByKey[key] = row;
                    desiredRows.Add(row);
                    if (!reuseExisting)
                    {
                        RowCreated?.Invoke(row);
                    }
                }

                break;

            case AgentTurnKind.ToolResult:
                foreach (var item in turn.Items.Where(item => item.Kind == AgentTurnItemKind.ToolResult))
                {
                    var key = CreateToolKey(turn, item);
                    var toolProjection = DescribeTool(turn, item);
                    if (!retainedAnchorKeys.Contains(toolProjection.AnchorKey))
                    {
                        continue;
                    }

                    if (_toolRowsByKey.TryGetValue(key, out var existingToolRow))
                    {
                        _factory.ApplyToolResult(existingToolRow, turn, item, toolProjection);
                        continue;
                    }

                    var reuseExisting = oldToolRows.TryGetValue(key, out var existingRow)
                                        && (_factory.GetRowId(existingRow) == turn.TurnId
                                            || _factory.GetResultTurnId(existingRow) == turn.TurnId);
                    var row = reuseExisting
                        ? existingRow!
                        : _factory.CreateTool(turn, item, toolProjection);
                    if (reuseExisting)
                    {
                        _factory.ApplyToolResult(row, turn, item, toolProjection);
                    }
                    _toolRowsByKey[key] = row;
                    desiredRows.Add(row);
                    if (!reuseExisting)
                    {
                        RowCreated?.Invoke(row);
                    }
                }

                break;

            default:
                var projection = DescribeMessage(turn);
                if (!retainedAnchorKeys.Contains(projection.AnchorKey))
                {
                    break;
                }

                if (oldMessageRows.TryGetValue(turn.TurnId, out var existingMessageRow))
                {
                    _factory.UpdateMessage(existingMessageRow, turn, projection);
                    _messageRowsByTurnId[turn.TurnId] = existingMessageRow;
                    desiredRows.Add(existingMessageRow);
                }
                else if (_factory.CreateMessage(turn, projection) is { } messageRow)
                {
                    _messageRowsByTurnId[turn.TurnId] = messageRow;
                    desiredRows.Add(messageRow);
                    RowCreated?.Invoke(messageRow);
                }

                break;
        }
    }

    private void ReconcileRows(IReadOnlyList<TRow> desiredRows)
    {
        var desiredRowSet = desiredRows.ToHashSet(ReferenceEqualityComparer.Instance);
        for (var index = _rows.Count - 1; index >= 0; index--)
        {
            if (!desiredRowSet.Contains(_rows[index]))
            {
                _factory.DisposeRow(_rows[index]);
                _rows.RemoveAt(index);
            }
        }

        for (var targetIndex = 0; targetIndex < desiredRows.Count; targetIndex++)
        {
            var row = desiredRows[targetIndex];
            var currentIndex = _rows.IndexOf(row);
            if (currentIndex < 0)
            {
                _rows.Insert(targetIndex, row);
            }
            else if (currentIndex != targetIndex)
            {
                _rows.Move(currentIndex, targetIndex);
            }
        }
    }

    private static string CreateToolKey(AgentTurnRecord turn, AgentTurnItemRecord item)
        => !string.IsNullOrWhiteSpace(item.CallId)
            ? item.CallId
            : $"{turn.TurnId:N}:{item.ItemId:N}";

    private static string ResolveRoleGlyph(AgentMessageRole role)
        => role switch
        {
            AgentMessageRole.User => "U",
            AgentMessageRole.Assistant => "A",
            AgentMessageRole.System => "S",
            AgentMessageRole.Tool => "T",
            _ => "?",
        };

    private static string HumanizeToolName(string? toolId)
    {
        var parts = (toolId ?? string.Empty)
            .Split(['_', '-', '.'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0
            ? "Tool"
            : string.Join(' ', parts.Select(part =>
                char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }
}

internal enum TranscriptInsertMode
{
    Append,
    Prepend,
}

[Flags]
internal enum TranscriptTrimResult
{
    None = 0,
    Oldest = 1,
    Newest = 2,
}
