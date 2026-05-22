using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

public sealed partial class DefaultAgentBehaviorLoop
{
    private async Task<IReadOnlyList<ChatMessage>> BuildPromptMessagesAsync(
        IReadOnlyList<AgentTurnRecord> turns,
        Guid activeUserTurnId,
        bool useBoundedHistoricalWindow,
        AgentProviderRunCapabilities runCapabilities,
        Guid? excludedTurnId,
        CancellationToken cancellationToken)
    {
        if (excludedTurnId is not null)
        {
            turns = turns.Where(turn => turn.TurnId != excludedTurnId.Value).ToArray();
        }

        var promptTurns = BuildPromptTurns(turns, activeUserTurnId, useBoundedHistoricalWindow);
        var messages = new List<ChatMessage>(promptTurns.Count);
        foreach (var turn in promptTurns)
        {
            messages.Add(await BuildChatMessageAsync(turn, runCapabilities, cancellationToken).ConfigureAwait(false));
        }

        return MergeAdjacentToolMessages(messages);
    }

    private static IReadOnlyList<ChatMessage> MergeAdjacentToolMessages(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count < 2)
        {
            return messages;
        }

        var merged = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            if (merged.Count > 0 && CanMergeToolMessages(merged[^1], message))
            {
                foreach (var content in message.Contents)
                {
                    merged[^1].Contents.Add(content);
                }

                continue;
            }

