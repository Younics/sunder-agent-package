using System.Diagnostics;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal sealed class AgentLoopTerminalHandler
{
    public async Task<AgentBehaviorLoopResult> CompleteAsync(
        IAgentBehaviorLoopRuntime host,
        AgentAssistantTurnState assistantTurnState,
        string responseContent,
        Stopwatch loopStopwatch,
        CancellationToken cancellationToken)
    {
        if (responseContent.Length == 0)
        {
            if (assistantTurnState.Turn is not null)
            {
                assistantTurnState.Turn = host.UpsertAssistantTurn(
                    assistantTurnState.Turn,
                    "No visible assistant response was produced.");
                assistantTurnState.Turn = host.CompleteAssistantTurn(assistantTurnState.Turn);
            }

            var emptyCheckpoint = host.SaveCheckpoint(
                AgentRunStatus.Completed,
                "No visible assistant response was produced.");
            await host.PublishLifecycleEventAsync(
                AgentLifecycleEventKind.AssistantTurnCompleted,
                AgentRunStatus.Completed,
                checkpoint: emptyCheckpoint,
                cancellationToken: cancellationToken);
            host.LogEvent(
                PackageLogLevel.Information,
                "behavior.loop.completed",
                "No visible assistant response was produced.",
                loopStopwatch.ElapsedMilliseconds);
            return new AgentBehaviorLoopResult(emptyCheckpoint, AgentBehaviorLoopCompletionKind.Completed);
        }

        assistantTurnState.Turn = host.UpsertAssistantTurn(assistantTurnState.Turn, responseContent);
        assistantTurnState.Turn = host.CompleteAssistantTurn(assistantTurnState.Turn);
        host.LogEvent(
            PackageLogLevel.Information,
            "assistant.response.completed",
            $"{responseContent.Length} characters",
            loopStopwatch.ElapsedMilliseconds,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["assistant.response_length"] = responseContent.Length,
                ["assistant.is_error"] = false,
                ["assistant.error_code"] = null,
            });
        var checkpoint = host.SaveCheckpoint(AgentRunStatus.Completed, "Assistant response saved.");
        await host.PublishLifecycleEventAsync(
            AgentLifecycleEventKind.AssistantTurnCompleted,
            AgentRunStatus.Completed,
            triggerTurn: assistantTurnState.Turn,
            checkpoint: checkpoint,
            cancellationToken: cancellationToken);
        var result = new AgentBehaviorLoopResult(checkpoint, ToCompletionKind(checkpoint.Status));
        host.LogEvent(
            PackageLogLevel.Information,
            "behavior.loop.completed",
            result.CompletionKind.ToString(),
            loopStopwatch.ElapsedMilliseconds);
        return result;
    }

    public async Task<AgentBehaviorLoopResult> FailAsync(
        IAgentBehaviorLoopRuntime host,
        AgentAssistantTurnState assistantTurnState,
        string visibleContent,
        string checkpointSummary,
        CancellationToken cancellationToken)
    {
        assistantTurnState.Turn = host.UpsertAssistantTurn(assistantTurnState.Turn, visibleContent);
        assistantTurnState.Turn = host.CompleteAssistantTurn(assistantTurnState.Turn);
        var checkpoint = host.SaveCheckpoint(AgentRunStatus.Failed, checkpointSummary);
        await host.PublishLifecycleEventAsync(
            AgentLifecycleEventKind.RunFailed,
            AgentRunStatus.Failed,
            triggerTurn: assistantTurnState.Turn,
            checkpoint: checkpoint,
            cancellationToken: cancellationToken);
        return new AgentBehaviorLoopResult(checkpoint, AgentBehaviorLoopCompletionKind.Failed);
    }

    public async Task<AgentBehaviorLoopResult> HandleProviderInterruptedAsync(
        IAgentBehaviorLoopRuntime host,
        AgentBehaviorLoopContext context,
        AgentAssistantTurnState assistantTurnState,
        string message,
        long elapsedMilliseconds,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (!host.IsCurrentRun())
        {
            host.LogEvent(
                PackageLogLevel.Information,
                "behavior.loop.interrupted",
                "Run was replaced or stopped after transient provider failure.",
                elapsedMilliseconds);
            return new AgentBehaviorLoopResult(
                context.RunningCheckpoint,
                AgentBehaviorLoopCompletionKind.Interrupted);
        }

        var interruptedTurn = host.UpsertAssistantTurn(
            assistantTurnState.Turn,
            $"### Provider connection interrupted\n\nThe provider connection was interrupted after retrying. You can retry or continue this session.\n\n{message}");
        interruptedTurn = host.CompleteAssistantTurn(interruptedTurn);
        assistantTurnState.Turn = interruptedTurn;
        var checkpoint = host.SaveCheckpoint(
            AgentRunStatus.Interrupted,
            "Provider connection was interrupted after retrying.");
        await host.PublishLifecycleEventAsync(
            AgentLifecycleEventKind.RunInterrupted,
            AgentRunStatus.Interrupted,
            triggerTurn: interruptedTurn,
            checkpoint: checkpoint,
            isInterrupted: true,
            cancellationToken: cancellationToken);
        host.LogEvent(
            PackageLogLevel.Warning,
            "behavior.loop.interrupted",
            message,
            elapsedMilliseconds,
            exception: exception);
        return new AgentBehaviorLoopResult(checkpoint, AgentBehaviorLoopCompletionKind.Interrupted);
    }

    public async Task<AgentBehaviorLoopResult> HandleProviderFailureAsync(
        IAgentBehaviorLoopRuntime host,
        AgentBehaviorLoopContext context,
        AgentAssistantTurnState assistantTurnState,
        AgentChatProviderException exception,
        long elapsedMilliseconds)
    {
        if (!host.IsCurrentRun())
        {
            host.LogEvent(
                PackageLogLevel.Information,
                "behavior.loop.interrupted",
                "Run was replaced or stopped after provider failure.",
                elapsedMilliseconds);
            return new AgentBehaviorLoopResult(
                context.RunningCheckpoint,
                AgentBehaviorLoopCompletionKind.Interrupted);
        }

        var result = await FailAsync(
            host,
            assistantTurnState,
            exception.Content,
            exception.ErrorCode ?? exception.Message,
            CancellationToken.None);
        host.LogEvent(
            PackageLogLevel.Error,
            "provider.request.failed",
            exception.ErrorCode ?? exception.Message,
            elapsedMilliseconds,
            exception: exception);
        return result;
    }

    public async Task<AgentBehaviorLoopResult> HandleFailureAsync(
        IAgentBehaviorLoopRuntime host,
        AgentBehaviorLoopContext context,
        AgentAssistantTurnState assistantTurnState,
        Exception exception,
        long elapsedMilliseconds)
    {
        if (!host.IsCurrentRun())
        {
            host.LogEvent(
                PackageLogLevel.Information,
                "behavior.loop.interrupted",
                "Run was replaced or stopped after failure.",
                elapsedMilliseconds);
            return new AgentBehaviorLoopResult(
                context.RunningCheckpoint,
                AgentBehaviorLoopCompletionKind.Interrupted);
        }

        var result = await FailAsync(
            host,
            assistantTurnState,
            $"### Agent run failed\n\n{exception.Message}",
            exception.Message,
            CancellationToken.None);
        host.LogEvent(
            PackageLogLevel.Error,
            "behavior.loop.failed",
            exception.Message,
            elapsedMilliseconds,
            exception: exception);
        return result;
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
}

internal sealed class AgentAssistantTurnState
{
    public AgentTurnRecord? Turn { get; set; }
}
