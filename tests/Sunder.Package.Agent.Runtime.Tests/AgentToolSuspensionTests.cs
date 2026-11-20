using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentToolSuspensionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("{invalid")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"tasks\":[null,42,{}]}")]
    [InlineData("{\"childSessionId\":\"00000000-0000-0000-0000-000000000000\",\"state\":\"waiting\"}")]
    public void BackendIdAloneCannotAuthorizeChildJoin(string? payload)
    {
        var result = WaitingResult(payload, Guid.NewGuid().ToString());

        Assert.False(AgentToolSuspensionCompatibility.TryCreateChildJoin(
            new AgentToolCallRequest("call", "task", "{}"), result, Guid.NewGuid(), out var suspension));
        Assert.Null(suspension);
    }

    [Fact]
    public void CurrentSingleAndBatchPayloadsPreserveOutstandingAndCompletedChildren()
    {
        var waitingId = Guid.NewGuid();
        var completedId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var call = new AgentToolCallRequest("call", "task", "{\"task\":\"review\"}");
        var waiting = new { childSessionId = waitingId, childSessionTitle = "Review", state = "waiting" };
        var singlePayload = JsonSerializer.Serialize(waiting);
        var batchPayload = JsonSerializer.Serialize(new
        {
            tasks = new object[]
            {
                waiting,
                new { childSessionId = completedId, state = "completed", resultSummary = "Done", resultContent = "Evidence" },
            },
        });

        Assert.True(AgentToolSuspensionCompatibility.TryCreateChildJoin(call, WaitingResult(singlePayload), turnId, out var single));
        Assert.Equal(waitingId, Assert.Single(single!.OutstandingTasks).ChildSessionId);
        Assert.Empty(single.CompletedTasks);
        Assert.True(AgentToolSuspensionCompatibility.TryCreateChildJoin(call, WaitingResult(batchPayload), turnId, out var batch));
        Assert.Equal(turnId, batch!.UserTurnId);
        Assert.Equal(call.ArgumentsJson, batch.ArgumentsJson);
        Assert.Equal("call", Assert.Single(batch.OutstandingTasks).ToolCallId);
        var completed = Assert.Single(batch.CompletedTasks);
        Assert.Equal(completedId, completed.ChildSessionId);
        Assert.Equal(AgentRunStatus.Completed, completed.Status);
        Assert.Equal("Evidence", completed.Content);

        var completedOnly = JsonSerializer.Serialize(new { childSessionId = completedId, state = "completed" });
        Assert.False(AgentToolSuspensionCompatibility.TryCreateChildJoin(
            call, WaitingResult(completedOnly, waitingId.ToString()), turnId, out _));
    }

    private static AgentToolResult WaitingResult(string? payload, string? backendId = null)
        => new("task", "Waiting", StructuredPayloadJson: payload, IsError: true,
            ErrorCode: AgentToolResultErrorCodes.ChildWaitingForApproval, BackendId: backendId);
}
