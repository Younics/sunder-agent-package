using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.Runtime;

internal static class AgentRuntimePayloadLimits
{
    // These mirror Core's RuntimePackageOperationPolicyOptions defaults.
    internal const int RuntimeMaximumRequestBytes = 1024 * 1024;
    internal const int RuntimeMaximumResponseBytes = 4 * 1024 * 1024;
    internal const int RuntimeMaximumEventBytes = 1024 * 1024;

    internal const int MaximumOperationRequestBytes = RuntimeMaximumRequestBytes - (32 * 1024);
    internal const int MaximumOperationResponseBytes = RuntimeMaximumResponseBytes - (64 * 1024);
    internal const int MaximumStreamEventBytes = RuntimeMaximumEventBytes - (32 * 1024);
    internal const int AttachmentUploadChunkBytes = 512 * 1024;
    internal const int AttachmentDownloadChunkBytes = 2 * 1024 * 1024;
    internal const int MaximumRunMessageCharacters = 256 * 1024;

    internal const int MaximumProjectedTurnItems = 32;
    internal const int InitialProjectedTurnCharacterBudget = 512 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static int GetSerializedByteCount<T>(T value)
        => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions).Length;

    internal static void EnsureRunCommandFits(AgentRunCommand command)
    {
        if (GetSerializedByteCount(command) > MaximumOperationRequestBytes)
        {
            throw new InvalidOperationException("Agent run request exceeds the Runtime transport limit.");
        }
    }

    internal static AgentTranscriptPage FitTranscriptPage(
        AgentTranscriptPage page,
        AgentTranscriptPageDirection direction,
        Guid? preferredItemId = null,
        int maximumBytes = MaximumOperationResponseBytes)
    {
        page = page with
        {
            Turns = page.Turns.Select(TranscriptTurnTransportProjection.ProjectToolHeaders).ToArray(),
            Continuation = page.Continuation ?? ResolveContinuation(page.Turns, direction),
        };
        if (GetSerializedByteCount(page) <= maximumBytes)
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
            if (GetSerializedByteCount(reduced) <= maximumBytes)
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
                if (GetSerializedByteCount(projected) <= maximumBytes)
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
        Guid? preferredItemId = null)
    {
        page = page with
        {
            Turns = page.Turns.Select(TranscriptTurnTransportProjection.ProjectToolHeaders).ToArray(),
        };
        if (GetSerializedByteCount(page) <= MaximumOperationResponseBytes)
        {
            return page;
        }

        var turns = page.Turns.ToList();
        while (turns.Count > 1 && GetSerializedByteCount(page with { Turns = turns }) > MaximumOperationResponseBytes)
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
        if (GetSerializedByteCount(reduced) <= MaximumOperationResponseBytes)
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
            preferredItemId);
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
            Session = change.Session is null
                ? null
                : change.Session with
                {
                    Session = change.Session.Session with
                    {
                        Title = Truncate(change.Session.Session.Title, 512),
                        AgentKind = TruncateNullable(change.Session.Session.AgentKind, 128),
                    },
                    Checkpoint = change.Session.Checkpoint is null
                        ? null
                        : change.Session.Checkpoint with
                        {
                            Summary = TruncateNullable(change.Session.Checkpoint.Summary, 2048),
                        },
                },
            RunActivity = change.RunActivity is null
                ? null
                : change.RunActivity with { Text = Truncate(change.RunActivity.Text, 16 * 1024) },
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
        int maximumBytes = MaximumOperationResponseBytes)
    {
        if (GetSerializedByteCount(new AgentTranscriptToolDetailResponse(detail)) <= maximumBytes)
        {
            return detail;
        }

        detail = detail with
        {
            SourcesJson = null,
            StructuredPayloadJson = null,
            WasTransportTruncated = true,
        };
        if (GetSerializedByteCount(new AgentTranscriptToolDetailResponse(detail)) <= maximumBytes)
        {
            return detail;
        }

        detail = detail with { PresentationPayloadJson = null };
        if (GetSerializedByteCount(new AgentTranscriptToolDetailResponse(detail)) <= maximumBytes)
        {
            return detail;
        }

        detail = detail with
        {
            OutputText = TruncateNullable(detail.OutputText, maximumBytes / 4),
            ResultSummary = TruncateNullable(detail.ResultSummary, 2048),
        };
        if (GetSerializedByteCount(new AgentTranscriptToolDetailResponse(detail)) <= maximumBytes)
        {
            return detail;
        }

        detail = detail with
        {
            ArgumentsJson = null,
            OutputText = TruncateNullable(detail.OutputText, 64 * 1024),
        };
        if (GetSerializedByteCount(new AgentTranscriptToolDetailResponse(detail)) <= maximumBytes)
        {
            return detail;
        }

        throw new InvalidOperationException("Tool detail metadata exceeds the Runtime response limit.");
    }

    internal static bool FitsStreamEvent(AgentRuntimeChange change)
        => GetSerializedByteCount(change) <= MaximumStreamEventBytes;

    internal static AgentRuntimeChange ReplaceWithResnapshotIfOversized(
        AgentRuntimeChange change,
        params AgentRuntimeChange[] subscriberProjections)
        => subscriberProjections.All(FitsStreamEvent)
            ? change
            : new AgentRuntimeChange(
                change.Revision,
                AgentRuntimeChangeKind.ResnapshotRequired,
                RuntimeInstanceId: change.RuntimeInstanceId);

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

    private static string Truncate(string value, int maximumCharacters)
        => value.Length <= maximumCharacters ? value : value[..maximumCharacters];

    private static string? TruncateNullable(string? value, int maximumCharacters)
        => value is null ? null : Truncate(value, maximumCharacters);
}
