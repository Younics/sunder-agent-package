using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    // Exercise domain entry points against the existing frozen-wire request/output
    // oracle. This catches mapping errors that a direct generated-client test cannot.
    private static async Task<JsonElement> InvokeRunControlRuntime(IAgentBehaviorLoopRuntime runtime, string route, JsonElement request)
    {
        object result = new { };
        switch (route)
        {
            case "is-current": result = runtime.IsCurrentRun(); break;
            case "list-turns": result = runtime.ListTurns(); break;
            case "list-recent-turns": result = runtime.ListRecentTurns(Read<RunControlRecentTurnsRequest>().Limit); break;
            case "list-ready-tools": result = (await runtime.ListReadyToolsAsync()).Select(tool => tool.Descriptor).ToArray(); break;
            case "build-instruction-context": result = await runtime.BuildInstructionContextAsync(); break;
            case "select-session-context": ((IAgentSessionContextSelectionRuntime)runtime).SelectSessionContextProjection(Read<RunControlSessionProjectionRequest>().Projection); break;
            case "report-activity":
                var activity = Read<RunControlActivityRequest>(); ((IAgentRunActivitySink)runtime).ReportRunActivity(activity.Kind, activity.Text); break;
            case "get-run-budget": result = ((IAgentRunBudgetRuntime)runtime).GetRunBudgetState(); break;
            case "charge-run-budget": result = ((IAgentRunBudgetRuntime)runtime).ChargeRunBudget(Read<RunControlBudgetChargeRequest>().Charge); break;
            case "acknowledge-prompt-context": await ((IAgentPromptContextAcknowledgmentRuntime)runtime).AcknowledgePromptContextAsync(Read<RunControlPromptContextRequest>().Blocks, default); break;
            case "discard-prompt-context": ((IAgentPromptContextAcknowledgmentRuntime)runtime).DiscardPromptContextAcknowledgment(); break;
            case "save-checkpoint":
                var checkpoint = Read<RunControlCheckpointRequest>(); result = runtime.SaveCheckpoint(checkpoint.Status, checkpoint.Summary); break;
            case "log-event":
                var log = Read<RunControlLogRequest>();
                runtime.LogEvent(log.Level, log.EventName, log.Message, log.ElapsedMilliseconds,
                    log.Attributes?.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal)); break;
            case "upsert-assistant-turn":
                var turn = Read<RunControlAssistantTurnRequest>(); result = runtime.UpsertAssistantTurn(turn.AssistantTurn, turn.Content); break;
            case "complete-assistant-turn": result = runtime.CompleteAssistantTurn(Read<RunControlCompleteTurnRequest>().AssistantTurn); break;
            case "publish-lifecycle":
                var lifecycle = Read<RunControlLifecycleRequest>();
                await runtime.PublishLifecycleEventAsync(lifecycle.Kind, lifecycle.Status, lifecycle.TriggerTurn, lifecycle.Checkpoint, lifecycle.IsInterrupted); break;
            case "invoke-tool":
                var tool = Read<RunControlToolRequest>(); result = await runtime.InvokeToolAsync(tool.ToolCall, tool.AssistantTurn); break;
            case "invoke-tools":
                var tools = Read<RunControlToolsRequest>(); result = await runtime.InvokeToolsAsync(tools.ToolCalls, tools.AssistantTurn); break;
            case "run-default": result = await ((IAgentInnerBehaviorLoopRuntime)runtime).RunDefaultLoopAsync(Read<RunControlDefaultLoopRequest>().Context); break;
            default: throw new InvalidOperationException("Uncovered callback route: " + route);
        }
        return RunJson(result);
        T Read<T>() => request.Deserialize<T>(ExecutionJson)!;
    }
}
