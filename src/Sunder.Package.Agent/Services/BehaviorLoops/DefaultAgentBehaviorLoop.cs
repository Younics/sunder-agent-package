using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

public sealed class DefaultAgentBehaviorLoop(AgentSystemPromptComposer promptComposer, IAgentAttachmentContentStore? attachmentStore = null) : IAgentBehaviorLoop
{
    public const string LoopId = AgentBehaviorLoopIds.Default;

    private const int MaxHistoricalTurnsWithInstructionContext = 16;
    private static readonly TimeSpan AssistantStreamFlushInterval = TimeSpan.FromMilliseconds(150);

    public AgentBehaviorLoopDescriptor Descriptor { get; } = new(
        LoopId,
        "Default",
        "Framework-backed provider/tool loop used by the base Agent runtime.");

    public async ValueTask<AgentBehaviorLoopResult> RunAsync(
        AgentBehaviorLoopContext context,
        IAgentBehaviorLoopRuntime host,
        CancellationToken cancellationToken = default)
    {
        var loopStopwatch = Stopwatch.StartNew();
        host.LogEvent(AgentLogLevel.Debug, "behavior.loop.start", "Behavior loop started.", attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["behavior.loop_id"] = Descriptor.LoopId,
        });
        var instructionContext = await host.BuildInstructionContextAsync(cancellationToken);
        AgentTurnRecord? assistantTurn = null;

