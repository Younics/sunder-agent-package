using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.Runtime;

internal static class AgentRuntimePayloadLimits
{
    internal const int EnvelopeReserveBytes = 64 * 1024;

    internal static AgentTransportProfile StandardFallbackProfile => AgentTransportProfile.StandardFallback;

    internal const int MaximumProjectedTurnItems = 32;
    internal const int InitialProjectedTurnCharacterBudget = 512 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static int GetSerializedByteCount<T>(T value)
        => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions).Length;

    internal static int GetRequestPayloadBytes(AgentTransportProfile profile)
        => GetDirectPayloadBytes(profile.RequestValueBytes);

    internal static int GetResponsePayloadBytes(AgentTransportProfile profile)
        => GetDirectPayloadBytes(profile.ResponseValueBytes);

    internal static int GetEventPayloadBytes(AgentTransportProfile profile)
        => GetDirectPayloadBytes(profile.EventValueBytes);

    internal static long GetStreamQueueBytes(AgentTransportProfile profile)
        => checked((long)profile.EventValueBytes * 2);

    internal static long GetReplayQueueBytes(AgentTransportProfile profile)
        => checked((long)profile.EventValueBytes * 4);

    internal static void EnsureRequestFits<T>(T value, AgentTransportProfile profile)
    {
        if (GetSerializedByteCount(value) > GetRequestPayloadBytes(profile))
        {
            throw new InvalidOperationException("Agent request exceeds the negotiated Runtime value limit.");
        }
    }

    internal static void EnsureResponseFits<T>(T value, AgentTransportProfile profile)
    {
        if (GetSerializedByteCount(value) > GetResponsePayloadBytes(profile))
        {
            throw new AgentOwnerOperationException(
                "Agent response cannot be represented within the negotiated Runtime value limit.");
        }
    }

    internal static AgentTranscriptPage FitTranscriptPage(
        AgentTranscriptPage page,
        AgentTranscriptPageDirection direction,
        Guid? preferredItemId = null,
        int? maximumBytes = null)
    {
        maximumBytes ??= GetResponsePayloadBytes(StandardFallbackProfile);
        page = page with
        {
            Turns = page.Turns.Select(TranscriptTurnTransportProjection.ProjectToolHeaders).ToArray(),
            Continuation = page.Continuation ?? ResolveContinuation(page.Turns, direction),
        };
        if (GetSerializedByteCount(page) <= maximumBytes.Value)
        {
            return page;
        }

        var turns = page.Turns.ToList();
        while (turns.Count > 1)
        {
            if (direction is AgentTranscriptPageDirection.Recent or AgentTranscriptPageDirection.Before)
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
            if (GetSerializedByteCount(reduced) <= maximumBytes.Value)
            {
                return reduced;
            }
        }

        if (turns.Count == 1)
        {
            var characterBudget = InitialProjectedTurnCharacterBudget;
            while (true)
            {
                var projected = page with
                {
                    Turns = [TranscriptTurnTransportProjection.Project(
                        turns[0],
                        characterBudget,
                        MaximumProjectedTurnItems,
                        preferredItemId)],
                    HasMore = true,
                    Continuation = TranscriptPageCursor.FromTurn(turns[0]),
                };
                if (GetSerializedByteCount(projected) <= maximumBytes.Value)
                {
                    return projected;
                }

                if (characterBudget == 0)
                {
                    break;
                }
                characterBudget /= 2;
            }
        }

        throw new InvalidOperationException(
            "A cursor-bearing Agent transcript turn could not be projected below the Runtime response limit.");
    }

    internal static AgentTranscriptAroundTurnPage FitAroundTurnPage(
        AgentTranscriptAroundTurnPage page,
        Guid? preferredItemId = null,
        int? maximumBytes = null)
    {
        maximumBytes ??= GetResponsePayloadBytes(StandardFallbackProfile);
        page = page with
        {
            Turns = page.Turns.Select(TranscriptTurnTransportProjection.ProjectToolHeaders).ToArray(),
        };
        if (GetSerializedByteCount(page) <= maximumBytes.Value)
        {
            return page;
        }

        var turns = page.Turns.ToList();
        while (turns.Count > 1 && GetSerializedByteCount(page with { Turns = turns }) > maximumBytes.Value)
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
        if (GetSerializedByteCount(reduced) <= maximumBytes.Value)
        {
            return reduced;
        }
        var anchor = turns.FirstOrDefault(turn => turn.TurnId == page.AnchorTurnId);
        if (anchor is null)
        {
            throw new InvalidOperationException("Transcript anchor metadata exceeds the Runtime response limit.");
        }
        var projected = FitTranscriptPage(
            new AgentTranscriptPage(page.Revision, [anchor], HasMore: true),
            AgentTranscriptPageDirection.Turn,
            preferredItemId,
            maximumBytes);
        return page with
        {
            Turns = projected.Turns,
            HasOlder = true,
            HasNewer = true,
        };
    }

    internal static AgentRuntimeChange ProjectChange(AgentRuntimeChange change)
        => change with
        {
            // Change consumers invalidate and reload these records; carrying the full records only
            // duplicates dashboard data and can make one stream frame unbounded.
            Profile = null,
            Workspace = null,
            Turn = change.Turn is null
                ? null
                : TranscriptTurnTransportProjection.ProjectToolHeaders(change.Turn),
            TurnMutation = change.TurnMutation?.Turn is null
                ? change.TurnMutation
                : change.TurnMutation with
                {
                    Turn = TranscriptTurnTransportProjection.ProjectToolHeaders(change.TurnMutation.Turn),
                },
        };

    internal static AgentTranscriptToolDetailRecord FitToolDetail(
        AgentTranscriptToolDetailRecord detail,
        int? maximumBytes = null)
    {
        maximumBytes ??= GetResponsePayloadBytes(StandardFallbackProfile);
        if (GetSerializedByteCount(new AgentTranscriptToolDetailResult(Found: true, detail)) <= maximumBytes.Value)
        {
            return detail;
        }

        detail = detail with
        {
            SourcesJson = null,
            StructuredPayloadJson = null,
            WasTransportTruncated = true,
        };
        if (GetSerializedByteCount(new AgentTranscriptToolDetailResult(Found: true, detail)) <= maximumBytes.Value)
        {
            return detail;
        }

        detail = detail with { PresentationPayloadJson = null };
        if (GetSerializedByteCount(new AgentTranscriptToolDetailResult(Found: true, detail)) <= maximumBytes.Value)
        {
            return detail;
        }

        detail = detail with
        {
            OutputText = TruncateNullable(detail.OutputText, maximumBytes.Value / 4),
            ResultSummary = TruncateNullable(detail.ResultSummary, 2048),
        };
        if (GetSerializedByteCount(new AgentTranscriptToolDetailResult(Found: true, detail)) <= maximumBytes.Value)
        {
            return detail;
        }

        detail = detail with
        {
            ArgumentsJson = null,
            OutputText = TruncateNullable(detail.OutputText, 64 * 1024),
        };
        if (GetSerializedByteCount(new AgentTranscriptToolDetailResult(Found: true, detail)) <= maximumBytes.Value)
        {
            return detail;
        }

        throw new InvalidOperationException("Tool detail metadata exceeds the Runtime response limit.");
    }

    internal static bool FitsStreamEvent(AgentRuntimeChange change, int maximumBytes)
        => GetSerializedByteCount(change) <= maximumBytes;

    internal static AgentRuntimeChange ReplaceWithResnapshotIfOversized(
        AgentRuntimeChange change,
        int maximumBytes,
        params AgentRuntimeChange[] subscriberProjections)
        => subscriberProjections.All(projection => FitsStreamEvent(projection, maximumBytes))
            ? change
            : new AgentRuntimeChange(
                change.Revision,
                AgentRuntimeChangeKind.ResnapshotRequired,
                RuntimeInstanceId: change.RuntimeInstanceId);

    private static int GetDirectPayloadBytes(int valueBytes)
        => Math.Max(1, checked(valueBytes - EnvelopeReserveBytes));

    private static TranscriptPageCursor? ResolveContinuation(
        IReadOnlyList<AgentTurnRecord> turns,
        AgentTranscriptPageDirection direction)
    {
        var continuationTurn = direction is AgentTranscriptPageDirection.Recent
            or AgentTranscriptPageDirection.Before
            ? turns.FirstOrDefault()
            : turns.LastOrDefault();
        return continuationTurn is null
            ? null
            : TranscriptPageCursor.FromTurn(continuationTurn);
    }

    private static string? TruncateNullable(string? value, int maximumCharacters)
        => value is null || value.Length <= maximumCharacters ? value : value[..maximumCharacters];
}
