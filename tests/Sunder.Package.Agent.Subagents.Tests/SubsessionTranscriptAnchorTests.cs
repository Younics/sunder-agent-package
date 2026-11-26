extern alias SubagentsWorker;

using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Runtime;
using IRuntimeCatalogService = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Runtime_Catalog.IRuntimeCatalogService;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class SubsessionTranscriptAnchorTests
{
    [Fact]
    public async Task AroundTurn_ReadsChildAnchorAndRejectsRootSession()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var catalog = new RegressionTestExtensionCatalog();
        await using var services = RegressionTestRuntime.Create(scope.Context, catalog);
        var workspace = services.GetRequiredService<AgentWorkspaceService>().CreateWorkspace("Navigation");
        var sessions = services.GetRequiredService<AgentSessionService>();
        var root = sessions.CreateSession("Root", workspaceId: workspace.WorkspaceId);
        var child = sessions.CreateSession("Child", root.SessionId, root.SessionId, workspaceId: workspace.WorkspaceId);
        var rootTurn = sessions.AppendTextTurn(root.SessionId, AgentMessageRole.User, "root");
        var childTurn = sessions.AppendTextTurn(child.SessionId, AgentMessageRole.User, "child");
        catalog.AddRuntimeCatalog(services.GetRequiredService<AgentRuntimeCatalog>());
        var runtime = Assert.Single((await catalog.Subagents().Providers<IRuntimeCatalogService>().ListAsync()).Providers).Service;
        var around = await SubsessionTranscriptReader.AroundAsync(runtime, child.SessionId, childTurn.TurnId,
            childTurn.CreatedAtUtc, childTurn.Items[0].ItemId, 20, 20, CancellationToken.None);
        Assert.Equal(childTurn.TurnId, Assert.Single(around.Turns).TurnId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SubsessionTranscriptReader.AroundAsync(runtime,
            root.SessionId, rootTurn.TurnId, rootTurn.CreatedAtUtc, rootTurn.Items[0].ItemId, 10, 10, CancellationToken.None).AsTask());
    }
}
