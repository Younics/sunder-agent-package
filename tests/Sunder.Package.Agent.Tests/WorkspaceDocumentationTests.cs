using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Services;
using Xunit;
using static Sunder.Package.Agent.Tests.WorkspaceStorageFixture;

namespace Sunder.Package.Agent.Tests;

[Collection("Workspace component behavior")]
public sealed class WorkspaceDocumentationTests
{
    [Fact]
    public async Task WorkspaceDocumentationContextService_ContributeContextAsync_LoadsOnlyExplicitDocsInConfiguredOrder()
    {
        using var scope = TestScope.Create();
        var workspaceRoot = Path.Combine(scope.RootPath, "repo");
        var docsRoot = Path.Combine(workspaceRoot, ".sunder", "docs");
        var explicitDocumentPath = Path.Combine(scope.RootPath, "project-guide.md");
        var autoDocumentPath = Path.Combine(docsRoot, "implicit-guide.md");
        var explicitNestedDocumentPath = Path.Combine(docsRoot, "explicit-guide.md");
        Directory.CreateDirectory(docsRoot);
        File.WriteAllText(explicitDocumentPath, "Explicit project guidance.");
        File.WriteAllText(autoDocumentPath, "Auto workspace guidance.");
        File.WriteAllText(explicitNestedDocumentPath, "Explicit nested guidance.");
        var now = DateTimeOffset.UtcNow;
        var workspace = new AgentWorkspaceRecord(
            "workspace.docs",
            "Docs Workspace",
            null,
            now,
            now,
            [new AgentWorkspacePathRecord("path", "workspace.docs", workspaceRoot, true, 0, now, now)],
            [
                new AgentWorkspaceDocumentRecord("doc-nested", "workspace.docs", explicitNestedDocumentPath, 0, now, now),
                new AgentWorkspaceDocumentRecord("doc", "workspace.docs", explicitDocumentPath, 1, now, now),
            ]);
        var service = new WorkspaceDocumentationContextService();

        var contribution = await service.ContributeContextAsync(CreatePromptContextRequest(workspace));

        var block = Assert.Single(Assert.IsType<AgentPromptContextContribution>(contribution).Blocks);
        Assert.Equal("Workspace Documentation", block.Title);
        Assert.Equal(AgentPromptContextUsage.Reference, block.Usage);
        Assert.True(
            block.Content.IndexOf("explicit-guide.md", StringComparison.Ordinal)
            < block.Content.IndexOf("project-guide.md", StringComparison.Ordinal));
        Assert.Contains("project-guide.md", block.Content, StringComparison.Ordinal);
        Assert.Contains("Explicit project guidance.", block.Content, StringComparison.Ordinal);
        Assert.Contains("explicit-guide.md", block.Content, StringComparison.Ordinal);
        Assert.Contains("Explicit nested guidance.", block.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("implicit-guide.md", block.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Auto workspace guidance.", block.Content, StringComparison.Ordinal);
    }

    private static AgentPromptContextRequest CreatePromptContextRequest(AgentWorkspaceRecord? workspace)
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new AgentProfileRecord(
            "profile", "Test Profile", null, null, "provider", "model", null, null, now, now, [], []);
        var session = new AgentSessionContextRecord(
            Guid.NewGuid(), profile.ProfileId, profile.DisplayName, "Test Session", AgentSessionState.Active);
        var run = new AgentRunContextRecord(Guid.NewGuid(), 1, AgentRunStatus.Running, IsInterrupted: false, now);
        return new AgentPromptContextRequest(
            session,
            run,
            new AgentTurnContextRecord(session, run, "Test user message."),
            [],
            [],
            new AgentPromptContextPlan("general", "Test user message."))
        {
            Profile = profile,
            Workspace = workspace,
        };
    }
}
