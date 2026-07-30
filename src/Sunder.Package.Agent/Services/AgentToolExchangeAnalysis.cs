using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services;

internal static class AgentToolExchangeAnalyzer
{
    public static AgentToolExchangeAnalysis Analyze(IReadOnlyList<AgentTurnRecord> turns)
    {
        var pendingCalls = new Dictionary<string, List<AgentToolExchangeItem>>(StringComparer.Ordinal);
        var pairs = new List<AgentToolExchangePair>();
        var unpairedItemIds = new HashSet<Guid>();
        for (var turnIndex = 0; turnIndex < turns.Count; turnIndex++)
        {
            var turn = turns[turnIndex];
            foreach (var item in turn.Items.OrderBy(item => item.SequenceNumber))
            {
                if (item.Kind is not (AgentTurnItemKind.ToolCall or AgentTurnItemKind.ToolResult)
                    || string.IsNullOrWhiteSpace(item.CallId))
                {
                    if (item.Kind is AgentTurnItemKind.ToolCall or AgentTurnItemKind.ToolResult)
                    {
                        unpairedItemIds.Add(item.ItemId);
                    }
                    continue;
                }

                if (item.Kind == AgentTurnItemKind.ToolCall)
                {
                    if (!pendingCalls.TryGetValue(item.CallId!, out var calls))
                    {
                        calls = [];
                        pendingCalls[item.CallId!] = calls;
                    }
                    calls.Add(CreateExchangeItem(turn, item, turnIndex));
                    continue;
                }

                var result = CreateExchangeItem(turn, item, turnIndex);
                if (pendingCalls.TryGetValue(item.CallId!, out var matchingCalls)
                    && FindMatchingCallIndex(matchingCalls, result) is var callIndex
                    && callIndex >= 0)
                {
                    var call = matchingCalls[callIndex];
                    matchingCalls.RemoveAt(callIndex);
                    pairs.Add(new AgentToolExchangePair(
                        call.ItemId,
                        call.TurnIndex,
                        item.ItemId,
                        turnIndex));
                }
                else
                {
                    unpairedItemIds.Add(item.ItemId);
                }
            }
        }

        foreach (var call in pendingCalls.Values.SelectMany(calls => calls))
        {
            unpairedItemIds.Add(call.ItemId);
        }

        return new AgentToolExchangeAnalysis(pairs, unpairedItemIds);
    }

    private static AgentToolExchangeItem CreateExchangeItem(
        AgentTurnRecord turn,
        AgentTurnItemRecord item,
        int turnIndex)
        => new(
            item.ItemId,
            turnIndex,
            item.ToolExecutionId,
            turn.RunId,
            turn.RunRevision,
            item.ToolId);

    private static int FindMatchingCallIndex(
        IReadOnlyList<AgentToolExchangeItem> calls,
        AgentToolExchangeItem result)
    {
        if (result.ToolExecutionId is { } executionId)
        {
            var exactExecution = FindLastIndex(calls, call =>
                call.ToolExecutionId == executionId && IsCompatible(call, result));
            if (exactExecution >= 0)
            {
                return exactExecution;
            }
        }
        if (result.RunId is { } runId && result.RunRevision is { } runRevision)
        {
            var exactRun = FindLastIndex(calls, call =>
                call.RunId == runId
                && call.RunRevision == runRevision
                && (call.ToolExecutionId is null || result.ToolExecutionId is null)
                && HasCompatibleToolIdentity(call, result));
            if (exactRun >= 0)
            {
                return exactRun;
            }
        }

        return FindLastIndex(calls, call => IsCompatible(call, result));
    }

    private static int FindLastIndex(
        IReadOnlyList<AgentToolExchangeItem> items,
        Func<AgentToolExchangeItem, bool> predicate)
    {
        for (var index = items.Count - 1; index >= 0; index--)
        {
            if (predicate(items[index]))
            {
                return index;
            }
        }
        return -1;
    }

    private static bool IsCompatible(AgentToolExchangeItem call, AgentToolExchangeItem result)
        => (call.ToolExecutionId is null
            || result.ToolExecutionId is null
            || call.ToolExecutionId == result.ToolExecutionId)
           && (call.RunId is null || result.RunId is null || call.RunId == result.RunId)
           && (call.RunRevision is null
               || result.RunRevision is null
               || call.RunRevision == result.RunRevision)
           && HasCompatibleToolIdentity(call, result);

    private static bool HasCompatibleToolIdentity(
        AgentToolExchangeItem call,
        AgentToolExchangeItem result)
        => string.IsNullOrWhiteSpace(call.ToolId)
           || string.IsNullOrWhiteSpace(result.ToolId)
           || string.Equals(call.ToolId, result.ToolId, StringComparison.Ordinal);

    private readonly record struct AgentToolExchangeItem(
        Guid ItemId,
        int TurnIndex,
        Guid? ToolExecutionId,
        Guid? RunId,
        long? RunRevision,
        string? ToolId);
}

internal sealed class AgentToolExchangeAnalysis
{
    private readonly IReadOnlyDictionary<Guid, AgentToolExchangePair> _pairsByItemId;

    public AgentToolExchangeAnalysis(
        IReadOnlyList<AgentToolExchangePair> pairs,
        IReadOnlySet<Guid> unpairedItemIds)
    {
        Pairs = pairs;
        UnpairedItemIds = unpairedItemIds;
        PairedItemIds = pairs
            .SelectMany(pair => new[] { pair.CallItemId, pair.ResultItemId })
            .ToHashSet();
        _pairsByItemId = pairs
            .SelectMany(pair => new[]
            {
                KeyValuePair.Create(pair.CallItemId, pair),
                KeyValuePair.Create(pair.ResultItemId, pair),
            })
            .ToDictionary();
    }

    public IReadOnlyList<AgentToolExchangePair> Pairs { get; }

    public IReadOnlySet<Guid> PairedItemIds { get; }

    public IReadOnlySet<Guid> UnpairedItemIds { get; }

    public bool TryGetPair(Guid itemId, out AgentToolExchangePair pair)
        => _pairsByItemId.TryGetValue(itemId, out pair!);
}

internal sealed record AgentToolExchangePair(
    Guid CallItemId,
    int CallTurnIndex,
    Guid ResultItemId,
    int ResultTurnIndex);
