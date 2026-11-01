extern alias AgentCore;

using AgentCore::Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Storage;
using Xunit;
using AgentChatSelectionStateService = AgentCore::Sunder.Package.Agent.Services.AgentChatSelectionStateService;

namespace Sunder.Package.Agent.Tests;

[Collection("Runtime correctness")]
public sealed class ChatSelectionStateTests
{
    [Fact]
    public async Task ChatSelection_UsesPortablePhysicalKeyForImportedWorkspaceId()
    {
        using var scope = RegressionTestPackageScope.Create();
        var workspaceId = " imported:/\u65E5\u672C\u8A9E workspace/" + new string('w', 300);
        var sessionId = Guid.NewGuid();
        var selections = new AgentChatSelectionStateService(scope.Context);

        await selections.SaveSelectedSessionIdAsync(workspaceId, sessionId);

        Assert.Equal(sessionId, await selections.GetSelectedSessionIdAsync(workspaceId));
        var key = Assert.Single(await scope.Context.Storage.State.ListKeysAsync());
        Assert.True(PackageStorageValidation.IsValidKey(key));
        Assert.Equal(
            PackageStorageKeyFactory.Create("agent.chat.selected-session", 1, workspaceId.Trim()),
            key);
    }

    [Fact]
    public async Task ChatSelection_IsWorkspaceScopedIncludingUnassignedAndNeverWritesGlobalSession()
    {
        Assert.Equal(AgentWorkspaceService.UnassignedSessionsWorkspaceId, AgentWorkspaceDefaults.UnassignedSessionsWorkspaceId);
        using var scope = RegressionTestPackageScope.Create();
        var selections = new AgentChatSelectionStateService(scope.Context);
        var first = Guid.NewGuid();
        var unassigned = Guid.NewGuid();
        await selections.SaveSelectedSessionIdAsync("workspace", first);
        await selections.SaveSelectedSessionIdAsync(AgentWorkspaceService.UnassignedSessionsWorkspaceId, unassigned);
        await selections.SaveSelectedSessionIdAsync((string?)null, Guid.NewGuid());

        Assert.Equal(first, await selections.GetSelectedSessionIdAsync("workspace"));
        Assert.Equal(unassigned, await selections.GetSelectedSessionIdAsync(AgentWorkspaceService.UnassignedSessionsWorkspaceId));
        Assert.Null(await selections.GetSelectedSessionIdAsync((string?)null));
        Assert.Null(await scope.Context.Storage.State.GetValueAsync("agent.chat.selectedSessionId"));
    }
}
