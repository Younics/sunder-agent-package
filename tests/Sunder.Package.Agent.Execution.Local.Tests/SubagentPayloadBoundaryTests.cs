using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Package.Agent.Subagents.Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class SubagentPayloadBoundaryTests
{
    [Fact]
    public void StructuredParentPayload_OmitsChildResultBodyAndKeepsSessionReference()
    {
        using var scope = RegressionTestPackageScope.Create();
        var renderer = new SubagentBatchResultRenderer(
            new SubagentService(new SubagentStore(scope.Context)),
            new SubagentRequestParser(),
            new SubagentPermissionStatusAdapter(new RegressionTestExtensionCatalog()));
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
            new AgentChildRunResult(
                childSessionId,
                AgentRunStatus.Completed,
                new string('s', 3_000),
                Content: new string('x', 200_000),
                Title: "Child"),
            subagent,
            "Child",
            SubagentTaskResultState.Completed);
        var result = renderer.BuildBatchResult(
        [
            new SubagentTaskResult(
                SubagentTaskResultState.Completed,
                new AgentToolResult(
                    SubagentConstants.TaskToolId,
                    "Completed",
                    Content: "bounded parent text",
                    StructuredPayloadJson: payload,
                    BackendId: childSessionId.ToString("N"))),
        ]);

        using var document = JsonDocument.Parse(result.StructuredPayloadJson!);
        var task = Assert.Single(document.RootElement.GetProperty("tasks").EnumerateArray());
        Assert.Equal(childSessionId, task.GetProperty("childSessionId").GetGuid());
        Assert.False(task.TryGetProperty("resultContent", out _));
        Assert.True(task.GetProperty("resultSummary").GetString()!.Length < 2_100);
        Assert.DoesNotContain(new string('x', 100), result.StructuredPayloadJson, StringComparison.Ordinal);
    }
}
