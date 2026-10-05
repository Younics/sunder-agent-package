using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias SubagentsWorker;
using SubagentsWorker::Sunder.Package.Agent.Contracts.Models;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Models;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Services;

public sealed class SubagentTaskResultPresentationTests
{
    [Fact]
    public void BatchResultContent_RendersWaitingTaskAsWaiting()
    {
        var result = new AgentToolResult(
            "delegate_tasks",
            "Waiting for approval.",
            Content: "The child is waiting.",
            BackendId: Guid.NewGuid().ToString("N"));

        var content = SubagentFeature.BuildDelegateTasksResultContent(
            [new SubagentTaskResult(SubagentTaskResultState.Waiting, result)]);

        Assert.Contains("status=\"waiting\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("status=\"completed\"", content, StringComparison.Ordinal);
    }
}
