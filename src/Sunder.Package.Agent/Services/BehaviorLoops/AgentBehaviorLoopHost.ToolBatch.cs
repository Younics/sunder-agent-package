using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal sealed class AgentToolBatchCoordinator(AgentBehaviorLoopHost host)
{
    private readonly AgentBehaviorLoopHost _host = host;

    public async ValueTask<AgentToolCallOutcome> InvokeToolAsync(
        AgentToolCallRequest toolCall,
        AgentTurnRecord? assistantTurn,
        CancellationToken cancellationToken = default)
    {
        var outcomes = await InvokeToolsAsync([toolCall], assistantTurn, cancellationToken);
        if (outcomes.Count > 0)
        {
            return outcomes[0];
        }

        var failedCheckpoint = _host.SaveCheckpoint(
            AgentRunStatus.Failed,
            "Tool invocation produced no outcome.");
        return new AgentToolCallOutcome(AgentToolCallOutcomeKind.Failed, failedCheckpoint);
    }

    public async ValueTask<IReadOnlyList<AgentToolCallOutcome>> InvokeToolsAsync(
        IReadOnlyList<AgentToolCallRequest> toolCalls,
        AgentTurnRecord? assistantTurn,
        CancellationToken cancellationToken = default)
    {
        if (toolCalls.Count == 0)
        {
            return [];
        }

        var availableToolsById = await _host.GetAvailableToolsByIdAsync(cancellationToken);
        await _host.PrepareToolExecutionsAsync(toolCalls, availableToolsById, cancellationToken);
        var outcomes = new AgentToolCallOutcome?[toolCalls.Count];
        var nextToolCallIndex = 0;
        foreach (var batch in AgentBehaviorLoopHost.BuildToolExecutionBatches(toolCalls, availableToolsById))
        {
            var batchStartIndex = nextToolCallIndex;
            nextToolCallIndex += batch.Count;
            for (var batchIndex = 0; batchIndex < batch.Count; batchIndex++)
            {
                var toolCall = batch[batchIndex];
                var permissionOutcome = await _host.EvaluateToolPermissionAsync(
                    toolCall,
                    assistantTurn,
                    availableToolsById,
                    cancellationToken);
                if (permissionOutcome is not null)
                {
                    outcomes[batchStartIndex + batchIndex] = permissionOutcome;
                    PairUnexecutedCalls(toolCalls, outcomes, batchStartIndex + batchIndex, permissionOutcome);
                    return outcomes.Select(outcome => outcome!).ToArray();
                }
            }

            foreach (var toolCall in batch)
            {
                _host.RecordToolCallStart(toolCall);
            }

            var executedResults = await _host.ExecuteToolBatchAsync(
                batch,
                availableToolsById,
                cancellationToken);
            var recordingOrder = Enumerable.Range(0, executedResults.Count)
                .OrderBy(index => AgentBehaviorLoopHost.RequiresTerminalRecordingLast(executedResults[index]));
            var terminalOutcomeRecorded = false;
            foreach (var index in recordingOrder)
            {
                var outcome = await _host.RecordExecutedToolResultAsync(
                    executedResults[index],
                    cancellationToken);
                outcomes[batchStartIndex + index] = outcome;
                if (outcome.Kind != AgentToolCallOutcomeKind.Executed)
                {
                    terminalOutcomeRecorded = true;
                    break;
                }
            }

            if (terminalOutcomeRecorded)
            {
                for (var index = 0; index < toolCalls.Count; index++)
                {
                    if (outcomes[index] is null)
                    {
                        outcomes[index] = _host.RecordUnexecutedToolCall(
                            toolCalls[index],
                            "Tool result was not recorded because another call in the provider batch suspended the run.");
                    }
                }

                return outcomes.Select(outcome => outcome!).ToArray();
            }
        }

        return outcomes.Select(outcome => outcome
            ?? throw new InvalidOperationException("Tool batch outcome was not populated.")).ToArray();
    }

    private void PairUnexecutedCalls(
        IReadOnlyList<AgentToolCallRequest> toolCalls,
        AgentToolCallOutcome?[] outcomes,
        int terminalIndex,
        AgentToolCallOutcome terminalOutcome)
    {
        for (var index = 0; index < toolCalls.Count; index++)
        {
            if (outcomes[index] is null && index != terminalIndex)
            {
                outcomes[index] = _host.RecordUnexecutedToolCall(
                    toolCalls[index],
                    terminalOutcome.Kind == AgentToolCallOutcomeKind.WaitingForApproval
                        ? "Tool call was not executed because another call in the provider batch is waiting for permission."
                        : "Tool call was not executed because another call in the provider batch terminated the run.");
            }
        }
    }

}
