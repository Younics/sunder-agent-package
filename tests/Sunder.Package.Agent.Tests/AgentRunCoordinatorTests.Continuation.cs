using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentRunCoordinatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputLimit_PreservesPartialTextAndContinuesWithoutDispatchingCutOffCalls(bool repeats)
    {
        const string toolId = "read_file";
        var tool = new TestTool(toolId);
        var provider = new ScriptedProvider((request, index) =>
        {
            if (index > 1)
            {
                Assert.Contains(request.Turns, turn => RenderTurnText(turn) == "Partial section 1");
                Assert.True(RequestContainsUserText(request, "output-token limit"));
            }
            if (index == 1 || repeats)
                return [Delta($"Partial section {index}"), ToolRequest($"cut-off-{index}", toolId, "{}")];
            return [Complete("The remaining answer.")];
        });
        provider.FinishReasonHandler = index => index == 1 || repeats ? ChatFinishReason.Length : ChatFinishReason.Stop;
        using var runtime = AgentTestRuntime.Create(provider, tool);
        var session = await runtime.CreateSessionAsync(toolId);

        var result = await runtime.RunCoordinator.QueueUserMessageAsync(session, runtime.CurrentProfileId,
            "Produce the requested work.", runtime.CurrentWorkspaceId);

        Assert.Equal(repeats ? AgentRunStatus.Interrupted : AgentRunStatus.Completed, result.Status);
        Assert.Equal(repeats ? 3 : 2, provider.Requests.Count);
        Assert.Equal(0, tool.ExecutionCount);
        Assert.Empty(runtime.Store.ListToolExecutions(session));
        var turns = runtime.SessionService.ListTurns(session);
        Assert.Contains(turns, turn => RenderTurnText(turn) == "Partial section 1");
        Assert.All(turns, turn => Assert.False(turn.IsStreaming));
    }

    [Fact]
    public async Task TransientContinuation_RetainsCompletedReceiptAndNeverDispatchesAnIncompleteToolBatch()
    {
        const string toolId = "read_file";
        var tool = new TestTool(toolId);
        var provider = new ScriptedProvider((request, index) => index switch
        {
            1 => [ToolRequest("completed-read", toolId, "{}")],
            2 => [ReasoningDelta("Reviewing the completed read."), Delta("Saved partial section"),
                ToolRequest("incomplete-read", toolId, "{}"), TransientStreamError()],
            3 => [Complete("Continued section")],
            _ => throw new Xunit.Sdk.XunitException("Unexpected provider request."),
        });
        // Announcing a call is not a completed response: let the fixture emit
        // the following transport failure before the batch can be accepted.
        provider.ContinueAfterToolCall = true;
        using var runtime = AgentTestRuntime.Create(provider, tool);
        var sessionId = await runtime.CreateSessionAsync(toolId);
        var result = await runtime.RunCoordinator.QueueUserMessageAsync(sessionId, runtime.CurrentProfileId,
            "Read and explain.", runtime.CurrentWorkspaceId);
        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(3, provider.Requests.Count);
        Assert.Equal(1, tool.ExecutionCount);
        Assert.Single(runtime.Store.ListToolExecutions(sessionId));
        var finalRequest = provider.Requests[^1];
        AssertNoUnpairedToolItems(finalRequest);
        Assert.Single(finalRequest.Turns.SelectMany(turn => turn.Items), item =>
            item.Kind == AgentTurnItemKind.ToolResult && item.CallId == "completed-read");
        Assert.DoesNotContain(finalRequest.Turns.SelectMany(turn => turn.Items), item => item.CallId == "incomplete-read");
        Assert.Contains(finalRequest.Turns, turn => RenderTurnText(turn) == "Saved partial section");
        Assert.All(runtime.SessionService.ListTurns(sessionId), turn => Assert.False(turn.IsStreaming));
    }

    [Fact]
    public async Task ContextOverflow_EachLaterProductiveStepCanRecoverIndependently()
    {
        const string toolId = "read_file";
        var provider = new ScriptedProvider((_, index) => index switch
        {
            1 or 3 => ContextWindowError(),
            2 => ToolRequest("recovered-call", toolId, "{}"),
            4 => Complete("Finished after two separate recoveries."),
            _ => throw new Xunit.Sdk.XunitException("Unexpected recovery request."),
        });
        using var runtime = AgentTestRuntime.Create(provider, new TestTool(toolId));
        var session = await runtime.CreateSessionAsync(toolId);
        for (var index = 0; index < 40; index++)
            runtime.SessionService.AppendTextTurn(session, index % 2 == 0 ? AgentMessageRole.User : AgentMessageRole.Assistant,
                $"history-{index}-" + new string('x', 1_000));

        var result = await runtime.RunCoordinator.QueueUserMessageAsync(session, runtime.CurrentProfileId,
            "Continue through context recovery.", runtime.CurrentWorkspaceId);

        Assert.True(result.Status == AgentRunStatus.Completed, result.Summary);
        Assert.Equal(4, provider.Requests.Count);
        Assert.Single(runtime.Store.ListToolExecutions(session));
        AssertNoUnpairedToolItems(provider.Requests[^1]);
    }

    [Fact]
    public async Task IdenticalSuccessfulObservationsDoNotTerminateTheRun()
    {
        const string toolId = "observe";
        var provider = new ScriptedProvider((_, index) => index <= 40
            ? ToolRequest($"observation-{index}", toolId, "{}") : Complete("Observation finished."));
        using var runtime = AgentTestRuntime.Create(provider, new TestTool(toolId));
        var session = await runtime.CreateSessionAsync(toolId);

        var result = await runtime.RunCoordinator.QueueUserMessageAsync(session, runtime.CurrentProfileId,
            "Observe the operation.", runtime.CurrentWorkspaceId);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(41, provider.Requests.Count);
    }

    [Theory]
    [InlineData("content_filter")]
    [InlineData("unknown")]
    public async Task UnsuccessfulFinishNeverDispatchesPendingToolsOrOverwritesPartialText(string reason)
    {
        var provider = new ScriptedProvider((_, _) => [Delta("Partial"), ToolRequest("not-authorized", "read_file", "{}")]);
        provider.FinishReasonHandler = _ => new ChatFinishReason(reason);
        var tool = new TestTool("read_file");
        using var runtime = AgentTestRuntime.Create(provider, tool);
        var session = await runtime.CreateSessionAsync("read_file");

        var result = await runtime.RunCoordinator.QueueUserMessageAsync(session, runtime.CurrentProfileId,
            "Perform the requested work.", runtime.CurrentWorkspaceId);

        Assert.Equal(AgentRunStatus.Interrupted, result.Status);
        Assert.Single(provider.Requests);
        Assert.Equal(0, tool.ExecutionCount);
        Assert.Empty(runtime.Store.ListToolExecutions(session));
        Assert.Contains(runtime.SessionService.ListTurns(session), turn => RenderTurnText(turn) == "Partial");
    }

    [Fact]
    public async Task StopDuringTransientRecoveryPreventsTheNextProviderAttempt()
    {
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider((_, index) =>
        {
            Assert.Equal(1, index);
            failed.TrySetResult();
            return TransientStreamError();
        });
        using var runtime = AgentTestRuntime.Create(provider);
        var session = await runtime.CreateSessionAsync("noop");
        var execution = runtime.RunCoordinator.QueueUserMessageAsync(session, runtime.CurrentProfileId,
            "Recover if possible.", runtime.CurrentWorkspaceId);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var stopped = await runtime.RunCoordinator.StopAsync(session);
        await execution.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AgentRunStatus.Stopped, stopped?.Status);
        Assert.Equal(AgentRunStatus.Stopped, runtime.SessionService.GetLatestCheckpoint(session)?.Status);
        Assert.Single(provider.Requests);
    }
}
