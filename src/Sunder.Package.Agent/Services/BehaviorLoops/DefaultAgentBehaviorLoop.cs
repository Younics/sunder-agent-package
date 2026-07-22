using System.Diagnostics;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

public sealed class DefaultAgentBehaviorLoop : IAgentBehaviorLoop
{
    public const string LoopId = AgentBehaviorLoopIds.Default;

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

            while (true)
            {
                var promptMessages = await _promptPreparationPipeline.BuildProviderMessagesAsync(
                    preparation,
                    context,
                    runCancellationToken);
                var providerCycle = await _providerCycleRunner.RunCycleAsync(
                    host,
                    context,
                    providerSession,
                    promptMessages,
                    assistantTurnState,
                    loopStopwatch,
                    runCancellationToken,
                    budgetTracker,
                    preparation.PromptOverheadTokens);

                if (providerCycle.TerminalResult is not null)
                {
                    return providerCycle.TerminalResult;
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
    }

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
