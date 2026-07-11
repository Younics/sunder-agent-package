using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal sealed class AgentToolCycleCoordinator(AgentLoopTerminalHandler terminalHandler)
{
    private readonly AgentLoopTerminalHandler _terminalHandler = terminalHandler;

    public async Task<AgentToolCycleResult> RunCycleAsync(
        IAgentBehaviorLoopRuntime host,
        AgentBehaviorLoopContext context,
        IReadOnlyList<FunctionCallContent> providerToolCalls,
        bool allowMultipleToolCalls,
        AgentRunProgressGuard progressGuard,
        AgentAssistantTurnState assistantTurnState,
        Stopwatch loopStopwatch,
        CancellationToken cancellationToken)
    {
        if (providerToolCalls.Count > 1 && !allowMultipleToolCalls)
        {
            var result = await _terminalHandler.FailAsync(
                host,
                assistantTurnState,
                "### Agent run failed\n\nThe provider requested multiple tool calls, but this profile/provider combination does not allow parallel tool calls.",
                "Provider requested multiple tool calls.",
                cancellationToken);
            return AgentToolCycleResult.Terminal(result);
        }

        var toolCalls = providerToolCalls.Select(CreateToolCallRequest).ToArray();
        var outcomes = await host.InvokeToolsAsync(toolCalls, assistantTurn: null, cancellationToken);
        var keyedOutcomes = outcomes
            .Take(toolCalls.Length)
            .Select((outcome, index) => new KeyValuePair<AgentToolCallRequest, AgentToolCallOutcome>(
                toolCalls[index],
                outcome));

        foreach (var keyedOutcome in keyedOutcomes)
        {
            var outcome = keyedOutcome.Value;
            if (outcome.Kind != AgentToolCallOutcomeKind.Executed)
            {
                var result = new AgentBehaviorLoopResult(
                    outcome.Checkpoint ?? context.RunningCheckpoint,
                    outcome.Kind == AgentToolCallOutcomeKind.WaitingForApproval
                        ? AgentBehaviorLoopCompletionKind.WaitingForApproval
                        : AgentBehaviorLoopCompletionKind.Failed);
                host.LogEvent(
                    AgentLogLevel.Information,
                    "behavior.loop.suspended",
                    result.CompletionKind.ToString(),
                    loopStopwatch.ElapsedMilliseconds);
                return AgentToolCycleResult.Terminal(result);
            }

            if (!progressGuard.RecordToolOutcome(keyedOutcome.Key, outcome, out var progressFailure))
            {
                continue;
            }

            assistantTurnState.Turn = null;
            var progressResult = await _terminalHandler.FailAsync(
                host,
                assistantTurnState,
                progressFailure.VisibleMessage,
                progressFailure.CheckpointSummary,
                cancellationToken);
            host.LogEvent(
                AgentLogLevel.Warning,
                "behavior.loop.no_progress_detected",
                progressFailure.CheckpointSummary,
                loopStopwatch.ElapsedMilliseconds,
                progressFailure.Attributes);
            return AgentToolCycleResult.Terminal(progressResult);
        }

        if (outcomes.Count < toolCalls.Length)
        {
            assistantTurnState.Turn = null;
            var result = await _terminalHandler.FailAsync(
                host,
                assistantTurnState,
                "### Agent run failed\n\nOne or more requested tool calls did not produce an outcome.",
                "Tool invocation produced no outcome.",
                cancellationToken);
            return AgentToolCycleResult.Terminal(result);
        }

        return AgentToolCycleResult.Continue;
    }

    private static AgentToolCallRequest CreateToolCallRequest(FunctionCallContent functionCall)
        => new(
            string.IsNullOrWhiteSpace(functionCall.CallId)
                ? Guid.NewGuid().ToString("N")
                : functionCall.CallId,
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
}

internal sealed record AgentToolCycleResult(
    bool ShouldContinue,
    AgentBehaviorLoopResult? TerminalResult)
{
    public static AgentToolCycleResult Continue { get; } = new(true, null);

    public static AgentToolCycleResult Terminal(AgentBehaviorLoopResult result) => new(false, result);
}
