using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

public sealed partial class DefaultAgentBehaviorLoop(
    AgentSystemPromptComposer promptComposer,
    IAgentAttachmentContentStore? attachmentStore = null,
    AgentSessionContextProjectionService? sessionContextProjectionService = null) : IAgentBehaviorLoop
{
    public const string LoopId = AgentBehaviorLoopIds.Default;

    private const int MaxHistoricalTurnsWithInstructionContext = 16;
    private const int MaxPromptContextTurns = 64;
    private static readonly TimeSpan AssistantStreamFlushInterval = TimeSpan.FromMilliseconds(150);
    private readonly IAgentAttachmentContentStore? _attachmentStore = attachmentStore;
    private readonly AgentSessionContextProjectionService? _sessionContextProjectionService = sessionContextProjectionService;

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
        var assistantTurnState = new AssistantTurnState();

        try
        {
            var promptProjection = BuildPromptProjection(host, context, excludedTurnId: null);
            var instructionContext = await host.BuildInstructionContextAsync(cancellationToken);
            var availableRuntimeTools = context.RunCapabilities.SupportsNativeToolCalling
                ? await host.ListReadyToolsAsync(cancellationToken)
                : [];
            var availableTools = availableRuntimeTools.Select(tool => tool.Descriptor).ToArray();
            var allowMultipleToolCalls = ShouldAllowMultipleToolCalls(context, availableTools);
            var promptRequest = new AgentSystemPromptRequest(
                context.Session,
                context.Profile,
                context.ProviderId,
                context.ModelId,
                context.RunCapabilities,
                context.Workspace,
                context.ExecutionBinding,
                availableTools,
                promptProjection.PromptTurns,
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
            var promptOverheadTokens = EstimatePromptOverheadTokens(runtimeSystemInstructions, availableTools);
            promptProjection = BuildPromptProjection(host, context, excludedTurnId: null, promptOverheadTokens);
            if (promptProjection.SummaryUpdated)
            {
                instructionContext = await host.BuildInstructionContextAsync(cancellationToken);
                promptRequest = promptRequest with { Turns = promptProjection.PromptTurns };
                runtimeSystemInstructions = await promptComposer.ComposeAsync(
                    promptRequest,
                    instructionContext.SystemInstructions,
                    cancellationToken);
                promptOverheadTokens = EstimatePromptOverheadTokens(runtimeSystemInstructions, availableTools);
                promptProjection = BuildPromptProjection(host, context, excludedTurnId: null, promptOverheadTokens);
            }
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
            var aiTools = availableRuntimeTools
                .Select(tool => (AITool)tool.Declaration)
                .ToList();
            var rawChatClient = await host.CreateChatClientAsync(
                new AgentChatClientContext(context.ProviderId, context.ModelId),
                cancellationToken);
            var chatOptions = new ChatOptions
            {
                Instructions = runtimeSystemInstructions,
                ConversationId = context.Session.SessionId.ToString("N"),
                Tools = aiTools,
                ToolMode = aiTools.Count > 0 ? new AutoChatToolMode() : ChatToolMode.None,
                AllowMultipleToolCalls = allowMultipleToolCalls,
                Reasoning = BuildReasoningOptions(context.ModelVariant),
            };
            var progressGuard = new AgentRunProgressGuard();
            AgentProviderCycleResult providerCycleResult;
            while (true)
            {
                var promptMessages = await BuildPromptMessagesAsync(
                    promptProjection.PromptTurns,
                    context.UserTurnId,
                    useBoundedHistoricalWindow: _sessionContextProjectionService is null,
                    context.RunCapabilities,
                    excludedTurnId: null,
                    cancellationToken);
                providerCycleResult = await RunProviderCycleAsync(
                    host,
                    context,
                    rawChatClient,
                    promptMessages,
                    chatOptions,
                    assistantTurnState,
                    loopStopwatch,
                    cancellationToken);

                if (providerCycleResult.TerminalResult is not null)
                {
                    return providerCycleResult.TerminalResult;
                }

                if (providerCycleResult.ToolCalls.Count == 0)
                {
                    break;
                }

                if (providerCycleResult.ToolCalls.Count > 1 && !allowMultipleToolCalls)
                {
                    assistantTurnState.Turn = host.UpsertAssistantTurn(
                        assistantTurnState.Turn,
                        "### Agent run failed\n\nThe provider requested multiple tool calls, but this profile/provider combination does not allow parallel tool calls.");
                    var failedCheckpoint = host.SaveCheckpoint(AgentRunStatus.Failed, "Provider requested multiple tool calls.");
                    await host.PublishLifecycleEventAsync(
                        AgentLifecycleEventKind.RunFailed,
                        AgentRunStatus.Failed,
                        triggerTurn: assistantTurnState.Turn,
                        checkpoint: failedCheckpoint,
                        cancellationToken: cancellationToken);
                    return new AgentBehaviorLoopResult(failedCheckpoint, AgentBehaviorLoopCompletionKind.Failed);
                }

                var toolCalls = providerCycleResult.ToolCalls
                    .Select(CreateToolCallRequest)
                    .ToArray();
                var outcomes = await host.InvokeToolsAsync(toolCalls, assistantTurn: null, cancellationToken);
                for (var index = 0; index < outcomes.Count; index++)
                {
                    var toolCall = toolCalls[index];
                    var outcome = outcomes[index];
                    if (outcome.Kind != AgentToolCallOutcomeKind.Executed)
                    {
                        var terminalResult = new AgentBehaviorLoopResult(
                            outcome.Checkpoint ?? context.RunningCheckpoint,
                            outcome.Kind == AgentToolCallOutcomeKind.WaitingForApproval
                                ? AgentBehaviorLoopCompletionKind.WaitingForApproval
                                : AgentBehaviorLoopCompletionKind.Failed);
                        host.LogEvent(AgentLogLevel.Information, "behavior.loop.suspended", terminalResult.CompletionKind.ToString(), loopStopwatch.ElapsedMilliseconds);
                        return terminalResult;
                    }

                    if (progressGuard.RecordToolOutcome(toolCall, outcome, out var progressFailure))
                    {
                        assistantTurnState.Turn = host.UpsertAssistantTurn(null, progressFailure.VisibleMessage);
                        var failedCheckpoint = host.SaveCheckpoint(AgentRunStatus.Failed, progressFailure.CheckpointSummary);
                        await host.PublishLifecycleEventAsync(
                            AgentLifecycleEventKind.RunFailed,
                            AgentRunStatus.Failed,
                            triggerTurn: assistantTurnState.Turn,
                            checkpoint: failedCheckpoint,
                            cancellationToken: cancellationToken);
                        host.LogEvent(
                            AgentLogLevel.Warning,
                            "behavior.loop.no_progress_detected",
                            progressFailure.CheckpointSummary,
                            loopStopwatch.ElapsedMilliseconds,
                            progressFailure.Attributes);
                        return new AgentBehaviorLoopResult(failedCheckpoint, AgentBehaviorLoopCompletionKind.Failed);
                    }
                }

                if (outcomes.Count < toolCalls.Length)
                {
                    var failedTurn = host.UpsertAssistantTurn(null, "### Agent run failed\n\nOne or more requested tool calls did not produce an outcome.");
                    var failedCheckpoint = host.SaveCheckpoint(AgentRunStatus.Failed, "Tool invocation produced no outcome.");
                    await host.PublishLifecycleEventAsync(
                        AgentLifecycleEventKind.RunFailed,
                        AgentRunStatus.Failed,
                        triggerTurn: failedTurn,
                        checkpoint: failedCheckpoint,
                        cancellationToken: cancellationToken);
                    return new AgentBehaviorLoopResult(failedCheckpoint, AgentBehaviorLoopCompletionKind.Failed);
                }

                assistantTurnState.Turn = null;
                promptProjection = BuildPromptProjection(host, context, excludedTurnId: null, promptOverheadTokens);
                if (promptProjection.SummaryUpdated)
                {
                    instructionContext = await host.BuildInstructionContextAsync(cancellationToken);
                    promptRequest = promptRequest with { Turns = promptProjection.PromptTurns };
                    runtimeSystemInstructions = await promptComposer.ComposeAsync(
                        promptRequest,
                        instructionContext.SystemInstructions,
                        cancellationToken);
                    promptOverheadTokens = EstimatePromptOverheadTokens(runtimeSystemInstructions, availableTools);
                    chatOptions.Instructions = runtimeSystemInstructions;
                    promptProjection = BuildPromptProjection(host, context, excludedTurnId: null, promptOverheadTokens);
                }
            }

            var contentBuilder = new StringBuilder(providerCycleResult.Text);

            if (contentBuilder.Length == 0)
            {
                if (assistantTurnState.Turn is not null)
                {
                    assistantTurnState.Turn = host.UpsertAssistantTurn(assistantTurnState.Turn, "No visible assistant response was produced.");
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
            assistantTurnState.Turn = host.UpsertAssistantTurn(assistantTurnState.Turn, responseContent);
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
                triggerTurn: assistantTurnState.Turn,
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
                    assistantTurnState.Turn,
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
                    assistantTurnState.Turn,
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

            assistantTurnState.Turn = host.UpsertAssistantTurn(assistantTurnState.Turn, ex.Content);
            var failedCheckpoint = host.SaveCheckpoint(AgentRunStatus.Failed, ex.ErrorCode ?? ex.Message);
            await host.PublishLifecycleEventAsync(
                AgentLifecycleEventKind.RunFailed,
                AgentRunStatus.Failed,
                triggerTurn: assistantTurnState.Turn,
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
                    assistantTurnState.Turn,
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

            assistantTurnState.Turn = host.UpsertAssistantTurn(
                assistantTurnState.Turn,
                $"### Agent run failed\n\n{ex.Message}");
            var failedCheckpoint = host.SaveCheckpoint(AgentRunStatus.Failed, ex.Message);
            await host.PublishLifecycleEventAsync(
                AgentLifecycleEventKind.RunFailed,
                AgentRunStatus.Failed,
                triggerTurn: assistantTurnState.Turn,
                checkpoint: failedCheckpoint,
                cancellationToken: CancellationToken.None);
            host.LogEvent(AgentLogLevel.Error, "behavior.loop.failed", ex.Message, loopStopwatch.ElapsedMilliseconds, exception: ex);
            return new AgentBehaviorLoopResult(failedCheckpoint, AgentBehaviorLoopCompletionKind.Failed);
        }
    }

    private async Task<AgentProviderCycleResult> RunProviderCycleAsync(
        IAgentBehaviorLoopRuntime host,
        AgentBehaviorLoopContext context,
        IChatClient chatClient,
        IReadOnlyList<ChatMessage> promptMessages,
        ChatOptions chatOptions,
        AssistantTurnState assistantTurnState,
        Stopwatch loopStopwatch,
        CancellationToken cancellationToken)
    {
        var contentBuilder = new StringBuilder();
        var toolCalls = new List<FunctionCallContent>();
        var reasoningActivity = new ReasoningActivityReporter(host as IAgentRunActivitySink);
        var lastAssistantFlushElapsed = TimeSpan.MinValue;
        AgentBehaviorLoopResult? terminalResult = null;
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
                if (assistantTurnState.Turn is not null && contentBuilder.Length > 0)
                {
                    assistantTurnState.Turn = host.UpsertAssistantTurn(assistantTurnState.Turn, string.Empty);
                }

                contentBuilder.Clear();
                toolCalls.Clear();
                lastAssistantFlushElapsed = TimeSpan.MinValue;
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
            await foreach (var streamUpdate in chatClient.GetStreamingResponseAsync(promptMessages, chatOptions, attemptCancellationToken))
            {
                if (!host.IsCurrentRun())
                {
                    terminalResult = new AgentBehaviorLoopResult(context.RunningCheckpoint, AgentBehaviorLoopCompletionKind.Interrupted);
                    return;
                }

                foreach (var functionCall in streamUpdate.Contents.OfType<FunctionCallContent>())
                {
                    toolCalls.Add(functionCall);
                }

                foreach (var reasoningContent in streamUpdate.Contents.OfType<TextReasoningContent>())
                {
                    reasoningActivity.Append(reasoningContent.Text, loopStopwatch.Elapsed);
                }

                if (!string.IsNullOrEmpty(streamUpdate.Text))
                {
                    contentBuilder.Append(streamUpdate.Text);
                    if (AgentVisibleResponseGuard.ContainsProtocolLeak(contentBuilder.ToString()))
                    {
                        assistantTurnState.Turn = host.UpsertAssistantTurn(
                            assistantTurnState.Turn,
                            AgentVisibleResponseGuard.BlockedResponseContent);
                        var failedCheckpoint = host.SaveCheckpoint(
                            AgentRunStatus.Failed,
                            "Assistant response contained internal protocol syntax.");
                        await host.PublishLifecycleEventAsync(
                            AgentLifecycleEventKind.RunFailed,
                            AgentRunStatus.Failed,
                            triggerTurn: assistantTurnState.Turn,
                            checkpoint: failedCheckpoint,
                            cancellationToken: attemptCancellationToken);
                        host.LogEvent(
                            AgentLogLevel.Warning,
                            "assistant.response.protocol_leak_blocked",
                            "Assistant response contained internal protocol syntax.",
                            loopStopwatch.ElapsedMilliseconds,
                            new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["assistant.response_length"] = contentBuilder.Length,
                            });
                        terminalResult = new AgentBehaviorLoopResult(
                            failedCheckpoint,
                            AgentBehaviorLoopCompletionKind.Failed);
                        return;
                    }

                    if (ShouldFlushAssistantStream(assistantTurnState.Turn, loopStopwatch.Elapsed, lastAssistantFlushElapsed))
                    {
                        assistantTurnState.Turn = host.UpsertAssistantTurn(assistantTurnState.Turn, contentBuilder.ToString());
                        lastAssistantFlushElapsed = loopStopwatch.Elapsed;
                    }
                }
            }
        }, cancellationToken);
        reasoningActivity.Flush();

        if (terminalResult is not null)
        {
            return new AgentProviderCycleResult(contentBuilder.ToString(), toolCalls, terminalResult);
        }

        if (contentBuilder.Length > 0)
        {
            assistantTurnState.Turn = host.UpsertAssistantTurn(assistantTurnState.Turn, contentBuilder.ToString());
        }

        return new AgentProviderCycleResult(contentBuilder.ToString(), toolCalls, TerminalResult: null);
    }

    private static AgentToolCallRequest CreateToolCallRequest(FunctionCallContent functionCall)
        => new(
            string.IsNullOrWhiteSpace(functionCall.CallId) ? Guid.NewGuid().ToString("N") : functionCall.CallId,
            functionCall.Name,
            SerializeArguments(functionCall.Arguments));

    private static string SerializeArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return "{}";
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var argument in arguments)
        {
            values[argument.Key] = argument.Value;
        }

        return JsonSerializer.Serialize(values);
    }

    private AgentSessionPromptProjection BuildPromptProjection(
        IAgentBehaviorLoopRuntime host,
        AgentBehaviorLoopContext context,
        Guid? excludedTurnId,
        int promptOverheadTokens = 0)
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
            excludedTurnId,
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

    private static int EstimatePromptOverheadTokens(string? systemInstructions, IReadOnlyList<AgentToolDescriptor> availableTools)
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

    private static bool ShouldAllowMultipleToolCalls(
        AgentBehaviorLoopContext context,
        IReadOnlyList<AgentToolDescriptor> availableTools)
        => context.RunCapabilities.SupportsMultipleToolCalls
           && availableTools.Any(tool => tool.ConcurrencyMode == AgentToolConcurrencyMode.ParallelSafe);

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
                Output = variant.ReasoningEffort.Value == AgentReasoningEffort.None
                    ? ReasoningOutput.None
                    : ReasoningOutput.Summary,
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

    private sealed record AgentProviderCycleResult(
        string Text,
        IReadOnlyList<FunctionCallContent> ToolCalls,
        AgentBehaviorLoopResult? TerminalResult);

    private sealed class ReasoningActivityReporter(IAgentRunActivitySink? activitySink)
    {
        private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(240);
        private const int MaxDisplayCharacters = 260;
        private readonly IAgentRunActivitySink? _activitySink = activitySink;
        private readonly StringBuilder _reasoning = new();
        private TimeSpan _lastReportElapsed = TimeSpan.MinValue;
        private string _lastReportedText = string.Empty;

        public void Append(string? text, TimeSpan elapsed)
        {
            if (_activitySink is null || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            _reasoning.Append(text);
            var displayText = CreateDisplayText(_reasoning.ToString());
            if (string.IsNullOrWhiteSpace(displayText)
                || string.Equals(displayText, _lastReportedText, StringComparison.Ordinal))
            {
                return;
            }

            if (_lastReportElapsed != TimeSpan.MinValue && elapsed - _lastReportElapsed < ReportInterval)
            {
                return;
            }

            Report(displayText, elapsed);
        }

        public void Flush()
        {
            if (_activitySink is null || _reasoning.Length == 0)
            {
                return;
            }

            var displayText = CreateDisplayText(_reasoning.ToString());
            if (!string.IsNullOrWhiteSpace(displayText)
                && !string.Equals(displayText, _lastReportedText, StringComparison.Ordinal))
            {
                Report(displayText, TimeSpan.MaxValue);
            }
        }

        private void Report(string displayText, TimeSpan elapsed)
        {
            _lastReportedText = displayText;
            _lastReportElapsed = elapsed;
            _activitySink?.ReportRunActivity(AgentRunActivityKind.Reasoning, displayText);
        }

        private static string CreateDisplayText(string text)
        {
            var normalized = string.Join(
                ' ',
                text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (normalized.Length <= MaxDisplayCharacters)
            {
                return normalized;
            }

            return normalized[..Math.Max(0, MaxDisplayCharacters - 3)].TrimEnd() + "...";
        }
    }

    private sealed class AssistantTurnState
    {
        public AgentTurnRecord? Turn { get; set; }
    }

}
