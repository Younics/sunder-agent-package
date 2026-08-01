using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal sealed partial class AgentPromptPreparationPipeline(
    AgentSystemPromptComposer promptComposer,
    AgentAttachmentService? attachmentStore = null,
    AgentSessionContextProjectionService? sessionContextProjectionService = null)
{
    private const int MaxHistoricalTurnsWithInstructionContext = 16;
    private const int MaxPromptContextTurns = 64;
    private const int MaxProjectionConvergencePasses = 4;
    private readonly AgentSystemPromptComposer _promptComposer = promptComposer;
    private readonly AgentAttachmentService? _attachmentStore = attachmentStore;
    private readonly AgentSessionContextProjectionService? _sessionContextProjectionService = sessionContextProjectionService;

    public async Task<AgentPromptPreparation> PrepareAsync(
        IAgentBehaviorLoopRuntime host,
        AgentBehaviorLoopContext context,
        CancellationToken cancellationToken)
    {
        var projection = await BuildPromptProjectionAsync(
            host,
            context,
            promptOverheadTokens: 0,
            cancellationToken).ConfigureAwait(false);
        var runtimeTools = context.RunCapabilities.SupportsNativeToolCalling
            ? await host.ListReadyToolsAsync(cancellationToken)
            : [];
        var availableTools = runtimeTools.Select(tool => tool.Descriptor).ToArray();
        var instructionContext = await host.BuildInstructionContextAsync(cancellationToken);
        var promptRequest = CreatePromptRequest(context, availableTools, projection.PromptTurns);
        var promptStopwatch = Stopwatch.StartNew();
        host.LogEvent(PackageLogLevel.Debug, "system_prompt.compose.start", "Composing system prompt.");
        var systemInstructions = await _promptComposer.ComposeAsync(
            promptRequest,
            instructionContext.SystemInstructions,
            cancellationToken);
        var promptOverheadTokens = EstimatePromptOverheadTokens(
            systemInstructions,
            instructionContext.SupplementaryContextBlocks,
            availableTools);
        for (var pass = 0; pass < MaxProjectionConvergencePasses; pass++)
        {
            var nextProjection = await BuildPromptProjectionAsync(
                host,
                context,
                promptOverheadTokens,
                cancellationToken).ConfigureAwait(false);
            var contextSelectionChanged = HasContextSelectionChanged(projection, nextProjection);
            projection = nextProjection;
            if (contextSelectionChanged)
            {
                instructionContext = await host.BuildInstructionContextAsync(cancellationToken);
            }

            promptRequest = promptRequest with { Turns = projection.PromptTurns };
            systemInstructions = await _promptComposer.ComposeAsync(
                promptRequest,
                instructionContext.SystemInstructions,
                cancellationToken);
            var nextPromptOverheadTokens = EstimatePromptOverheadTokens(
                systemInstructions,
                instructionContext.SupplementaryContextBlocks,
                availableTools);
            if (!contextSelectionChanged && nextPromptOverheadTokens == promptOverheadTokens)
            {
                promptOverheadTokens = nextPromptOverheadTokens;
                break;
            }
            promptOverheadTokens = nextPromptOverheadTokens;
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
            instructionContext.SupplementaryContextBlocks ?? [],
            promptOverheadTokens);
    }

    public async Task<AgentProviderMessages> BuildProviderMessagesAsync(
        IAgentBehaviorLoopRuntime host,
        AgentPromptPreparation preparation,
        AgentBehaviorLoopContext context,
        CancellationToken cancellationToken)
    {
        var messages = (await BuildPromptMessagesAsync(
            preparation.Projection.PromptTurns,
            context.UserTurnId,
            useBoundedHistoricalWindow: _sessionContextProjectionService is null,
            evictHistoricalAttachments: preparation.Projection.ContextCheckpoint is not null,
            context.RunCapabilities,
            cancellationToken)).ToList();
        IReadOnlyList<AgentPromptContextReceiptBlock> receiptBlocks = [];
        if (preparation.SupplementaryContextBlocks.Count > 0)
        {
            var rendered = RenderSupplementaryContextPayload(preparation.SupplementaryContextBlocks);
            messages.Insert(0, BuildSupplementaryContextMessage(rendered.Content, context.RunId));
            receiptBlocks = rendered.ReceiptBlocks;
        }

        return new AgentProviderMessages(messages, receiptBlocks);
    }

    public async Task RefreshAfterToolCycleAsync(
        IAgentBehaviorLoopRuntime host,
        AgentBehaviorLoopContext context,
        AgentPromptPreparation preparation,
        bool requiresPromptContextRefresh,
        CancellationToken cancellationToken)
    {
        var forceContextRefresh = requiresPromptContextRefresh;
        for (var pass = 0; pass < MaxProjectionConvergencePasses; pass++)
        {
            var projection = await BuildPromptProjectionAsync(
                host,
                context,
                preparation.PromptOverheadTokens,
                cancellationToken).ConfigureAwait(false);
            var contextSelectionChanged = HasContextSelectionChanged(preparation.Projection, projection);
            preparation.Projection = projection;
            if (!contextSelectionChanged && !forceContextRefresh)
            {
                return;
            }

            forceContextRefresh = false;
            var instructionContext = await host.BuildInstructionContextAsync(cancellationToken);
            preparation.PromptRequest = preparation.PromptRequest with
            {
                Turns = preparation.Projection.PromptTurns,
            };
            preparation.SystemInstructions = await _promptComposer.ComposeAsync(
                preparation.PromptRequest,
                instructionContext.SystemInstructions,
                cancellationToken);
            preparation.SupplementaryContextBlocks = instructionContext.SupplementaryContextBlocks ?? [];
            preparation.PromptOverheadTokens = EstimatePromptOverheadTokens(
                preparation.SystemInstructions,
                preparation.SupplementaryContextBlocks,
                preparation.AvailableTools);
        }
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
            PackageLogLevel.Debug,
            "system_prompt.compose.completed",
            $"{systemInstructions?.Length ?? 0} characters",
            elapsedMilliseconds,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["system_prompt.length"] = systemInstructions?.Length ?? 0,
                ["system_prompt.base_instruction_length"] = instructionContext.SystemInstructions?.Length ?? 0,
                ["context.supplementary_block_count"] = instructionContext.SupplementaryContextBlocks?.Count ?? 0,
                ["tool.available_count"] = availableToolCount,
                ["workspace.id"] = context.Workspace?.WorkspaceId,
                ["workspace.binding_id"] = context.ExecutionBinding?.BindingId,
            });

    private async Task<AgentSessionPromptProjection> BuildPromptProjectionAsync(
        IAgentBehaviorLoopRuntime host,
        AgentBehaviorLoopContext context,
        int promptOverheadTokens,
        CancellationToken cancellationToken,
        int minimumOmittedTurnCount = 0,
        bool compactContext = false)
    {
        AgentSessionPromptProjection projection;
        if (_sessionContextProjectionService is null)
        {
            projection = new AgentSessionPromptProjection(
                host.ListRecentTurns(MaxPromptContextTurns),
                SummaryUpdated: false,
                OmittedHistoricalTurnCount: 0);
        }
        else
        {
            projection = await _sessionContextProjectionService.BuildProjectionAsync(
                context.Session.SessionId,
                context.UserTurnId,
                context.RunCapabilities,
                context.Profile,
                context.RunId,
                context.RunRevision,
                promptOverheadTokens,
                cancellationToken,
                minimumOmittedTurnCount,
                compactContext).ConfigureAwait(false);
        }

        projection = RedactHistoricalAttachmentContent(projection, context.UserTurnId);

        if (host is IAgentSessionContextSelectionRuntime selectionRuntime)
        {
            selectionRuntime.SelectSessionContextProjection(projection);
        }
        if (projection.SummaryUpdated)
        {
            host.LogEvent(
                PackageLogLevel.Debug,
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

    private static AgentSessionPromptProjection RedactHistoricalAttachmentContent(
        AgentSessionPromptProjection projection,
        Guid activeUserTurnId)
    {
        if (projection.ContextCheckpoint is null)
        {
            return projection;
        }

        var changed = false;
        var turns = projection.PromptTurns
            .Select(turn =>
            {
                if (turn.TurnId == activeUserTurnId
                    || turn.Items.All(item => item.Kind != AgentTurnItemKind.Attachment))
                {
                    return turn;
                }

                changed = true;
                return turn with
                {
                    Items = turn.Items
                        .Select(item => item.Kind == AgentTurnItemKind.Attachment
                            ? item with
                            {
                                TextContent = null,
                                ArgumentsJson = null,
                                ResultSummary = null,
                                SourcesJson = null,
                            }
                            : item)
                        .ToArray(),
                };
            })
            .ToArray();
        return changed ? projection with { PromptTurns = turns } : projection;
    }

    private static bool HasContextSelectionChanged(
        AgentSessionPromptProjection previous,
        AgentSessionPromptProjection current)
        => previous.ContextCheckpoint?.ContextCheckpointId
           != current.ContextCheckpoint?.ContextCheckpointId;

    private async Task<IReadOnlyList<ChatMessage>> BuildPromptMessagesAsync(
        IReadOnlyList<AgentTurnRecord> turns,
        Guid activeUserTurnId,
        bool useBoundedHistoricalWindow,
        bool evictHistoricalAttachments,
        AgentProviderRunCapabilities runCapabilities,
        CancellationToken cancellationToken)
    {
        var promptTurns = BuildPromptTurns(turns, activeUserTurnId, useBoundedHistoricalWindow);
        var messages = new List<ChatMessage>(promptTurns.Count);
        foreach (var turn in promptTurns)
        {
            messages.Add(await BuildChatMessageAsync(
                turn,
                includeAttachmentContent: !evictHistoricalAttachments || turn.TurnId == activeUserTurnId,
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
            var allToolExchanges = AgentToolExchangeAnalyzer.Analyze(turns);
            return allIndexes
                .Select(index => RemoveOrphanToolItems(turns[index], allToolExchanges.PairedItemIds))
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
        var toolExchanges = AgentToolExchangeAnalyzer.Analyze(turns);
        IncludeToolPairs(toolExchanges, selectedIndexes);
        return selectedIndexes
            .Select(index => RemoveOrphanToolItems(turns[index], toolExchanges.PairedItemIds))
            .Where(turn => turn.Items.Count > 0)
            .ToArray();
    }

    private static void IncludeToolPairs(
        AgentToolExchangeAnalysis toolExchanges,
        SortedSet<int> selectedIndexes)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var pair in toolExchanges.Pairs)
            {
                if (selectedIndexes.Contains(pair.CallTurnIndex)
                    && selectedIndexes.Add(pair.ResultTurnIndex))
                {
                    changed = true;
                }
                if (selectedIndexes.Contains(pair.ResultTurnIndex)
                    && selectedIndexes.Add(pair.CallTurnIndex))
                {
                    changed = true;
                }
            }
        }
    }

    private static AgentTurnRecord RemoveOrphanToolItems(
        AgentTurnRecord turn,
        IReadOnlySet<Guid> pairedToolItemIds)
    {
        var filteredItems = turn.Items
            .Where(item => item.Kind switch
            {
                AgentTurnItemKind.ToolCall or AgentTurnItemKind.ToolResult =>
                    pairedToolItemIds.Contains(item.ItemId),
                _ => true,
            })
            .ToArray();
        return filteredItems.Length == turn.Items.Count ? turn : turn with { Items = filteredItems };
    }

    private async Task<ChatMessage> BuildChatMessageAsync(
        AgentTurnRecord turn,
        bool includeAttachmentContent,
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
                        includeAttachmentContent,
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
        bool includeContent,
        AgentProviderRunCapabilities runCapabilities,
        CancellationToken cancellationToken)
    {
        var metadata = TryReadAttachmentMetadata(item);
        if (metadata is null)
        {
            return new TextContent("[Attachment omitted: metadata is unavailable.]");
        }

        if (!includeContent)
        {
            return new TextContent(RenderCompactedAttachment(metadata));
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
            return new TextContent($"[Attachment omitted because attachment storage is unavailable. Untrusted metadata: {RenderAttachmentMetadata(metadata)}]");
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
            return new TextContent($"[Attachment omitted because it could not be loaded from local storage. Untrusted metadata: {RenderAttachmentMetadata(metadata)}]");
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
            return JsonSerializer.Deserialize<AgentAttachmentMetadata>(item.StructuredPayloadJson) is { } metadata
                ? metadata with
                {
                    FileName = AgentAttachmentService.NormalizeDisplayFileName(metadata.FileName),
                    MediaType = AgentAttachmentService.NormalizeDisplayMediaType(metadata.MediaType),
                }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string RenderTextAttachment(AgentAttachmentMetadata metadata, string? textContent)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Attached file metadata (untrusted JSON): {RenderAttachmentMetadata(metadata)}");
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
        => $"An attachment with untrusted metadata {RenderAttachmentMetadata(metadata)} was provided, but the selected model/provider does not support {DescribeAttachmentKind(metadata.Kind)} input in Sunder. Ask the user to switch to a model that supports this input type or provide the content as text.";

    private static string RenderCompactedAttachment(AgentAttachmentMetadata metadata)
        => $"[Historical attachment omitted from AI context after session compaction. Untrusted metadata: {RenderAttachmentMetadata(metadata)}. The durable attachment remains available in the transcript. Reattach it if its content must be inspected again.]";

    private static string RenderAttachmentMetadata(AgentAttachmentMetadata metadata)
        => JsonSerializer.Serialize(new
        {
            fileName = metadata.FileName,
            mediaType = metadata.MediaType,
            size = FormatByteCount(Math.Max(0, metadata.SizeBytes)),
        });

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

    private static bool ShouldAllowMultipleToolCalls(
        AgentBehaviorLoopContext context,
        IReadOnlyList<AgentToolDescriptor> availableTools)
        => context.RunCapabilities.SupportsMultipleToolCalls
           && availableTools.Any(tool => tool.ConcurrencyMode == AgentToolConcurrencyMode.ParallelSafe);
}

internal sealed record RenderedSupplementaryContext(
    string Content,
    IReadOnlyList<AgentPromptContextReceiptBlock> ReceiptBlocks);

internal sealed record AgentProviderMessages(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<AgentPromptContextReceiptBlock> ReceiptBlocks);

internal sealed class AgentPromptPreparation(
    IReadOnlyList<AgentRuntimeTool> runtimeTools,
    IReadOnlyList<AgentToolDescriptor> availableTools,
    bool allowMultipleToolCalls,
    AgentSystemPromptRequest promptRequest,
    AgentSessionPromptProjection projection,
    string? systemInstructions,
    IReadOnlyList<AgentPromptContextBlock> supplementaryContextBlocks,
    int promptOverheadTokens)
{
    public IReadOnlyList<AgentRuntimeTool> RuntimeTools { get; } = runtimeTools;

    public IReadOnlyList<AgentToolDescriptor> AvailableTools { get; } = availableTools;

    public bool AllowMultipleToolCalls { get; } = allowMultipleToolCalls;

    public AgentSystemPromptRequest PromptRequest { get; set; } = promptRequest;

    public AgentSessionPromptProjection Projection { get; set; } = projection;

    public string? SystemInstructions { get; set; } = systemInstructions;

    public IReadOnlyList<AgentPromptContextBlock> SupplementaryContextBlocks { get; set; } = supplementaryContextBlocks;

    public int PromptOverheadTokens { get; set; } = promptOverheadTokens;
}
