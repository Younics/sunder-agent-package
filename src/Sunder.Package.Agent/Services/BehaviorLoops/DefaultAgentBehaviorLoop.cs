using System.Diagnostics;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

public sealed partial class DefaultAgentBehaviorLoop(AgentSystemPromptComposer promptComposer, IAgentAttachmentContentStore? attachmentStore = null) : IAgentBehaviorLoop
{
    public const string LoopId = AgentBehaviorLoopIds.Default;

    private const int MaxHistoricalTurnsWithInstructionContext = 16;
    private const int MaxPromptContextTurns = 64;
    private const int MaxFunctionInvokingIterationsPerRequest = 128;
    private static readonly TimeSpan AssistantStreamFlushInterval = TimeSpan.FromMilliseconds(150);
    private readonly IAgentAttachmentContentStore? _attachmentStore = attachmentStore;

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
            var promptContextTurns = host.ListRecentTurns(MaxPromptContextTurns);
            var promptRequest = new AgentSystemPromptRequest(
                context.Session,
                context.Profile,
                context.ProviderId,
                context.ModelId,
                context.RunCapabilities,
                context.Workspace,
                context.ExecutionBinding,
                availableTools,
                promptContextTurns,
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
                MaximumIterationsPerRequest = MaxFunctionInvokingIterationsPerRequest,
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
                host.ListRecentTurns(MaxPromptContextTurns),
                context.UserTurnId,
                useBoundedHistoricalWindow: true,
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
                        host.ListRecentTurns(MaxPromptContextTurns),
                        context.UserTurnId,
                        useBoundedHistoricalWindow: true,
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
                        if (AgentVisibleResponseGuard.ContainsProtocolLeak(contentBuilder.ToString()))
                        {
                            assistantTurn = host.UpsertAssistantTurn(
                                assistantTurn,
                                AgentVisibleResponseGuard.BlockedResponseContent);
                            var failedCheckpoint = host.SaveCheckpoint(
                                AgentRunStatus.Failed,
                                "Assistant response contained internal protocol syntax.");
                            await host.PublishLifecycleEventAsync(
                                AgentLifecycleEventKind.RunFailed,
                                AgentRunStatus.Failed,
                                triggerTurn: assistantTurn,
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
                            interruptedResult = new AgentBehaviorLoopResult(
                                failedCheckpoint,
                                AgentBehaviorLoopCompletionKind.Failed);
                            return;
                        }

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

}