            merged.Add(message);
        }

        return merged;
    }

    private static bool CanMergeToolMessages(ChatMessage left, ChatMessage right)
        => left.Role == right.Role
           && (left.Role == ChatRole.Assistant && HasOnlyFunctionCalls(left) && HasOnlyFunctionCalls(right)
               || left.Role == ChatRole.Tool && HasOnlyFunctionResults(left) && HasOnlyFunctionResults(right));

    private static bool HasOnlyFunctionCalls(ChatMessage message)
        => message.Contents.Count > 0 && message.Contents.All(content => content is FunctionCallContent);

    private static bool HasOnlyFunctionResults(ChatMessage message)
        => message.Contents.Count > 0 && message.Contents.All(content => content is FunctionResultContent);

    private static IReadOnlyList<AgentTurnRecord> BuildPromptTurns(
        IReadOnlyList<AgentTurnRecord> turns,
        Guid activeUserTurnId,
        bool useBoundedHistoricalWindow)
    {
        if (turns.Count == 0)
        {
            return turns;
        }

        if (!useBoundedHistoricalWindow)
        {
            var allIndexes = new SortedSet<int>(Enumerable.Range(0, turns.Count));
            var allPairedToolCallIds = CollectToolCallIds(turns, allIndexes);
            allPairedToolCallIds.IntersectWith(CollectToolResultIds(turns, allIndexes));
            return allIndexes
                .Select(index => RemoveOrphanToolItems(turns[index], allPairedToolCallIds))
                .Where(turn => turn.Items.Count > 0)
                .ToArray();
        }

        var activeTurnIndex = FindTurnIndex(turns, activeUserTurnId);
        if (activeTurnIndex <= 0)
        {
            return turns;
        }

        var historicalCount = Math.Min(activeTurnIndex, MaxHistoricalTurnsWithInstructionContext);
        var selectedIndexes = new SortedSet<int>(Enumerable.Range(activeTurnIndex - historicalCount, historicalCount + (turns.Count - activeTurnIndex)));
        var changed = true;
        while (changed)
        {
            changed = false;
            var includedToolCallIds = CollectToolCallIds(turns, selectedIndexes);
            var includedToolResultIds = CollectToolResultIds(turns, selectedIndexes);
            foreach (var index in selectedIndexes.ToArray())
            {
                foreach (var orphanedResult in turns[index].Items.Where(item => item.Kind == AgentTurnItemKind.ToolResult && !string.IsNullOrWhiteSpace(item.CallId)))
                {
                    if (includedToolCallIds.Contains(orphanedResult.CallId!))
                    {
                        continue;
                    }

                    var matchingCallIndex = FindMatchingToolCallIndex(turns, index, orphanedResult.CallId!);
                    if (matchingCallIndex >= 0 && selectedIndexes.Add(matchingCallIndex))
                    {
                        changed = true;
                    }
                }

                foreach (var orphanedCall in turns[index].Items.Where(item => item.Kind == AgentTurnItemKind.ToolCall && !string.IsNullOrWhiteSpace(item.CallId)))
                {
                    if (includedToolResultIds.Contains(orphanedCall.CallId!))
                    {
                        continue;
                    }

                    var matchingResultIndex = FindMatchingToolResultIndex(turns, index, orphanedCall.CallId!);
                    if (matchingResultIndex >= 0 && selectedIndexes.Add(matchingResultIndex))
                    {
                        changed = true;
                    }
                }
            }
        }

        var pairedToolCallIds = CollectToolCallIds(turns, selectedIndexes);
        pairedToolCallIds.IntersectWith(CollectToolResultIds(turns, selectedIndexes));
        return selectedIndexes
            .Select(index => RemoveOrphanToolItems(turns[index], pairedToolCallIds))
            .Where(turn => turn.Items.Count > 0)
            .ToArray();
    }

    private static HashSet<string> CollectToolCallIds(IReadOnlyList<AgentTurnRecord> turns, IEnumerable<int> indexes)
        => indexes
            .SelectMany(index => turns[index].Items)
            .Where(item => item.Kind == AgentTurnItemKind.ToolCall && !string.IsNullOrWhiteSpace(item.CallId))
            .Select(item => item.CallId!)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> CollectToolResultIds(IReadOnlyList<AgentTurnRecord> turns, IEnumerable<int> indexes)
        => indexes
            .SelectMany(index => turns[index].Items)
            .Where(item => item.Kind == AgentTurnItemKind.ToolResult && !string.IsNullOrWhiteSpace(item.CallId))
            .Select(item => item.CallId!)
            .ToHashSet(StringComparer.Ordinal);

    private static int FindMatchingToolCallIndex(IReadOnlyList<AgentTurnRecord> turns, int resultIndex, string callId)
    {
        for (var index = resultIndex - 1; index >= 0; index--)
        {
            if (turns[index].Items.Any(item => item.Kind == AgentTurnItemKind.ToolCall && string.Equals(item.CallId, callId, StringComparison.Ordinal)))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindMatchingToolResultIndex(IReadOnlyList<AgentTurnRecord> turns, int callIndex, string callId)
    {
        for (var index = callIndex + 1; index < turns.Count; index++)
        {
            if (turns[index].Items.Any(item => item.Kind == AgentTurnItemKind.ToolResult && string.Equals(item.CallId, callId, StringComparison.Ordinal)))
            {
                return index;
            }
        }

        return -1;
    }

    private static AgentTurnRecord RemoveOrphanToolItems(AgentTurnRecord turn, ISet<string> pairedToolCallIds)
    {
        var filteredItems = turn.Items
            .Where(item => item.Kind switch
            {
                AgentTurnItemKind.ToolCall or AgentTurnItemKind.ToolResult => !string.IsNullOrWhiteSpace(item.CallId) && pairedToolCallIds.Contains(item.CallId!),
                _ => true,
            })
            .ToArray();
        return filteredItems.Length == turn.Items.Count ? turn : turn with { Items = filteredItems };
    }

    private async Task<ChatMessage> BuildChatMessageAsync(
        AgentTurnRecord turn,
        AgentProviderRunCapabilities runCapabilities,
        CancellationToken cancellationToken)
    {
        var contents = new List<AIContent>();
        foreach (var item in turn.Items.OrderBy(item => item.SequenceNumber))
        {
            switch (item.Kind)
            {
                case AgentTurnItemKind.Text when !string.IsNullOrWhiteSpace(item.TextContent):
                    contents.Add(new TextContent(item.TextContent));
                    break;

                case AgentTurnItemKind.ToolCall when !string.IsNullOrWhiteSpace(item.CallId) && !string.IsNullOrWhiteSpace(item.ToolId):
                    contents.Add(new FunctionCallContent(
                        item.CallId,
                        item.ToolId,
                        ParseArguments(item.ArgumentsJson))
                    {
                        InformationalOnly = true,
                    });
                    break;

                case AgentTurnItemKind.ToolResult when !string.IsNullOrWhiteSpace(item.CallId):
                    contents.Add(new FunctionResultContent(item.CallId, BuildToolResultContent(item)));
                    break;

                case AgentTurnItemKind.Attachment:
                    contents.Add(await BuildAttachmentContentAsync(item, runCapabilities, cancellationToken).ConfigureAwait(false));
                    break;
            }
        }

        var message = new ChatMessage(ToChatRole(turn.Role), contents)
        {
            MessageId = turn.TurnId.ToString("N"),
            CreatedAt = turn.CreatedAtUtc,
        };
        return message;
    }

    private async Task<AIContent> BuildAttachmentContentAsync(
        AgentTurnItemRecord item,
        AgentProviderRunCapabilities runCapabilities,
        CancellationToken cancellationToken)
    {
        var metadata = TryReadAttachmentMetadata(item);
        if (metadata is null)
        {
            return new TextContent("[Attachment omitted: metadata is unavailable.]");
        }

        if (metadata.IsText)
        {
            return new TextContent(RenderTextAttachment(metadata, item.TextContent));
        }

        if (!SupportsAttachmentInput(runCapabilities, metadata.Kind))
        {
            return new TextContent(RenderUnsupportedAttachment(metadata));
        }

        if (_attachmentStore is null)
        {
            return new TextContent($"[Attachment omitted: {metadata.FileName} could not be loaded because attachment storage is unavailable.]");
        }

        try
        {
            var bytes = await _attachmentStore.ReadAttachmentBytesAsync(metadata, cancellationToken).ConfigureAwait(false);
            return new DataContent(bytes, metadata.MediaType)
            {
                Name = metadata.FileName,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new TextContent($"[Attachment omitted: {metadata.FileName} could not be loaded from local storage ({ex.Message}).]");
        }
    }

    private static AgentAttachmentMetadata? TryReadAttachmentMetadata(AgentTurnItemRecord item)
    {
        if (string.IsNullOrWhiteSpace(item.StructuredPayloadJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AgentAttachmentMetadata>(item.StructuredPayloadJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string RenderTextAttachment(AgentAttachmentMetadata metadata, string? textContent)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Attached file: {metadata.FileName} ({metadata.MediaType}, {FormatByteCount(metadata.SizeBytes)})");
        if (metadata.WasTruncated)
        {
            builder.AppendLine($"Only the first {AgentAttachmentService.MaxTextAttachmentCharacters:N0} characters are included.");
        }

        builder.AppendLine();
        builder.AppendLine("```text");
        builder.AppendLine(textContent ?? string.Empty);
        builder.AppendLine("```");
        return builder.ToString().TrimEnd();
    }

    private static string RenderUnsupportedAttachment(AgentAttachmentMetadata metadata)
        => $"Attached file '{metadata.FileName}' ({metadata.MediaType}, {FormatByteCount(metadata.SizeBytes)}) was provided, but the selected model/provider does not support {DescribeAttachmentKind(metadata.Kind)} input in Sunder. Ask the user to switch to a model that supports this input type or provide the content as text.";

    private static bool SupportsAttachmentInput(AgentProviderRunCapabilities capabilities, AgentAttachmentKind kind)
        => kind switch
        {
            AgentAttachmentKind.Image => capabilities.SupportsImageInput,
            AgentAttachmentKind.Pdf => capabilities.SupportsPdfInput,
            AgentAttachmentKind.Audio => capabilities.SupportsAudioInput,
            AgentAttachmentKind.Video => capabilities.SupportsVideoInput,
            _ => false,
        };

    private static string DescribeAttachmentKind(AgentAttachmentKind kind)
        => kind switch
        {
            AgentAttachmentKind.Image => "image",
            AgentAttachmentKind.Pdf => "PDF",
            AgentAttachmentKind.Audio => "audio",
            AgentAttachmentKind.Video => "video",
            _ => "binary file",
        };

    private static string FormatByteCount(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / 1024d / 1024d:0.#} MB"
            : bytes >= 1024
                ? $"{bytes / 1024d:0.#} KB"
                : $"{bytes} B";

    private static ChatRole ToChatRole(AgentMessageRole role)
        => role switch
        {
            AgentMessageRole.System => ChatRole.System,
            AgentMessageRole.Assistant => ChatRole.Assistant,
            AgentMessageRole.Tool => ChatRole.Tool,
            _ => ChatRole.User,
        };

    private static IDictionary<string, object?> ParseArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["value"] = document.RootElement.Clone(),
                };
            }

            return document.RootElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["value"] = argumentsJson,
            };
        }
    }

    private static string BuildToolResultContent(AgentTurnItemRecord item)
        => !string.IsNullOrWhiteSpace(item.StructuredPayloadJson)
            ? item.StructuredPayloadJson
            : !string.IsNullOrWhiteSpace(item.TextContent)
                ? item.TextContent
                : item.ResultSummary ?? string.Empty;

    private static int FindTurnIndex(IReadOnlyList<AgentTurnRecord> turns, Guid turnId)
    {
        for (var index = 0; index < turns.Count; index++)
        {
            if (turns[index].TurnId == turnId)
            {
                return index;
            }
        }

        return -1;
    }
}
