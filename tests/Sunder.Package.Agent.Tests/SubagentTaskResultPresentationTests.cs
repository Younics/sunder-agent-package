using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias SubagentsWorker;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Models;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Services;
using AgentToolResult = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentToolResult;

public sealed class SubagentTaskResultPresentationTests
{
    [Fact]
    public void BatchResultContent_RendersWaitingTaskAsWaiting()
    {
        var result = new AgentToolResult
        {
            ToolId = "delegate_tasks",
            Summary = "Waiting for approval.",
            Content = "The child is waiting.",
            BackendId = Guid.NewGuid().ToString("N"),
            IsError = false,
            RequiresPromptContextRefresh = false,
            WasTruncated = false,
        };

        var content = SubagentFeature.BuildDelegateTasksResultContent(
            [new SubagentTaskResult(SubagentTaskResultState.Waiting, result)]);

        Assert.Contains("status=\"waiting\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("status=\"completed\"", content, StringComparison.Ordinal);
    }
}
