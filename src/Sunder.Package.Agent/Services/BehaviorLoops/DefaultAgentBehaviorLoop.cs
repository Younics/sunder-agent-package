using System.Diagnostics;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

[SunderServiceProvider(typeof(Rpc.Public.BehaviorLoopService), ProviderId = "agent.behavior.loop")]
public sealed partial class DefaultAgentBehaviorLoop : IAgentBehaviorLoop, Rpc.Public.BehaviorLoopService
{
    public const string LoopId = AgentBehaviorLoopIds.Default;
    internal const string BuiltInSourceId = "younics.ai.agent";
    private const int MaxRequestCompactionPasses = 3;

    private readonly AgentPromptPreparationPipeline _promptPreparationPipeline;
    private readonly AgentProviderCycleRunner _providerCycleRunner;
    private readonly AgentToolCycleCoordinator _toolCycleCoordinator;
    private readonly AgentLoopTerminalHandler _terminalHandler;

    public DefaultAgentBehaviorLoop(
        AgentSystemPromptComposer promptComposer,
        AgentAttachmentService? attachmentStore = null,
        AgentSessionContextProjectionService? sessionContextProjectionService = null)
    {
        _terminalHandler = new AgentLoopTerminalHandler();
        var streamingTurnWriter = new AgentStreamingTurnWriter(_terminalHandler);
        _promptPreparationPipeline = new AgentPromptPreparationPipeline(
            promptComposer,
            attachmentStore,
            sessionContextProjectionService);
        _providerCycleRunner = new AgentProviderCycleRunner(streamingTurnWriter);
        _toolCycleCoordinator = new AgentToolCycleCoordinator(_terminalHandler);
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal DefaultAgentBehaviorLoop(
        AgentPromptPreparationPipeline promptPreparationPipeline,
        AgentProviderCycleRunner providerCycleRunner,
        AgentToolCycleCoordinator toolCycleCoordinator,
        AgentLoopTerminalHandler terminalHandler)
    {
        _promptPreparationPipeline = promptPreparationPipeline;
        _providerCycleRunner = providerCycleRunner;
        _toolCycleCoordinator = toolCycleCoordinator;
        _terminalHandler = terminalHandler;
    }

    public AgentBehaviorLoopDescriptor Descriptor { get; } = new(
        LoopId,
        "Default",
        "Framework-backed provider/tool loop used by the base Agent runtime.",
        SourceId: BuiltInSourceId);

    public ValueTask<AgentBehaviorLoopDescriptor> DescribeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Descriptor);
    }

    // A remote request for the built-in behavior delegates to the owning run's
    // default-loop operation. That owner executes the concrete local algorithm
    // below; no remote implementation of the local runtime interface is needed.
    public ValueTask<AgentBehaviorLoopResult> RunAsync(AgentBehaviorLoopContext context, Guid invocationId,
        ServiceReference<AgentServices.IRunControlService> runControl, CancellationToken cancellationToken = default)
        => runControl.Service.RunDefaultAsync(invocationId, context, cancellationToken);

    public async ValueTask<AgentBehaviorLoopResult> RunAsync(
        AgentBehaviorLoopContext context,
        IAgentBehaviorLoopRuntime host,
        CancellationToken cancellationToken = default)
    {
        var loopStopwatch = Stopwatch.StartNew();
        host.LogEvent(
            PackageLogLevel.Debug,
            "behavior.loop.start",
            "Behavior loop started.",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["behavior.loop_id"] = Descriptor.LoopId,
            });
        var assistantTurnState = new AgentAssistantTurnState();
        var budgetRuntime = host as IAgentRunBudgetRuntime;
        Func<AgentRunBudgetCharge, CancellationToken, ValueTask<AgentRunBudgetState>>? durableCharge = budgetRuntime is null
            ? null
            : (charge, token) =>
            {
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult(budgetRuntime.ChargeRunBudget(charge));
            };
        var usageTracker = new AgentRunUsageTracker(
            budgetRuntime?.GetRunBudgetState() ?? default,
            durableCharge);
        var runCancellationToken = cancellationToken;

        try
        {
            var preparation = await _promptPreparationPipeline.PrepareAsync(
                host,
                context,
                runCancellationToken);
            var providerSession = await _providerCycleRunner.CreateSessionAsync(
                host,
                context,
                preparation,
                runCancellationToken);
            var progressGuard = new AgentRunProgressGuard();
            var contextWindowRecoveryAttempted = false;
            var consecutiveOutputLimits = 0;
            string? continuationInstruction = null;
            var retryState = new AgentProviderRetryState();

            while (true)
            {
                var materializedRequest = await BuildAdmittedProviderRequestAsync(
                    host,
                    context,
                    preparation,
                    providerSession,
                    continuationInstruction,
                    runCancellationToken);
                await AcknowledgeProviderContextAsync(
                    host,
                    materializedRequest.ReceiptBlocks,
                    runCancellationToken);
                AgentProviderCycleResult providerCycle;
                try
                {
                    providerCycle = await _providerCycleRunner.RunCycleAsync(
                        host,
                        context,
                        providerSession,
                        materializedRequest.Messages,
                        assistantTurnState,
                        loopStopwatch,
                        runCancellationToken,
                        usageTracker,
                        materializedRequest.Assessment.Estimate.EstimatedInputTokens,
                        retryState);
                }
                catch (AgentProviderContinuationException)
                {
                    continuationInstruction = InterruptedStreamContinuationInstruction;
                    await _promptPreparationPipeline.RefreshAfterToolCycleAsync(host, context, preparation,
                        requiresPromptContextRefresh: false, runCancellationToken);
                    providerSession.Options.Instructions = preparation.SystemInstructions;
                    continue;
                }
                catch (AgentProviderContextWindowException ex)
                {
                    if (!contextWindowRecoveryAttempted && !ex.HasContentBearingOutput)
                    {
                        contextWindowRecoveryAttempted = true;
                        host.LogEvent(
                            PackageLogLevel.Information,
                            "provider.context_window.recovery.started",
                            "Provider context overflow occurred before output; forcing session compaction.",
                            loopStopwatch.ElapsedMilliseconds,
                            new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["prompt.estimated_tokens.before"] = ex.EstimatedInputTokens,
                            });
                        var recoveryTarget = Math.Max(1_024L, ex.EstimatedInputTokens / 2);
                        var requestedReduction = Math.Max(
                            1L,
                            ex.EstimatedInputTokens - recoveryTarget);
                        var changed = await _promptPreparationPipeline.CompactForRequestPressureAsync(
                            host,
                            context,
                            preparation,
                            (int)Math.Min(int.MaxValue, requestedReduction),
                            requireHistoricalAttachmentEviction: false,
                            runCancellationToken);
                        providerSession.Options.Instructions = preparation.SystemInstructions;
                        if (changed)
                        {
                            var reduced = await _promptPreparationPipeline.BuildProviderMessagesAsync(
                                host, preparation, context, runCancellationToken);
                            reduced = WithContinuation(reduced, continuationInstruction);
                            var reducedEstimate = AgentProviderRequestBudget.Assess(reduced.Messages,
                                preparation.SystemInstructions, preparation.AvailableTools, context.RunCapabilities).Estimate;
                            if (reducedEstimate.EstimatedInputTokens < materializedRequest.Assessment.Estimate.EstimatedInputTokens
                                || reducedEstimate.BinaryBytes < materializedRequest.Assessment.Estimate.BinaryBytes)
                            {
                                host.LogEvent(
                                    PackageLogLevel.Information,
                                    "provider.context_window.recovery.succeeded",
                                    "Session context was reduced; retrying the affected provider request once.",
                                    loopStopwatch.ElapsedMilliseconds);
                                continue;
                            }
                        }
                    }

                    host.LogEvent(
                        PackageLogLevel.Warning,
                        "provider.context_window.recovery.exhausted",
                        ex.HasContentBearingOutput
                            ? "Provider context overflow occurred after output began; automatic replay was skipped."
                            : "Provider context overflow could not be reduced safely.",
                        loopStopwatch.ElapsedMilliseconds,
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["provider.output_started"] = ex.HasContentBearingOutput,
                            ["provider.recovery_attempted"] = contextWindowRecoveryAttempted,
                        });
                    return await _terminalHandler.HandleProviderFailureAsync(
                        host,
                        context,
                        assistantTurnState,
                        ex.ProviderException,
                        loopStopwatch.ElapsedMilliseconds);
                }

                if (providerCycle.TerminalResult is not null)
                {
                    return providerCycle.TerminalResult;
                }
                // A successful request starts a new logical step. An overflow on
                // a later step gets its own recovery, not a lifetime allowance.
                contextWindowRecoveryAttempted = false;

                if (providerCycle.FinishReason == ChatFinishReason.Length)
                {
                    AgentLoopTerminalHandler.PreservePartialTurn(host, assistantTurnState);
                    if (++consecutiveOutputLimits > 2)
                    {
                        return await _terminalHandler.InterruptAsync(host, assistantTurnState,
                            "### Response could not finish\n\nThe provider repeatedly reached its output-token limit. Partial output is saved. Continue with a smaller step or adjust the model's output settings.",
                            "Provider output remained truncated after two continuation attempts.", runCancellationToken);
                    }
                    // No calls from an incomplete response have been dispatched.
                    // Even syntactically valid arguments may be part of a cut-off batch.
                    continuationInstruction = OutputLimitContinuationInstruction;
                    await _promptPreparationPipeline.RefreshAfterToolCycleAsync(host, context, preparation,
                        requiresPromptContextRefresh: false, runCancellationToken);
                    providerSession.Options.Instructions = preparation.SystemInstructions;
                    continue;
                }
                consecutiveOutputLimits = 0;
                retryState = new AgentProviderRetryState();
                if (providerCycle.FinishReason is { } finish
                    && finish != ChatFinishReason.Stop && finish != ChatFinishReason.ToolCalls)
                {
                    return await _terminalHandler.InterruptAsync(host, assistantTurnState,
                        "### Provider response interrupted\n\nThe provider did not report a normal completion. Partial output is saved; no pending tools from this response were executed.",
                        "Provider reported an unsupported or filtered completion.", runCancellationToken);
                }

                var compactedAfterProviderCycle = await _promptPreparationPipeline.CompactAfterProviderCycleAsync(
                    host,
                    context,
                    preparation,
                    providerCycle.ReportedContextTokenCount,
                    runCancellationToken);
                if (compactedAfterProviderCycle)
                {
                    providerSession.Options.Instructions = preparation.SystemInstructions;
                }

                if (providerCycle.ToolCalls.Count == 0)
                {
                    return await _terminalHandler.CompleteAsync(
                        host,
                        assistantTurnState,
                        providerCycle.Text,
                        loopStopwatch,
                        runCancellationToken);
                }

                if (assistantTurnState.Turn is { } toolPreamble)
                {
                    assistantTurnState.Turn = host.CompleteAssistantTurn(toolPreamble);
                }
                var toolCycle = await _toolCycleCoordinator.RunCycleAsync(
                    host,
                    context,
                    providerCycle.ToolCalls,
                    preparation.AllowMultipleToolCalls,
                    progressGuard,
                    assistantTurnState,
                    loopStopwatch,
                    runCancellationToken,
                    usageTracker);
                if (!toolCycle.ShouldContinue)
                {
                    return toolCycle.TerminalResult!;
                }
                (host as IAgentRunActivitySink)?.ReportRunActivity(AgentRunActivityKind.Processing, "Reviewing results");
                continuationInstruction = toolCycle.ContinuationInstruction;

                assistantTurnState.Turn = null;
                await _promptPreparationPipeline.RefreshAfterToolCycleAsync(
                    host,
                    context,
                    preparation,
                    toolCycle.RequiresPromptContextRefresh,
                    runCancellationToken);
                providerSession.Options.Instructions = preparation.SystemInstructions;
            }
        }
        catch (AgentRunTranscriptWriteRejectedException)
        {
            host.LogEvent(
                PackageLogLevel.Debug,
                "behavior.loop.transcript_write_rejected",
                "The run changed before a transcript mutation could be committed.",
                loopStopwatch.ElapsedMilliseconds);
            return new AgentBehaviorLoopResult(
                context.RunningCheckpoint,
                AgentBehaviorLoopCompletionKind.Interrupted);
        }
        catch (OperationCanceledException ex)
        {
            if (_providerCycleRunner.IsTransientFailure(ex, cancellationToken))
            {
                return await _terminalHandler.HandleProviderInterruptedAsync(
                    host,
                    context,
                    assistantTurnState,
                    ex.Message,
                    loopStopwatch.ElapsedMilliseconds,
                    ex,
                    CancellationToken.None);
            }

            host.LogEvent(
                PackageLogLevel.Debug,
                "behavior.loop.canceled",
                "Behavior loop was canceled.",
                loopStopwatch.ElapsedMilliseconds);
            return new AgentBehaviorLoopResult(
                context.RunningCheckpoint,
                AgentBehaviorLoopCompletionKind.Interrupted);
        }
        catch (AgentChatProviderException ex)
        {
            if (AgentFailurePresentationPolicy.IsStreamQueueLimit(ex))
            {
                return await _terminalHandler.HandleFailureAsync(
                    host,
                    context,
                    assistantTurnState,
                    ex,
                    loopStopwatch.ElapsedMilliseconds);
            }

            if (_providerCycleRunner.IsTransientFailure(ex, cancellationToken))
            {
                return await _terminalHandler.HandleProviderInterruptedAsync(
                    host,
                    context,
                    assistantTurnState,
                    ex.Message,
                    loopStopwatch.ElapsedMilliseconds,
                    ex,
                    CancellationToken.None);
            }

            return await _terminalHandler.HandleProviderFailureAsync(
                host,
                context,
                assistantTurnState,
                ex,
                loopStopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            if (_providerCycleRunner.IsTransientFailure(ex, cancellationToken))
            {
                return await _terminalHandler.HandleProviderInterruptedAsync(
                    host,
                    context,
                    assistantTurnState,
                    ex.Message,
                    loopStopwatch.ElapsedMilliseconds,
                    ex,
                    CancellationToken.None);
            }

            return await _terminalHandler.HandleFailureAsync(
                host,
                context,
                assistantTurnState,
                ex,
                loopStopwatch.ElapsedMilliseconds);
        }
        finally
        {
            if (host is IAgentPromptContextAcknowledgmentRuntime acknowledgmentRuntime)
            {
                acknowledgmentRuntime.DiscardPromptContextAcknowledgment();
            }
        }
    }

    private async Task<AdmittedProviderRequest> BuildAdmittedProviderRequestAsync(
        IAgentBehaviorLoopRuntime host,
        AgentBehaviorLoopContext context,
        AgentPromptPreparation preparation,
        AgentProviderSession providerSession,
        string? continuationInstruction,
        CancellationToken cancellationToken)
    {
        var canReduceFurther = true;
        for (var pass = 0; ; pass++)
        {
            var providerMessages = await _promptPreparationPipeline.BuildProviderMessagesAsync(
                host,
                preparation,
                context,
                cancellationToken);
            providerMessages = WithContinuation(providerMessages, continuationInstruction);
            var assessment = AgentProviderRequestBudget.Assess(
                providerMessages.Messages,
                preparation.SystemInstructions,
                preparation.AvailableTools,
                context.RunCapabilities);
            var requireHistoricalAttachmentEviction = ShouldEvictHistoricalAttachments(
                providerMessages.Messages,
                context,
                preparation,
                assessment);
            if (!assessment.NeedsCompaction)
            {
                return new AdmittedProviderRequest(
                    providerMessages.Messages,
                    providerMessages.ReceiptBlocks,
                    assessment);
            }

            if (!assessment.FitsPayloadLimit && !requireHistoricalAttachmentEviction)
            {
                throw CreatePromptTooLargeException(assessment);
            }

            if (!canReduceFurther || pass >= MaxRequestCompactionPasses - 1)
            {
                if (assessment.FitsHardLimit)
                {
                    return new AdmittedProviderRequest(
                        providerMessages.Messages,
                        providerMessages.ReceiptBlocks,
                        assessment);
                }

                throw CreatePromptTooLargeException(assessment);
            }

            canReduceFurther = await _promptPreparationPipeline.CompactForRequestPressureAsync(
                host,
                context,
                preparation,
                assessment.TargetReductionTokens,
                requireHistoricalAttachmentEviction,
                cancellationToken);
            providerSession.Options.Instructions = preparation.SystemInstructions;
        }
    }

    private static bool ShouldEvictHistoricalAttachments(
        IReadOnlyList<ChatMessage> messages,
        AgentBehaviorLoopContext context,
        AgentPromptPreparation preparation,
        AgentProviderRequestAssessment assessment)
    {
        if (assessment.FitsPayloadLimit || preparation.Projection.ContextCheckpoint is not null)
        {
            return false;
        }

        var activeMessageId = context.UserTurnId.ToString("N");
        var foundHistoricalMedia = false;
        var withoutHistoricalMedia = messages
            .Select(message =>
            {
                if (string.Equals(message.MessageId, activeMessageId, StringComparison.Ordinal))
                {
                    return message;
                }

                var contents = message.Contents
                    .Select(content =>
                    {
                        if (content is not DataContent)
                        {
                            return content;
                        }

                        foundHistoricalMedia = true;
                        return (AIContent)new TextContent(
                            "[Historical attachment omitted from AI context after session compaction.]");
                    })
                    .ToArray();
                return new ChatMessage(message.Role, contents) { MessageId = message.MessageId };
            })
            .ToArray();
        return foundHistoricalMedia
               && AgentProviderRequestBudget.Assess(
                       withoutHistoricalMedia,
                       preparation.SystemInstructions,
                       preparation.AvailableTools,
                       context.RunCapabilities)
                   .FitsPayloadLimit;
    }

    private static async Task AcknowledgeProviderContextAsync(
        IAgentBehaviorLoopRuntime host,
        IReadOnlyList<AgentPromptContextReceiptBlock> receiptBlocks,
        CancellationToken cancellationToken)
    {
        if (receiptBlocks.Count == 0)
        {
            return;
        }

        if (host is not IAgentPromptContextAcknowledgmentRuntime acknowledgmentRuntime)
        {
            throw new InvalidOperationException(
                "The behavior runtime cannot acknowledge required scoped instruction prompt context.");
        }

        await acknowledgmentRuntime.AcknowledgePromptContextAsync(
            receiptBlocks,
            cancellationToken).ConfigureAwait(false);
    }

    private static AgentPromptTooLargeException CreatePromptTooLargeException(
        AgentProviderRequestAssessment assessment)
        => !assessment.FitsPayloadLimit
            ? new AgentPromptTooLargeException(
                "The current attachment payload is too large for one provider request after session compaction. "
                + $"The estimated serialized payload is {assessment.Estimate.EstimatedPayloadBytes:N0} bytes and the request limit is "
                + $"{AgentProviderRequestBudget.MaxSerializedPayloadBytes:N0} bytes. Use smaller or fewer current attachments.")
            : new AgentPromptTooLargeException(
                "The current request cannot fit within the selected model's context window after session compaction. "
                + $"The estimated input is {assessment.Estimate.EstimatedInputTokens:N0} tokens and the available input budget is "
                + $"{assessment.Limits.HardInputLimitTokens:N0} tokens. Use smaller or fewer current attachments, reduce required instructions, or select a model with a larger context window.");

}

internal sealed record AdmittedProviderRequest(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<AgentPromptContextReceiptBlock> ReceiptBlocks,
    AgentProviderRequestAssessment Assessment);