        try
        {
            var availableRuntimeTools = context.RunCapabilities.SupportsNativeToolCalling
                ? await host.ListReadyToolsAsync(cancellationToken)
                : [];
            var availableTools = availableRuntimeTools.Select(tool => tool.Descriptor).ToArray();
            var promptRequest = new AgentSystemPromptRequest(
                context.Session,
                context.Profile,
                context.ProviderId,
                context.ModelId,
                context.RunCapabilities,
                context.Workspace,
                context.ExecutionBinding,
                availableTools,
                host.ListTurns(),
                context.RunId,
                context.RunRevision,
                context.RunStartedAtUtc,
                context.UserMessage);
            var promptStopwatch = Stopwatch.StartNew();
            host.LogEvent(AgentLogLevel.Debug, "system_prompt.compose.start", "Composing system prompt.");
            var runtimeSystemInstructions = await promptComposer.ComposeAsync(
                promptRequest,
                instructionContext.SystemInstructions,
                cancellationToken);
            host.LogEvent(
                AgentLogLevel.Debug,
                "system_prompt.compose.completed",
                $"{runtimeSystemInstructions?.Length ?? 0} characters",
                promptStopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["system_prompt.length"] = runtimeSystemInstructions?.Length ?? 0,
                    ["system_prompt.base_instruction_length"] = instructionContext.SystemInstructions?.Length ?? 0,
                    ["tool.available_count"] = availableTools.Length,
                    ["workspace.id"] = context.Workspace?.WorkspaceId,
                    ["workspace.binding_id"] = context.ExecutionBinding?.BindingId,
            });
            var toolInvoker = new SunderAgentToolInvoker(
                context,
                host);
            var aiTools = availableRuntimeTools
                .Select(tool => (AITool)new SunderAgentToolFunction(tool, toolInvoker))
                .ToList();
            var rawChatClient = await host.CreateChatClientAsync(
                new AgentChatClientContext(context.ProviderId, context.ModelId),
                cancellationToken);
            var chatClient = new FunctionInvokingChatClient(rawChatClient)
            {
                FunctionInvoker = toolInvoker.InvokeAsync,
                MaximumConsecutiveErrorsPerRequest = 0,
            };
            var agentOptions = new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions
                {
                    Instructions = runtimeSystemInstructions,
                    ConversationId = context.Session.SessionId.ToString("N"),
                    Tools = aiTools,
                    ToolMode = aiTools.Count > 0 ? new AutoChatToolMode() : ChatToolMode.None,
                    AllowMultipleToolCalls = ShouldAllowMultipleToolCalls(context),
                    Reasoning = BuildReasoningOptions(context.ModelVariant),
                },
                UseProvidedChatClientAsIs = true,
            };
            var agent = chatClient.AsAIAgent(agentOptions);
            var promptMessages = await BuildPromptMessagesAsync(
                host.ListTurns(),
                context.UserTurnId,
                instructionContext.HasSupplementaryContext,
                context.RunCapabilities,
                excludedTurnId: null,
                cancellationToken);
            var contentBuilder = new StringBuilder();
            var lastAssistantFlushElapsed = TimeSpan.MinValue;
            var observedToolBoundaryVersion = toolInvoker.ToolBoundaryVersion;
            AgentBehaviorLoopResult? interruptedResult = null;
            var streamAttempt = 0;
            var retryPipeline = AgentProviderResilience.CreatePipeline(notification =>
            {
                host.LogEvent(
                    AgentLogLevel.Warning,
                    "provider.stream.retrying",
                    $"Transient provider stream interruption. Retrying in {notification.Delay.TotalSeconds:0.#}s (attempt {notification.AttemptNumber}/{notification.MaxRetryAttempts}).",
                    loopStopwatch.ElapsedMilliseconds,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["retry.attempt"] = notification.AttemptNumber,
                        ["retry.max_attempts"] = notification.MaxRetryAttempts,
                        ["retry.delay_ms"] = notification.Delay.TotalMilliseconds,
                        ["retry.exception_type"] = notification.Exception.GetType().FullName,
                    },
                    notification.Exception);
            });

            await retryPipeline.ExecuteAsync(async attemptCancellationToken =>
            {
                if (streamAttempt > 0)
                {
                    if (assistantTurn is not null && contentBuilder.Length > 0)
                    {
                        assistantTurn = host.UpsertAssistantTurn(assistantTurn, string.Empty);
                    }

                    contentBuilder.Clear();
                    lastAssistantFlushElapsed = TimeSpan.MinValue;
                    observedToolBoundaryVersion = toolInvoker.ToolBoundaryVersion;
                    promptMessages = await BuildPromptMessagesAsync(
                        host.ListTurns(),
                        context.UserTurnId,
                        instructionContext.HasSupplementaryContext,
                        context.RunCapabilities,
                        assistantTurn?.TurnId,
                        attemptCancellationToken);
                    host.LogEvent(
                        AgentLogLevel.Debug,
                        "behavior.loop.retry.start",
                        "Retrying provider execution from persisted transcript.",
                        loopStopwatch.ElapsedMilliseconds,
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["retry.attempt"] = streamAttempt,
                            ["prompt.turn_count"] = promptMessages.Count,
                        });
                }

                streamAttempt++;
                var agentSession = await agent.CreateSessionAsync(attemptCancellationToken);
                await foreach (var streamUpdate in agent.RunStreamingAsync(promptMessages, agentSession, cancellationToken: attemptCancellationToken))
                {
                    if (!host.IsCurrentRun())
                    {
                        interruptedResult = new AgentBehaviorLoopResult(context.RunningCheckpoint, AgentBehaviorLoopCompletionKind.Interrupted);
                        return;
                    }

                    if (toolInvoker.ToolBoundaryVersion != observedToolBoundaryVersion)
                    {
                        observedToolBoundaryVersion = toolInvoker.ToolBoundaryVersion;
                        assistantTurn = null;
                        contentBuilder.Clear();
                        lastAssistantFlushElapsed = TimeSpan.MinValue;
                    }

                    if (toolInvoker.TerminalResult is not null)
                    {
                        break;
                    }

                    if (!string.IsNullOrEmpty(streamUpdate.Text))
                    {
                        contentBuilder.Append(streamUpdate.Text);
                        if (ShouldFlushAssistantStream(assistantTurn, loopStopwatch.Elapsed, lastAssistantFlushElapsed))
                        {
                            assistantTurn = host.UpsertAssistantTurn(assistantTurn, contentBuilder.ToString());
                            lastAssistantFlushElapsed = loopStopwatch.Elapsed;
                        }
                    }
                }
            }, cancellationToken);

            if (interruptedResult is not null)
            {
                return interruptedResult;
            }

            if (toolInvoker.TerminalResult is not null)
            {
                if (assistantTurn is not null && contentBuilder.Length > 0)
                {
                    assistantTurn = host.UpsertAssistantTurn(assistantTurn, contentBuilder.ToString());
                }

                host.LogEvent(AgentLogLevel.Information, "behavior.loop.suspended", toolInvoker.TerminalResult.CompletionKind.ToString(), loopStopwatch.ElapsedMilliseconds);
                return toolInvoker.TerminalResult;
            }

            if (contentBuilder.Length == 0)
            {
                if (assistantTurn is not null)
                {
                    assistantTurn = host.UpsertAssistantTurn(assistantTurn, "No visible assistant response was produced.");
                }

                var completedCheckpoint = host.SaveCheckpoint(
                    AgentRunStatus.Completed,
                    "No visible assistant response was produced.");
                await host.PublishLifecycleEventAsync(
                    AgentLifecycleEventKind.AssistantTurnCompleted,
                    AgentRunStatus.Completed,
                    checkpoint: completedCheckpoint,
                    cancellationToken: cancellationToken);
                host.LogEvent(AgentLogLevel.Information, "behavior.loop.completed", "No visible assistant response was produced.", loopStopwatch.ElapsedMilliseconds);
                return new AgentBehaviorLoopResult(completedCheckpoint, AgentBehaviorLoopCompletionKind.Completed);
            }

            var responseContent = contentBuilder.ToString();
            assistantTurn = host.UpsertAssistantTurn(assistantTurn, responseContent);
            host.LogEvent(
                AgentLogLevel.Information,
                "assistant.response.completed",
                $"{responseContent.Length} characters",
                loopStopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["assistant.response_length"] = responseContent.Length,
                    ["assistant.is_error"] = false,
                    ["assistant.error_code"] = null,
                });
            var finalCheckpoint = host.SaveCheckpoint(
                AgentRunStatus.Completed,
                "Assistant response saved.");
            await host.PublishLifecycleEventAsync(
                AgentLifecycleEventKind.AssistantTurnCompleted,
                AgentRunStatus.Completed,
                triggerTurn: assistantTurn,
                checkpoint: finalCheckpoint,
                cancellationToken: cancellationToken);
            var result = new AgentBehaviorLoopResult(finalCheckpoint, ToCompletionKind(finalCheckpoint.Status));
            host.LogEvent(AgentLogLevel.Information, "behavior.loop.completed", result.CompletionKind.ToString(), loopStopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (OperationCanceledException ex)
        {
            if (AgentProviderResilience.IsTransient(ex, cancellationToken))
            {
                return await HandleProviderInterruptedAsync(
                    host,
                    context,
                    assistantTurn,
                    ex.Message,
                    loopStopwatch.ElapsedMilliseconds,
                    ex,
                    CancellationToken.None);
            }

            host.LogEvent(AgentLogLevel.Debug, "behavior.loop.canceled", "Behavior loop was canceled.", loopStopwatch.ElapsedMilliseconds);
            return new AgentBehaviorLoopResult(context.RunningCheckpoint, AgentBehaviorLoopCompletionKind.Interrupted);
        }
        catch (AgentChatProviderException ex)
        {
            if (AgentProviderResilience.IsTransient(ex, cancellationToken))
            {
                return await HandleProviderInterruptedAsync(
                    host,
                    context,
                    assistantTurn,
                    ex.Message,
                    loopStopwatch.ElapsedMilliseconds,
                    ex,
                    CancellationToken.None);
            }

            if (!host.IsCurrentRun())
            {
                host.LogEvent(AgentLogLevel.Information, "behavior.loop.interrupted", "Run was replaced or stopped after provider failure.", loopStopwatch.ElapsedMilliseconds);
                return new AgentBehaviorLoopResult(context.RunningCheckpoint, AgentBehaviorLoopCompletionKind.Interrupted);
            }

            assistantTurn = host.UpsertAssistantTurn(assistantTurn, ex.Content);
            var failedCheckpoint = host.SaveCheckpoint(AgentRunStatus.Failed, ex.ErrorCode ?? ex.Message);
            await host.PublishLifecycleEventAsync(
                AgentLifecycleEventKind.RunFailed,
                AgentRunStatus.Failed,
                triggerTurn: assistantTurn,
                checkpoint: failedCheckpoint,
                cancellationToken: CancellationToken.None);
            host.LogEvent(AgentLogLevel.Error, "provider.request.failed", ex.ErrorCode ?? ex.Message, loopStopwatch.ElapsedMilliseconds, exception: ex);
            return new AgentBehaviorLoopResult(failedCheckpoint, AgentBehaviorLoopCompletionKind.Failed);
        }
        catch (Exception ex)
        {
            if (AgentProviderResilience.IsTransient(ex, cancellationToken))
            {
                return await HandleProviderInterruptedAsync(
                    host,
                    context,
                    assistantTurn,
                    ex.Message,
                    loopStopwatch.ElapsedMilliseconds,
                    ex,
                    CancellationToken.None);
            }

            if (!host.IsCurrentRun())
            {
                host.LogEvent(AgentLogLevel.Information, "behavior.loop.interrupted", "Run was replaced or stopped after failure.", loopStopwatch.ElapsedMilliseconds);
                return new AgentBehaviorLoopResult(context.RunningCheckpoint, AgentBehaviorLoopCompletionKind.Interrupted);
            }

            assistantTurn = host.UpsertAssistantTurn(
                assistantTurn,
                $"### Agent run failed\n\n{ex.Message}");
            var failedCheckpoint = host.SaveCheckpoint(AgentRunStatus.Failed, ex.Message);
            await host.PublishLifecycleEventAsync(
                AgentLifecycleEventKind.RunFailed,
                AgentRunStatus.Failed,
                triggerTurn: assistantTurn,
                checkpoint: failedCheckpoint,
                cancellationToken: CancellationToken.None);
            host.LogEvent(AgentLogLevel.Error, "behavior.loop.failed", ex.Message, loopStopwatch.ElapsedMilliseconds, exception: ex);
            return new AgentBehaviorLoopResult(failedCheckpoint, AgentBehaviorLoopCompletionKind.Failed);
        }
    }

    private static AgentBehaviorLoopCompletionKind ToCompletionKind(AgentRunStatus status)
        => status switch
        {
            AgentRunStatus.Completed => AgentBehaviorLoopCompletionKind.Completed,
            AgentRunStatus.WaitingForApproval => AgentBehaviorLoopCompletionKind.WaitingForApproval,
            AgentRunStatus.Stopped => AgentBehaviorLoopCompletionKind.Stopped,
            AgentRunStatus.Interrupted => AgentBehaviorLoopCompletionKind.Interrupted,
            _ => AgentBehaviorLoopCompletionKind.Failed,
        };

    private static async Task<AgentBehaviorLoopResult> HandleProviderInterruptedAsync(
        IAgentBehaviorLoopRuntime host,
        AgentBehaviorLoopContext context,
        AgentTurnRecord? assistantTurn,
        string message,
        long elapsedMilliseconds,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (!host.IsCurrentRun())
        {
            host.LogEvent(AgentLogLevel.Information, "behavior.loop.interrupted", "Run was replaced or stopped after transient provider failure.", elapsedMilliseconds);
            return new AgentBehaviorLoopResult(context.RunningCheckpoint, AgentBehaviorLoopCompletionKind.Interrupted);
        }

        var interruptedTurn = host.UpsertAssistantTurn(
            assistantTurn,
            $"### Provider connection interrupted\n\nThe provider connection was interrupted after retrying. You can retry or continue this session.\n\n{message}");
        var interruptedCheckpoint = host.SaveCheckpoint(
            AgentRunStatus.Interrupted,
            "Provider connection was interrupted after retrying.");
        await host.PublishLifecycleEventAsync(
            AgentLifecycleEventKind.RunInterrupted,
            AgentRunStatus.Interrupted,
            triggerTurn: interruptedTurn,
            checkpoint: interruptedCheckpoint,
            isInterrupted: true,
            cancellationToken: cancellationToken);
        host.LogEvent(AgentLogLevel.Warning, "behavior.loop.interrupted", message, elapsedMilliseconds, exception: exception);
        return new AgentBehaviorLoopResult(interruptedCheckpoint, AgentBehaviorLoopCompletionKind.Interrupted);
    }

    private static bool ShouldAllowMultipleToolCalls(AgentBehaviorLoopContext context)
        => context.RunCapabilities.SupportsMultipleToolCalls
           && string.Equals(context.Profile.BehaviorLoopId, "orchestrated", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldFlushAssistantStream(AgentTurnRecord? assistantTurn, TimeSpan elapsed, TimeSpan lastFlushElapsed)
        => assistantTurn is null
           || lastFlushElapsed == TimeSpan.MinValue
           || elapsed - lastFlushElapsed >= AssistantStreamFlushInterval;

    private static ReasoningOptions? BuildReasoningOptions(AgentModelVariantDescriptor? variant)
        => variant?.ReasoningEffort is null
            ? null
            : new ReasoningOptions
            {
                Effort = ToReasoningEffort(variant.ReasoningEffort.Value),
            };

    private static ReasoningEffort ToReasoningEffort(AgentReasoningEffort effort)
        => effort switch
        {
            AgentReasoningEffort.None => ReasoningEffort.None,
            AgentReasoningEffort.Low => ReasoningEffort.Low,
            AgentReasoningEffort.Medium => ReasoningEffort.Medium,
            AgentReasoningEffort.High => ReasoningEffort.High,
            AgentReasoningEffort.ExtraHigh => ReasoningEffort.ExtraHigh,
            _ => ReasoningEffort.Medium,
        };

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

        return messages;
    }

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

        if (attachmentStore is null)
        {
            return new TextContent($"[Attachment omitted: {metadata.FileName} could not be loaded because attachment storage is unavailable.]");
        }

        try
        {
            var bytes = await attachmentStore.ReadAttachmentBytesAsync(metadata, cancellationToken).ConfigureAwait(false);
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
