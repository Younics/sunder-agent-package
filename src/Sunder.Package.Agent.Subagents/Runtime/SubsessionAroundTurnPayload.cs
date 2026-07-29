using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.Subagents.Runtime;

internal static class SubsessionAroundTurnPayload
{
    internal const int MaximumResponseBytes = (4 * 1024 * 1024) - (64 * 1024);
    internal const int MaximumEventBytes = (1024 * 1024) - (32 * 1024);
    private const int MaximumTurnItems = 32;

    internal static SubsessionTranscriptPage FitPage(
        SubsessionTranscriptPage page,
        SubagentQueryKind direction)
    {
        page = page with
        {
            Turns = page.Turns.Select(TranscriptTurnTransportProjection.ProjectToolHeaders).ToArray(),
            Continuation = page.Continuation ?? ResolveContinuation(page.Turns, direction),
        };
        if (Size(page) <= MaximumResponseBytes)
        {
            return page;
        }

        var turns = page.Turns.ToList();
        while (turns.Count > 1)
        {
            if (direction is SubagentQueryKind.RecentTurns or SubagentQueryKind.TurnsBefore)
            {
                turns.RemoveAt(0);
            }
            else
            {
                turns.RemoveAt(turns.Count - 1);
            }

            var reduced = page with
            {
                Turns = turns.ToArray(),
                HasMore = true,
                Continuation = ResolveContinuation(turns, direction),
            };
            if (Size(reduced) <= MaximumResponseBytes)
            {
                return reduced;
            }
        }

        if (turns.Count == 1)
        {
            for (var budget = 512 * 1024; ; budget /= 2)
            {
                var projected = page with
                {
                    Turns = [TranscriptTurnTransportProjection.Project(
                        turns[0],
                        budget,
                        MaximumTurnItems)],
                    HasMore = true,
                    Continuation = TranscriptPageCursor.FromTurn(turns[0]),
                };
                if (Size(projected) <= MaximumResponseBytes)
                {
                    return projected;
                }
                if (budget == 0)
                {
                    break;
                }
            }
        }

        throw new InvalidOperationException(
            "Subsession transcript data exceeds the Runtime response limit.");
    }

    internal static SubagentChanged FitChange(SubagentChanged change)
    {
        change = change with
        {
            Turn = change.Turn is null
                ? null
                : TranscriptTurnTransportProjection.ProjectToolHeaders(change.Turn),
        };
        return Size(change) <= MaximumEventBytes
            ? change
            : new SubagentChanged(
                change.Revision,
                SubagentChangeKind.ResnapshotRequired,
                RuntimeInstanceId: change.RuntimeInstanceId);
    }

    internal static SubsessionAroundTurnPage Fit(
        SubsessionAroundTurnPage page,
        Guid preferredItemId)
    {
        page = page with
        {
            Turns = page.Turns.Select(TranscriptTurnTransportProjection.ProjectToolHeaders).ToArray(),
        };
        if (Size(page) <= MaximumResponseBytes)
        {
            return page;
        }

        var turns = page.Turns.ToList();
        while (turns.Count > 1 && Size(page with { Turns = turns }) > MaximumResponseBytes)
        {
            var anchorIndex = turns.FindIndex(turn => turn.TurnId == page.AnchorTurnId);
            if (anchorIndex < 0)
            {
                break;
            }
            var olderDistance = anchorIndex;
            var newerDistance = turns.Count - anchorIndex - 1;
            if (newerDistance >= olderDistance && newerDistance > 0)
            {
                turns.RemoveAt(turns.Count - 1);
                page = page with { HasNewer = true };
            }
            else if (olderDistance > 0)
            {
                turns.RemoveAt(0);
                page = page with { HasOlder = true };
            }
            else
            {
                break;
            }
        }

        var reduced = page with { Turns = turns };
        if (Size(reduced) <= MaximumResponseBytes)
        {
            return reduced;
        }
        var anchor = turns.FirstOrDefault(turn => turn.TurnId == page.AnchorTurnId)
                     ?? throw new InvalidOperationException("Transcript anchor metadata exceeds the Runtime response limit.");
        for (var budget = 512 * 1024; budget > 0; budget /= 2)
        {
            var projected = page with
            {
                Turns = [TranscriptTurnTransportProjection.Project(
                    anchor,
                    budget,
                    MaximumTurnItems,
                    preferredItemId)],
                HasOlder = true,
                HasNewer = true,
            };
            if (Size(projected) <= MaximumResponseBytes)
            {
                return projected;
            }
        }
        throw new InvalidOperationException("Transcript anchor metadata exceeds the Runtime response limit.");
    }

    internal static AgentTranscriptToolDetailRecord FitToolDetail(
        AgentTranscriptToolDetailRecord detail)
    {
        if (Size(new SubagentProjection(ToolDetail: detail)) <= MaximumResponseBytes)
        {
            return detail;
        }

        detail = detail with
        {
            SourcesJson = null,
            StructuredPayloadJson = null,
            WasTransportTruncated = true,
        };
        if (Size(new SubagentProjection(ToolDetail: detail)) <= MaximumResponseBytes)
        {
            return detail;
        }

        detail = detail with { PresentationPayloadJson = null };
        if (Size(new SubagentProjection(ToolDetail: detail)) <= MaximumResponseBytes)
        {
            return detail;
        }

        detail = detail with
        {
            OutputText = Truncate(detail.OutputText, MaximumResponseBytes / 4),
            ResultSummary = Truncate(detail.ResultSummary, 2048),
        };
        if (Size(new SubagentProjection(ToolDetail: detail)) <= MaximumResponseBytes)
        {
            return detail;
        }

        detail = detail with
        {
            ArgumentsJson = null,
            OutputText = Truncate(detail.OutputText, 64 * 1024),
        };
        return Size(new SubagentProjection(ToolDetail: detail)) <= MaximumResponseBytes
            ? detail
            : throw new InvalidOperationException("Tool detail metadata exceeds the Runtime response limit.");
    }

    private static TranscriptPageCursor? ResolveContinuation(
        IReadOnlyList<AgentTurnRecord> turns,
        SubagentQueryKind direction)
    {
        var continuationTurn = direction is SubagentQueryKind.RecentTurns
            or SubagentQueryKind.TurnsBefore
            ? turns.FirstOrDefault()
            : turns.LastOrDefault();
        return continuationTurn is null
            ? null
            : TranscriptPageCursor.FromTurn(continuationTurn);
    }

    internal static int Size<T>(T page)
        => JsonSerializer.SerializeToUtf8Bytes(page).Length;

    private static string? Truncate(string? value, int maximumCharacters)
        => value is null || value.Length <= maximumCharacters
            ? value
            : value[..maximumCharacters];
}
