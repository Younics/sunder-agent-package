using System.Text;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal static class TranscriptTurnTransportProjection
{
    internal const string ContentTruncatedNotice =
        "[Content truncated for display because this turn exceeds the Runtime response limit. "
        + "The durable turn is preserved; use older/newer transcript paging to continue.]";

    private const int MaximumHeaderCharacters = 240;

    internal static AgentTurnRecord ProjectToolHeaders(AgentTurnRecord turn)
    {
        if (turn.Kind is not (AgentTurnKind.ToolCall or AgentTurnKind.ToolResult))
        {
            return turn;
        }

        var revision = CreateDetailRevision(turn);
        return turn with
        {
            Items = turn.Items.Select(item => item.Kind is AgentTurnItemKind.ToolCall or AgentTurnItemKind.ToolResult
                ? ProjectToolHeader(item, revision)
                : item).ToArray(),
        };
    }

    internal static long CreateDetailRevision(AgentTurnRecord turn)
        // Unix microseconds retain timestamp ordering while staying within the RPC safe-integer bound.
        => Math.Max(1, (turn.UpdatedAtUtc.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks) / 10);

    internal static bool HasToolDetails(AgentTurnItemRecord item)
        => item.ToolHasDetails
           ?? item.ToolExecutionStatus == AgentToolExecutionStatus.Ambiguous
           || item.IsError
           || !IsEmptyArguments(item.ArgumentsJson)
           || !string.IsNullOrWhiteSpace(item.TextContent)
           || !string.IsNullOrWhiteSpace(item.ResultSummary)
           || !string.IsNullOrWhiteSpace(item.StructuredPayloadJson)
           || !string.IsNullOrWhiteSpace(item.SourcesJson)
           || !string.IsNullOrWhiteSpace(item.PresentationPayloadJson)
           || !string.IsNullOrWhiteSpace(item.ErrorCode)
           || !string.IsNullOrWhiteSpace(item.BackendId);

    internal static AgentTurnRecord Project(
        AgentTurnRecord turn,
        int characterBudget,
        int maximumItems,
        Guid? preferredItemId = null)
    {
        turn = ProjectToolHeaders(turn);
        var remaining = Math.Max(0, characterBudget);
        var orderedItems = turn.Items.OrderBy(item => item.SequenceNumber).ToArray();
        var selectedItems = orderedItems.Take(maximumItems).ToList();
        if (preferredItemId is { } itemId
            && selectedItems.All(item => item.ItemId != itemId)
            && orderedItems.FirstOrDefault(item => item.ItemId == itemId) is { } preferredItem)
        {
            if (selectedItems.Count == maximumItems)
            {
                selectedItems.RemoveAt(selectedItems.Count - 1);
            }
            selectedItems.Add(preferredItem);
            selectedItems.Sort(static (left, right) => left.SequenceNumber.CompareTo(right.SequenceNumber));
        }

        var itemsOmitted = orderedItems.Length > selectedItems.Count;
        var projectedItems = new List<AgentTurnItemRecord>(selectedItems.Count);
        foreach (var item in selectedItems)
        {
            projectedItems.Add(ProjectItem(item, ref remaining, itemsOmitted));
        }
        return turn with { Items = projectedItems };
    }

    private static AgentTurnItemRecord ProjectItem(
        AgentTurnItemRecord item,
        ref int remaining,
        bool itemsOmitted)
    {
        if (item.Kind is AgentTurnItemKind.ToolCall or AgentTurnItemKind.ToolResult)
        {
            return item;
        }

        var text = TakeText(item.TextContent, ref remaining);
        var callId = TakeText(item.CallId, ref remaining, 1024);
        var toolId = TakeText(item.ToolId, ref remaining, 1024);
        var arguments = TakeJson(item.ArgumentsJson, ref remaining);
        var resultSummary = TakeText(item.ResultSummary, ref remaining);
        var structuredPayload = TakeJson(item.StructuredPayloadJson, ref remaining);
        var sources = TakeJson(item.SourcesJson, ref remaining);
        var errorCode = TakeText(item.ErrorCode, ref remaining, 256);
        var backendId = TakeText(item.BackendId, ref remaining, 512);
        var presentationPayload = TakeJson(item.PresentationPayloadJson, ref remaining);
        var toolOwnerPackageId = TakeText(item.ToolOwnerPackageId, ref remaining, 512);
        var toolSchemaId = TakeText(item.ToolSchemaId, ref remaining, 512);
        var toolSchemaVersion = TakeText(item.ToolSchemaVersion, ref remaining, 128);
        var transportTruncated = itemsOmitted
                                 || text != item.TextContent
                                 || callId != item.CallId
                                 || toolId != item.ToolId
                                 || arguments != item.ArgumentsJson
                                 || resultSummary != item.ResultSummary
                                 || structuredPayload != item.StructuredPayloadJson
                                 || sources != item.SourcesJson
                                 || errorCode != item.ErrorCode
                                 || backendId != item.BackendId
                                 || presentationPayload != item.PresentationPayloadJson
                                 || toolOwnerPackageId != item.ToolOwnerPackageId
                                 || toolSchemaId != item.ToolSchemaId
                                 || toolSchemaVersion != item.ToolSchemaVersion;
        if (transportTruncated)
        {
            if (item.Kind is AgentTurnItemKind.ToolCall or AgentTurnItemKind.ToolResult)
            {
                resultSummary = AppendNotice(resultSummary);
            }
            else
            {
                text = AppendNotice(text);
            }
        }

        return item with
        {
            TextContent = text,
            CallId = callId,
            ToolId = toolId,
            ArgumentsJson = arguments,
            ResultSummary = resultSummary,
            StructuredPayloadJson = structuredPayload,
            SourcesJson = sources,
            WasTruncated = item.WasTruncated || transportTruncated,
            ErrorCode = errorCode,
            BackendId = backendId,
            PresentationPayloadJson = presentationPayload,
            ToolOwnerPackageId = toolOwnerPackageId,
            ToolSchemaId = toolSchemaId,
            ToolSchemaVersion = toolSchemaVersion,
        };
    }

    private static AgentTurnItemRecord ProjectToolHeader(AgentTurnItemRecord item, long revision)
    {
        if (item.IsToolHeaderProjection)
        {
            return item;
        }

        var hasDetails = HasToolDetails(item);
        var isFailure = item.IsError
                        || item.ToolExecutionStatus is AgentToolExecutionStatus.Failed
                            or AgentToolExecutionStatus.Ambiguous;
        var summary = CompactHeader(FirstNonBlank(item.ResultSummary, isFailure ? item.TextContent : null));
        return item with
        {
            TextContent = null,
            CallId = Bound(item.CallId, 1024),
            ToolId = Bound(item.ToolId, 1024),
            ArgumentsJson = null,
            ResultSummary = null,
            StructuredPayloadJson = null,
            SourcesJson = null,
            ErrorCode = null,
            BackendId = null,
            PresentationPayloadJson = null,
            ToolOwnerPackageId = null,
            ToolSchemaId = null,
            ToolSchemaVersion = null,
            ToolHeaderHint = isFailure ? null : summary,
            ToolErrorSummary = isFailure ? summary ?? CompactHeader(item.ErrorCode) : null,
            ToolDetailRevision = revision,
            ToolHasDetails = hasDetails,
            IsToolHeaderProjection = true,
        };
    }

    private static bool IsEmptyArguments(string? value)
        => string.IsNullOrWhiteSpace(value)
           || string.Equals(value.Trim(), "{}", StringComparison.Ordinal);

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? Bound(string? value, int maximumCharacters)
        => value is null || value.Length <= maximumCharacters
            ? value
            : value[..maximumCharacters];

    private static string? CompactHeader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(value.Length, MaximumHeaderCharacters + 3));
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace && builder.Length < MaximumHeaderCharacters)
            {
                builder.Append(' ');
            }
            pendingSpace = false;
            if (builder.Length >= MaximumHeaderCharacters)
            {
                break;
            }
            builder.Append(character);
        }

        var result = builder.ToString().Trim();
        return value.Length > result.Length && result.Length > 0 ? result + "..." : result;
    }

    private static string AppendNotice(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? ContentTruncatedNotice
            : $"{value}\n\n{ContentTruncatedNotice}";

    private static string? TakeText(
        string? value,
        ref int remaining,
        int maximumCharacters = int.MaxValue)
    {
        if (value is null)
        {
            return null;
        }

        var take = Math.Min(value.Length, Math.Min(remaining, maximumCharacters));
        remaining -= take;
        return take == value.Length ? value : value[..take];
    }

    private static string? TakeJson(string? value, ref int remaining)
    {
        if (value is null)
        {
            return null;
        }
        if (value.Length > remaining)
        {
            return null;
        }

        remaining -= value.Length;
        return value;
    }
}
