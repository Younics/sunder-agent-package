using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;

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

    private const int MaximumProjectedTurnItems = 32;
    private const int InitialProjectedTurnCharacterBudget = 512 * 1024;
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
        AgentTranscriptPageDirection direction)
    {
        if (GetSerializedByteCount(page) <= MaximumOperationResponseBytes)
        {
            return page;
        }

        var turns = page.Turns.ToList();
        var wasReduced = false;
        while (turns.Count > 1)
        {
            wasReduced = true;
            if (direction is AgentTranscriptPageDirection.Recent or AgentTranscriptPageDirection.Before)
            {
                turns.RemoveAt(0);
            }
            else
            {
                turns.RemoveAt(turns.Count - 1);
            }

            var reduced = page with { Turns = turns.ToArray(), HasMore = true };
            if (GetSerializedByteCount(reduced) <= MaximumOperationResponseBytes)
            {
                return reduced;
            }
        }

        if (turns.Count == 1)
        {
            var characterBudget = InitialProjectedTurnCharacterBudget;
            while (characterBudget > 0)
            {
                var projected = page with
                {
                    Turns = [ProjectTurn(turns[0], characterBudget)],
                    HasMore = true,
                };
                if (GetSerializedByteCount(projected) <= MaximumOperationResponseBytes)
                {
                    return projected;
                }

                characterBudget /= 2;
            }
        }

        var empty = page with { Turns = [], HasMore = page.HasMore || wasReduced || page.Turns.Count > 0 };
        if (GetSerializedByteCount(empty) > MaximumOperationResponseBytes)
        {
            throw new InvalidOperationException("Agent transcript metadata exceeds the Runtime response limit.");
        }

        return empty;
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
        };

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

    private static AgentTurnRecord ProjectTurn(AgentTurnRecord turn, int characterBudget)
    {
        var remaining = characterBudget;
        var omittedItems = turn.Items.Count > MaximumProjectedTurnItems;
        var projectedItems = new List<AgentTurnItemRecord>(Math.Min(
            turn.Items.Count,
            MaximumProjectedTurnItems));
        foreach (var item in turn.Items
                     .OrderBy(item => item.SequenceNumber)
                     .Take(MaximumProjectedTurnItems))
        {
            var truncated = omittedItems || item.WasTruncated;
            var text = TakeText(item.TextContent, ref remaining, ref truncated);
            var callId = TakeText(item.CallId, ref remaining, ref truncated, 1024);
            var toolId = TakeText(item.ToolId, ref remaining, ref truncated, 1024);
            var arguments = TakeJson(item.ArgumentsJson, ref remaining, ref truncated);
            var resultSummary = TakeText(item.ResultSummary, ref remaining, ref truncated);
            var structuredPayload = TakeJson(item.StructuredPayloadJson, ref remaining, ref truncated);
            var sources = TakeJson(item.SourcesJson, ref remaining, ref truncated);
            var errorCode = TakeText(item.ErrorCode, ref remaining, ref truncated, 256);
            var backendId = TakeText(item.BackendId, ref remaining, ref truncated, 512);
            var presentationPayload = TakeJson(item.PresentationPayloadJson, ref remaining, ref truncated);
            projectedItems.Add(item with
            {
                TextContent = text,
                CallId = callId,
                ToolId = toolId,
                ArgumentsJson = arguments,
                ResultSummary = resultSummary,
                StructuredPayloadJson = structuredPayload,
                SourcesJson = sources,
                WasTruncated = truncated,
                ErrorCode = errorCode,
                BackendId = backendId,
                PresentationPayloadJson = presentationPayload,
            });
        }

        return turn with { Items = projectedItems };
    }

    private static string? TakeText(
        string? value,
        ref int remaining,
        ref bool truncated,
        int maximumCharacters = int.MaxValue)
    {
        if (value is null)
        {
            return null;
        }

        var take = Math.Min(value.Length, Math.Min(remaining, maximumCharacters));
        remaining -= take;
        if (take < value.Length)
        {
            truncated = true;
        }

        return take == value.Length ? value : value[..take];
    }

    private static string? TakeJson(string? value, ref int remaining, ref bool truncated)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length > remaining)
        {
            truncated = true;
            return null;
        }

        remaining -= value.Length;
        return value;
    }

    private static string Truncate(string value, int maximumCharacters)
        => value.Length <= maximumCharacters ? value : value[..maximumCharacters];

    private static string? TruncateNullable(string? value, int maximumCharacters)
        => value is null ? null : Truncate(value, maximumCharacters);
}
