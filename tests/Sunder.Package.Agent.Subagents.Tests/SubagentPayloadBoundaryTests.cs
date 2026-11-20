extern alias SubagentsWorker;

using System.Text.Json;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Models;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Services;
using Xunit;
using AgentModels = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using IToolSourceService = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Tool_Source.IToolSourceService;

namespace Sunder.Package.Agent.Tests;

public sealed class SubagentPayloadBoundaryTests
{
    [Fact]
    public void StructuredParentPayload_OmitsChildResultBodyAndKeepsSessionReference()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var renderer = new SubagentBatchResultRenderer(
            new SubagentService(new SubagentStore(scope.Context)),
            new SubagentRequestParser(),
            new SubagentPermissionStatusAdapter(
                catalog.Receiver("younics.ai.agent.subagents").Providers<IToolSourceService>()));
        var childSessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var subagent = new SubagentRecord(
            "reviewer",
            "Reviewer",
            "Reviews changes.",
            "Review carefully.",
            null,
            null,
            [],
            now,
            now);
        var payload = renderer.BuildChildSessionPayload(
            new AgentModels.AgentChildRunResult
            {
                SessionId = childSessionId,
                Status = AgentModels.AgentRunStatus.Completed,
                Summary = new string('s', 3_000),
                Content = new string('x', 200_000),
                Title = "Child",
            },
            subagent,
            "Child",
            SubagentTaskResultState.Completed);
        var result = renderer.BuildBatchResult(
        [
            new SubagentTaskResult(
                SubagentTaskResultState.Completed,
                new AgentModels.AgentToolResult
                {
                    ToolId = SubagentConstants.TaskToolId,
                    Summary = "Completed",
                    Content = "bounded parent text",
                    StructuredPayloadJson = payload,
                    BackendId = childSessionId.ToString("N"),
                    IsError = false,
                    RequiresPromptContextRefresh = false,
                    WasTruncated = false,
                }),
        ]);

        using var document = JsonDocument.Parse(result.StructuredPayloadJson!);
        var task = Assert.Single(document.RootElement.GetProperty("tasks").EnumerateArray());
        Assert.Equal(childSessionId, task.GetProperty("childSessionId").GetGuid());
        Assert.False(task.TryGetProperty("resultContent", out _));
        Assert.True(task.GetProperty("resultSummary").GetString()!.Length < 2_100);
        Assert.DoesNotContain(new string('x', 100), result.StructuredPayloadJson, StringComparison.Ordinal);
    }
}
