using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Shared.PackageViews;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class TranscriptCompactPayload
{
    internal const int DefaultMaximumBytes = 8 * 1024 * 1024 - 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static AgentCompactTranscriptPage Fit(
        AgentCompactTranscriptPage page, AgentCompactTranscriptDirection direction, int maximumBytes = DefaultMaximumBytes)
    {
        var entries = page.Entries.ToList();
        while (Size(page) > maximumBytes && entries.Count > 1)
        {
            var anchor = page.AnchorEntryId is { } id ? entries.FindIndex(entry => entry.EntryId == id) : -1;
            var removeOldest = anchor >= 0 ? anchor > entries.Count - anchor - 1
                : direction is TranscriptCompactDirections.Recent or TranscriptCompactDirections.Before;
            entries.RemoveAt(removeOldest ? 0 : entries.Count - 1);
            page = Copy(page, entries.ToArray(), page.HasOlder || removeOldest, page.HasNewer || !removeOldest);
        }
        if (Size(page) <= maximumBytes) return page;
        if (entries.Count == 1 && entries[0].Message is { } message)
        {
            for (var budget = 512 * 1024; ; budget /= 2)
            {
                var projected = TranscriptTurnTransportProjection.Project(message, budget, 32);
                page = Copy(page, [Copy(entries[0], projected)], page.HasOlder, page.HasNewer);
                if (Size(page) <= maximumBytes) return page;
                if (budget == 0) break;
            }
        }
        throw new InvalidOperationException("Compact transcript metadata exceeds the Runtime response limit.");
    }

    internal static AgentTranscriptActivityMembersPage Fit(
        AgentTranscriptActivityMembersPage page, int maximumBytes = DefaultMaximumBytes)
    {
        while (Size(page) > maximumBytes && page.Headers.Count > 1)
        {
            var headers = page.Headers.Take(page.Headers.Count - 1).ToArray();
            var last = headers[^1];
            var item = last.Items.Single();
            page = Members(headers, true, Cursor(last, item));
        }
        if (Size(page) > maximumBytes) throw new InvalidOperationException("An activity header exceeds the Runtime response limit.");
        return page;
    }

    internal static AgentTranscriptInvocationCursor Cursor(AgentTurnRecord turn, AgentTurnItemRecord item)
#if SUNDER_SUBAGENTS_APP || SUNDER_SUBAGENTS_WORKER
        => new() { CreatedAtUtc = turn.CreatedAtUtc, TurnId = turn.TurnId, SequenceNumber = item.SequenceNumber, ItemId = item.ItemId, TurnSequence = turn.TurnSequence };
#else
        => new(turn.CreatedAtUtc, turn.TurnId, item.SequenceNumber, item.ItemId) { TurnSequence = turn.TurnSequence };
#endif

    private static int Size<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions).Length;

    private static AgentCompactTranscriptEntry Copy(AgentCompactTranscriptEntry entry, AgentTurnRecord message)
#if SUNDER_SUBAGENTS_APP || SUNDER_SUBAGENTS_WORKER
        => new() { EntryId = entry.EntryId, CreatedAtUtc = entry.CreatedAtUtc, TurnId = entry.TurnId, Message = message, Group = entry.Group, TurnSequence = entry.TurnSequence };
#else
        => entry with { Message = message };
#endif

    private static AgentCompactTranscriptPage Copy(AgentCompactTranscriptPage page,
        IReadOnlyList<AgentCompactTranscriptEntry> entries, bool older, bool newer)
#if SUNDER_SUBAGENTS_APP || SUNDER_SUBAGENTS_WORKER
        => new() { Entries = entries, HasOlder = older, HasNewer = newer, AnchorEntryId = page.AnchorEntryId, TranscriptEpoch = page.TranscriptEpoch };
#else
        => page with { Entries = entries, HasOlder = older, HasNewer = newer };
#endif

    private static AgentTranscriptActivityMembersPage Members(IReadOnlyList<AgentTurnRecord> headers, bool more,
        AgentTranscriptInvocationCursor continuation)
#if SUNDER_SUBAGENTS_APP || SUNDER_SUBAGENTS_WORKER
        => new() { Headers = headers, HasMore = more, Continuation = continuation };
#else
        => new(headers, more, continuation);
#endif
}

// Numeric wire enums expose ValueN in generated consumer APIs. Keep that mapping
// at the boundary rather than spreading ordinal casts throughout the shared UI.
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class TranscriptCompactDirections
{
#if SUNDER_SUBAGENTS_APP || SUNDER_SUBAGENTS_WORKER
    internal const AgentCompactTranscriptDirection Recent = AgentCompactTranscriptDirection.Value0;
    internal const AgentCompactTranscriptDirection Before = AgentCompactTranscriptDirection.Value1;
    internal const AgentCompactTranscriptDirection After = AgentCompactTranscriptDirection.Value2;
    internal const AgentCompactTranscriptDirection Around = AgentCompactTranscriptDirection.Value3;
    internal const AgentCompactTranscriptDirection Window = AgentCompactTranscriptDirection.Value4;
#else
    internal const AgentCompactTranscriptDirection Recent = AgentCompactTranscriptDirection.Recent;
    internal const AgentCompactTranscriptDirection Before = AgentCompactTranscriptDirection.Before;
    internal const AgentCompactTranscriptDirection After = AgentCompactTranscriptDirection.After;
    internal const AgentCompactTranscriptDirection Around = AgentCompactTranscriptDirection.Around;
    internal const AgentCompactTranscriptDirection Window = AgentCompactTranscriptDirection.Window;
#endif
}
