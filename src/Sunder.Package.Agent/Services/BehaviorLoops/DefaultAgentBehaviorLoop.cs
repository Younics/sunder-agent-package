using System.Diagnostics;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

public sealed class DefaultAgentBehaviorLoop : IAgentBehaviorLoop
{
    public const string LoopId = AgentBehaviorLoopIds.Default;
    private const int MaxRequestCompactionPasses = 3;

    private readonly AgentPromptPreparationPipeline _promptPreparationPipeline;
    private readonly AgentProviderCycleRunner _providerCycleRunner;
    private readonly AgentToolCycleCoordinator _toolCycleCoordinator;
    private readonly AgentLoopTerminalHandler _terminalHandler;
    private readonly AgentRunBudgetLimits _budgetLimits;

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
        _budgetLimits = AgentRunBudgetLimits.Default;
    }

    internal DefaultAgentBehaviorLoop(
        AgentPromptPreparationPipeline promptPreparationPipeline,
        AgentProviderCycleRunner providerCycleRunner,
        AgentToolCycleCoordinator toolCycleCoordinator,
        AgentLoopTerminalHandler terminalHandler,
        AgentRunBudgetLimits? budgetLimits = null)
    {
        _promptPreparationPipeline = promptPreparationPipeline;
        _providerCycleRunner = providerCycleRunner;
        _toolCycleCoordinator = toolCycleCoordinator;
        _terminalHandler = terminalHandler;
        _budgetLimits = budgetLimits ?? AgentRunBudgetLimits.Default;
    }

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
        Func<AgentRunBudgetCharge, AgentRunBudgetState>? durableCharge = budgetRuntime is null
            ? null
            : budgetRuntime.ChargeRunBudget;
        var budgetTracker = new AgentRunBudgetTracker(
            _budgetLimits,
            budgetRuntime?.GetRunBudgetState() ?? default,
            durableCharge);
        var remainingWallClock = budgetTracker.GetRemainingWallClock(context.RunStartedAtUtc);
        if (remainingWallClock <= TimeSpan.Zero)
        {
            return await FailBudgetAsync(
                host,
                assistantTurnState,
                budgetTracker.CreateWallClockViolation(),
                loopStopwatch);
        }

        using var runBudgetCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runBudgetCancellation.CancelAfter(remainingWallClock);
        var runCancellationToken = runBudgetCancellation.Token;

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

            while (true)
            {
                var materializedRequest = await BuildAdmittedProviderRequestAsync(
                    host,
                    context,
                    preparation,
                    providerSession,
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
                        budgetTracker,
                        materializedRequest.Assessment.Estimate.EstimatedInputTokens);
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
                            host.LogEvent(
                                PackageLogLevel.Information,
                                "provider.context_window.recovery.succeeded",
                                "Session context was compacted; retrying the provider once.",
                                loopStopwatch.ElapsedMilliseconds);
                            continue;
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
                    budgetTracker);
                if (!toolCycle.ShouldContinue)
                {
                    return toolCycle.TerminalResult!;
                }

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
        catch (AgentRunBudgetExceededException ex)
        {
            return await FailBudgetAsync(
                host,
                assistantTurnState,
                ex.Violation,
                loopStopwatch);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && runBudgetCancellation.IsCancellationRequested)
        {
            return await FailBudgetAsync(
                host,
                assistantTurnState,
                budgetTracker.CreateWallClockViolation(),
                loopStopwatch);
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
        CancellationToken cancellationToken)
    {
        for (var pass = 0; ; pass++)
        {
            var providerMessages = await _promptPreparationPipeline.BuildProviderMessagesAsync(
                host,
                preparation,
                context,
                cancellationToken);
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

            if (pass >= MaxRequestCompactionPasses - 1)
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

            await _promptPreparationPipeline.CompactForRequestPressureAsync(
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

    private async Task<AgentBehaviorLoopResult> FailBudgetAsync(
        IAgentBehaviorLoopRuntime host,
        AgentAssistantTurnState assistantTurnState,
        AgentRunBudgetViolation violation,
        Stopwatch loopStopwatch)
    {
        var result = await _terminalHandler.FailAsync(
            host,
            assistantTurnState,
            $"### Agent run budget exhausted\n\n{violation.VisibleMessage}",
            violation.Summary,
            CancellationToken.None);
        host.LogEvent(
            PackageLogLevel.Warning,
            "behavior.loop.budget_exhausted",
            violation.Summary,
            loopStopwatch.ElapsedMilliseconds,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["budget.kind"] = violation.Kind.ToString(),
                ["budget.consumed"] = violation.Consumed,
                ["budget.limit"] = violation.Limit,
            });
        return result;
    }
}

internal sealed record AdmittedProviderRequest(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<AgentPromptContextReceiptBlock> ReceiptBlocks,
    AgentProviderRequestAssessment Assessment);
