using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal sealed class AgentPromptPreparationPipeline(
    AgentSystemPromptComposer promptComposer,
    IAgentAttachmentContentStore? attachmentStore = null,
    AgentSessionContextProjectionService? sessionContextProjectionService = null)
{
    private const int MaxHistoricalTurnsWithInstructionContext = 16;
    private const int MaxPromptContextTurns = 64;
    private readonly AgentSystemPromptComposer _promptComposer = promptComposer;
    private readonly IAgentAttachmentContentStore? _attachmentStore = attachmentStore;
    private readonly AgentSessionContextProjectionService? _sessionContextProjectionService = sessionContextProjectionService;

    public async Task<AgentPromptPreparation> PrepareAsync(
        IAgentBehaviorLoopRuntime host,
        AgentBehaviorLoopContext context,
        CancellationToken cancellationToken)
    {
        var projection = BuildPromptProjection(host, context, promptOverheadTokens: 0);
        var instructionContext = await host.BuildInstructionContextAsync(cancellationToken);
        var runtimeTools = context.RunCapabilities.SupportsNativeToolCalling
            ? await host.ListReadyToolsAsync(cancellationToken)
            : [];
        var availableTools = runtimeTools.Select(tool => tool.Descriptor).ToArray();
        var promptRequest = CreatePromptRequest(context, availableTools, projection.PromptTurns);
        var promptStopwatch = Stopwatch.StartNew();
        host.LogEvent(AgentLogLevel.Debug, "system_prompt.compose.start", "Composing system prompt.");
        var systemInstructions = await _promptComposer.ComposeAsync(
            promptRequest,
            instructionContext.SystemInstructions,
            cancellationToken);
        var promptOverheadTokens = EstimatePromptOverheadTokens(systemInstructions, availableTools);
        projection = BuildPromptProjection(host, context, promptOverheadTokens);

        if (projection.SummaryUpdated)
        {
            instructionContext = await host.BuildInstructionContextAsync(cancellationToken);
            promptRequest = promptRequest with { Turns = projection.PromptTurns };
            systemInstructions = await _promptComposer.ComposeAsync(
                promptRequest,
                instructionContext.SystemInstructions,
                cancellationToken);
            promptOverheadTokens = EstimatePromptOverheadTokens(systemInstructions, availableTools);
            projection = BuildPromptProjection(host, context, promptOverheadTokens);
        }

        LogPromptCompleted(
            host,
            context,
            systemInstructions,
            instructionContext,
            availableTools.Length,
            promptStopwatch.ElapsedMilliseconds);
        return new AgentPromptPreparation(
            runtimeTools,
            availableTools,
            ShouldAllowMultipleToolCalls(context, availableTools),
            promptRequest,
            projection,
            systemInstructions,
            promptOverheadTokens);
    }

    public Task<IReadOnlyList<ChatMessage>> BuildProviderMessagesAsync(
        AgentPromptPreparation preparation,
        AgentBehaviorLoopContext context,
        CancellationToken cancellationToken)
        => BuildPromptMessagesAsync(
            preparation.Projection.PromptTurns,
            context.UserTurnId,
            useBoundedHistoricalWindow: _sessionContextProjectionService is null,
            context.RunCapabilities,
            cancellationToken);

    public async Task RefreshAfterToolCycleAsync(
        IAgentBehaviorLoopRuntime host,
        AgentBehaviorLoopContext context,
        AgentPromptPreparation preparation,
        CancellationToken cancellationToken)
    {
        preparation.Projection = BuildPromptProjection(
            host,
            context,
            preparation.PromptOverheadTokens);
        if (!preparation.Projection.SummaryUpdated)
        {
            return;
        }

        var instructionContext = await host.BuildInstructionContextAsync(cancellationToken);
        preparation.PromptRequest = preparation.PromptRequest with
        {
            Turns = preparation.Projection.PromptTurns,
        };
        preparation.SystemInstructions = await _promptComposer.ComposeAsync(
            preparation.PromptRequest,
            instructionContext.SystemInstructions,
            cancellationToken);
        preparation.PromptOverheadTokens = EstimatePromptOverheadTokens(
            preparation.SystemInstructions,
            preparation.AvailableTools);
        preparation.Projection = BuildPromptProjection(
            host,
            context,
            preparation.PromptOverheadTokens);
    }

    private static AgentSystemPromptRequest CreatePromptRequest(
        AgentBehaviorLoopContext context,
        IReadOnlyList<AgentToolDescriptor> availableTools,
        IReadOnlyList<AgentTurnRecord> turns)
        => new(
            context.Session,
            context.Profile,
            context.ProviderId,
            context.ModelId,
            context.RunCapabilities,
            context.Workspace,
            context.ExecutionBinding,
            availableTools,
            turns,
            context.RunId,
            context.RunRevision,
            context.RunStartedAtUtc,
            context.UserMessage);

    private static void LogPromptCompleted(
        IAgentBehaviorLoopRuntime host,
        AgentBehaviorLoopContext context,
        string? systemInstructions,
        AgentBehaviorInstructionContext instructionContext,
        int availableToolCount,
        long elapsedMilliseconds)
        => host.LogEvent(
            AgentLogLevel.Debug,
            "system_prompt.compose.completed",
            $"{systemInstructions?.Length ?? 0} characters",
            elapsedMilliseconds,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["system_prompt.length"] = systemInstructions?.Length ?? 0,
                ["system_prompt.base_instruction_length"] = instructionContext.SystemInstructions?.Length ?? 0,
                ["tool.available_count"] = availableToolCount,
                ["workspace.id"] = context.Workspace?.WorkspaceId,
                ["workspace.binding_id"] = context.ExecutionBinding?.BindingId,
            });

    private AgentSessionPromptProjection BuildPromptProjection(
        IAgentBehaviorLoopRuntime host,
        AgentBehaviorLoopContext context,
        int promptOverheadTokens)
    {
        if (_sessionContextProjectionService is null)
        {
            return new AgentSessionPromptProjection(
                host.ListRecentTurns(MaxPromptContextTurns),
                SummaryUpdated: false,
                OmittedHistoricalTurnCount: 0);
        }

        var projection = _sessionContextProjectionService.BuildProjection(
            context.Session.SessionId,
            host.ListTurns(),
            context.UserTurnId,
            context.RunCapabilities,
            excludedTurnId: null,
            promptOverheadTokens);
        if (projection.SummaryUpdated)
        {
            host.LogEvent(
                AgentLogLevel.Debug,
                "session.context.summary.updated",
                "Updated core session continuity summary.",
                attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["session.context.omitted_turn_count"] = projection.OmittedHistoricalTurnCount,
                    ["session.context.prompt_turn_count"] = projection.PromptTurns.Count,
                });
        }

        return projection;
    }

    private async Task<IReadOnlyList<ChatMessage>> BuildPromptMessagesAsync(
        IReadOnlyList<AgentTurnRecord> turns,
        Guid activeUserTurnId,
        bool useBoundedHistoricalWindow,
        AgentProviderRunCapabilities runCapabilities,
        CancellationToken cancellationToken)
    {
        var promptTurns = BuildPromptTurns(turns, activeUserTurnId, useBoundedHistoricalWindow);
        var messages = new List<ChatMessage>(promptTurns.Count);
        foreach (var turn in promptTurns)
        {
            messages.Add(await BuildChatMessageAsync(
                turn,
                runCapabilities,
                cancellationToken).ConfigureAwait(false));
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
        var selectedIndexes = new SortedSet<int>(Enumerable.Range(
            activeTurnIndex - historicalCount,
            historicalCount + (turns.Count - activeTurnIndex)));
        IncludeToolPairs(turns, selectedIndexes);
        var pairedToolCallIds = CollectToolCallIds(turns, selectedIndexes);
        pairedToolCallIds.IntersectWith(CollectToolResultIds(turns, selectedIndexes));
        return selectedIndexes
            .Select(index => RemoveOrphanToolItems(turns[index], pairedToolCallIds))
            .Where(turn => turn.Items.Count > 0)
            .ToArray();
    }

    private static void IncludeToolPairs(
        IReadOnlyList<AgentTurnRecord> turns,
        SortedSet<int> selectedIndexes)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            var includedToolCallIds = CollectToolCallIds(turns, selectedIndexes);
            var includedToolResultIds = CollectToolResultIds(turns, selectedIndexes);
            foreach (var index in selectedIndexes.ToArray())
            {
                foreach (var result in turns[index].Items.Where(item =>
                             item.Kind == AgentTurnItemKind.ToolResult
                             && !string.IsNullOrWhiteSpace(item.CallId)))
                {
                    if (includedToolCallIds.Contains(result.CallId!))
                    {
                        continue;
                    }

                    var matchingCallIndex = FindMatchingToolCallIndex(turns, index, result.CallId!);
                    if (matchingCallIndex >= 0 && selectedIndexes.Add(matchingCallIndex))
                    {
                        changed = true;
                    }
                }

                foreach (var call in turns[index].Items.Where(item =>
                             item.Kind == AgentTurnItemKind.ToolCall
                             && !string.IsNullOrWhiteSpace(item.CallId)))
                {
                    if (includedToolResultIds.Contains(call.CallId!))
                    {
                        continue;
                    }

                    var matchingResultIndex = FindMatchingToolResultIndex(turns, index, call.CallId!);
                    if (matchingResultIndex >= 0 && selectedIndexes.Add(matchingResultIndex))
                    {
                        changed = true;
                    }
                }
            }
        }
    }

    private static HashSet<string> CollectToolCallIds(
        IReadOnlyList<AgentTurnRecord> turns,
        IEnumerable<int> indexes)
        => indexes
            .SelectMany(index => turns[index].Items)
            .Where(item => item.Kind == AgentTurnItemKind.ToolCall && !string.IsNullOrWhiteSpace(item.CallId))
            .Select(item => item.CallId!)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> CollectToolResultIds(
        IReadOnlyList<AgentTurnRecord> turns,
        IEnumerable<int> indexes)
        => indexes
            .SelectMany(index => turns[index].Items)
            .Where(item => item.Kind == AgentTurnItemKind.ToolResult && !string.IsNullOrWhiteSpace(item.CallId))
            .Select(item => item.CallId!)
            .ToHashSet(StringComparer.Ordinal);

    private static int FindMatchingToolCallIndex(
        IReadOnlyList<AgentTurnRecord> turns,
        int resultIndex,
        string callId)
    {
        for (var index = resultIndex - 1; index >= 0; index--)
        {
            if (turns[index].Items.Any(item =>
                    item.Kind == AgentTurnItemKind.ToolCall
                    && string.Equals(item.CallId, callId, StringComparison.Ordinal)))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindMatchingToolResultIndex(
        IReadOnlyList<AgentTurnRecord> turns,
        int callIndex,
        string callId)
    {
        for (var index = callIndex + 1; index < turns.Count; index++)
        {
            if (turns[index].Items.Any(item =>
                    item.Kind == AgentTurnItemKind.ToolResult
                    && string.Equals(item.CallId, callId, StringComparison.Ordinal)))
            {
                return index;
            }
        }

        return -1;
    }

    private static AgentTurnRecord RemoveOrphanToolItems(
        AgentTurnRecord turn,
        ISet<string> pairedToolCallIds)
    {
        var filteredItems = turn.Items
            .Where(item => item.Kind switch
            {
                AgentTurnItemKind.ToolCall or AgentTurnItemKind.ToolResult =>
                    !string.IsNullOrWhiteSpace(item.CallId) && pairedToolCallIds.Contains(item.CallId!),
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

                case AgentTurnItemKind.ToolCall when !string.IsNullOrWhiteSpace(item.CallId)
                                                     && !string.IsNullOrWhiteSpace(item.ToolId):
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
                    contents.Add(await BuildAttachmentContentAsync(
                        item,
                        runCapabilities,
                        cancellationToken).ConfigureAwait(false));
                    break;
            }
        }

        return new ChatMessage(ToChatRole(turn.Role), contents)
        {
            MessageId = turn.TurnId.ToString("N"),
            CreatedAt = turn.CreatedAtUtc,
        };
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
            var bytes = await _attachmentStore.ReadAttachmentBytesAsync(
                metadata,
                cancellationToken).ConfigureAwait(false);
            return new DataContent(bytes, metadata.MediaType) { Name = metadata.FileName };
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

    private static bool SupportsAttachmentInput(
        AgentProviderRunCapabilities capabilities,
        AgentAttachmentKind kind)
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
                .ToDictionary(
                    property => property.Name,
                    property => (object?)property.Value.Clone(),
                    StringComparer.Ordinal);
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
        => !string.IsNullOrWhiteSpace(item.TextContent)
            ? item.TextContent
            : !string.IsNullOrWhiteSpace(item.StructuredPayloadJson)
                ? item.StructuredPayloadJson
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

    private static int EstimatePromptOverheadTokens(
        string? systemInstructions,
        IReadOnlyList<AgentToolDescriptor> availableTools)
    {
        var chars = systemInstructions?.Length ?? 0;
        foreach (var tool in availableTools)
        {
            chars += tool.ToolId.Length;
            chars += tool.DisplayName.Length;
            chars += tool.Description.Length;
            chars += tool.ArgumentsJsonSchema?.Length ?? 0;
            chars += tool.RuntimeInstructions?.Length ?? 0;
        }

        return 512 + (chars / 4);
    }

    private static bool ShouldAllowMultipleToolCalls(
        AgentBehaviorLoopContext context,
        IReadOnlyList<AgentToolDescriptor> availableTools)
        => context.RunCapabilities.SupportsMultipleToolCalls
           && availableTools.Any(tool => tool.ConcurrencyMode == AgentToolConcurrencyMode.ParallelSafe);
}

internal sealed class AgentPromptPreparation(
    IReadOnlyList<AgentRuntimeTool> runtimeTools,
    IReadOnlyList<AgentToolDescriptor> availableTools,
    bool allowMultipleToolCalls,
    AgentSystemPromptRequest promptRequest,
    AgentSessionPromptProjection projection,
    string? systemInstructions,
    int promptOverheadTokens)
{
    public IReadOnlyList<AgentRuntimeTool> RuntimeTools { get; } = runtimeTools;

    public IReadOnlyList<AgentToolDescriptor> AvailableTools { get; } = availableTools;

    public bool AllowMultipleToolCalls { get; } = allowMultipleToolCalls;

    public AgentSystemPromptRequest PromptRequest { get; set; } = promptRequest;

    public AgentSessionPromptProjection Projection { get; set; } = projection;

    public string? SystemInstructions { get; set; } = systemInstructions;

    public int PromptOverheadTokens { get; set; } = promptOverheadTokens;
}
