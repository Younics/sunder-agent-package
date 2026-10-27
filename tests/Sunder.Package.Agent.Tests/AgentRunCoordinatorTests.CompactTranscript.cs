extern alias AgentCore;
extern alias SubagentsApp;

using Sunder.Package.Agent.Contracts.Models;
using Xunit;
using AppViews = AgentCore::Sunder.Package.Agent.PackageViews;
using AppHistory = AgentCore::Sunder.Package.Agent.HistorySearch;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentRunCoordinatorTests
{
    private static async Task RevealCompactGroupsAsync(AppViews.AgentChatViewModel model)
    {
        await model.PendingTranscriptActivities.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var group in model.Messages.OfType<AppViews.AgentActivityGroupRowViewModel>().ToArray())
            group.IsExpanded = true;
        await model.PendingTranscriptActivities.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task RevealCompactGroupsAsync(SubagentsApp::Sunder.Package.Agent.Subagents.PackageViews.SubsessionsViewModel model)
    {
        await model.PendingTranscriptActivities.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var group in model.Messages.OfType<SubagentsApp::Sunder.Package.Agent.Subagents.PackageViews.SubsessionActivityGroupRowViewModel>().ToArray())
            group.IsExpanded = true;
        await model.PendingTranscriptActivities.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AgentChat_CompactHistoryNavigationFindsDelayedResultInsideACompleteLargeGroup()
    {
        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("unused")));
        var session = await runtime.CreateSessionAsync("noop");
        var calls = Enumerable.Range(0, 123).Select(index => runtime.SessionService.AppendToolCallTurn(
            session, AgentMessageRole.Assistant, "compact-" + index, "read", "{\"path\":\"file.txt\"}")).ToArray();
        var targetCall = calls[111].Items.Single();
        var result = runtime.SessionService.AppendToolResultTurn(session, targetCall.CallId!, "read", "{}", "Lazy result canary", "Done",
            null, null, false, false, null, null);
        for (var index = 0; index < 80; index++)
            runtime.SessionService.AppendTextTurn(session, AgentMessageRole.Assistant, "Later message " + index);
        using var model = RegressionTestAppViews.Chat(runtime.ProfileService, runtime.WorkspaceService, runtime.SessionService,
            runtime.PermissionService, runtime.RunCoordinator);
        await model.InitializeAsync();
        Assert.Empty(model.Messages.OfType<AppViews.AgentActivityGroupRowViewModel>());
        var exact = new BlockingTranscriptAnchorGateway(runtime.SessionService.GetTranscriptHeader(result.TurnId)!);
        exact.ReleaseLoad.SetResult();
        model.SetTranscriptAnchorGateway(exact);
        AgentCore::Sunder.Package.Agent.PackageViews.AgentHistoryNavigationPlacement? placement = null;
        var failure = await Record.ExceptionAsync(async () => placement = await model.NavigateToHistoryAnchorAsync(new AppHistory.HistorySearchNavigationTarget(
            runtime.CurrentWorkspaceId, session, result.TurnId, result.Items.Single().ItemId, targetCall.CallId,
            AppHistory.HistoryAnchorKind.Activity, result.CreatedAtUtc), default));
        Assert.True(failure is null, $"{failure}; status={model.StatusText}; rows={model.Messages.Count}; loading={model.IsTranscriptLoading}; "
            + string.Join(" | ", model.Messages.OfType<AppViews.AgentActivityGroupRowViewModel>().Select(group => $"{group.Summary}: {group.LoadingText}, expanded={group.IsExpanded}")));
        Assert.NotNull(placement);
        var group = Assert.Single(model.Messages.OfType<AppViews.AgentActivityGroupRowViewModel>());
        Assert.Equal("123 Reads", group.Summary);
        Assert.True(group.IsExpanded);
        var tools = model.Messages.OfType<AppViews.AgentToolInvocationRowViewModel>().ToArray();
        Assert.Equal(123, tools.Length);
        Assert.All(tools, tool => Assert.False(tool.HasMaterializedDetails));
        var selected = Assert.Single(tools, tool => Equals(tool.AnchorKey, placement.AnchorKey));
        Assert.Contains(targetCall.ToolExecutionId!.Value.ToString("N"), selected.AnchorKey.ToString());
        group.IsExpanded = false;
        Assert.Empty(model.Messages.OfType<AppViews.AgentToolInvocationRowViewModel>());
        Assert.Equal(group.AnchorKey, model.ResolveVisibleTranscriptAnchor(placement.AnchorKey));
    }
}
