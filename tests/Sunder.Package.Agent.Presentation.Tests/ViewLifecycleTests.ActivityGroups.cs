extern alias AgentApp;
extern alias SubagentsApp;

using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AgentApp::Sunder.Package.Agent.PackageViews;
using AgentApp::Sunder.Package.Agent.Shared.Presentation;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using SubagentsApp::Sunder.Package.Agent.Subagents.PackageViews;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed partial class ViewLifecycleTests
{
    [AvaloniaFact]
    public async Task AgentActivityGroups_HistoricalOperationReceiptsDoNotKeepIdleHistoryAnimating()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profiles = services.ProfileService;
        await profiles.CreateProfileAsync("Historical operations");
        var workspace = services.WorkspaceService.CreateWorkspace("Historical operations");
        var session = services.SessionService.CreateSession("Historical operations", workspaceId: workspace.WorkspaceId);
        foreach (var call in new[] { "apply", "observe" })
        {
            services.SessionService.AppendToolCallTurn(session.SessionId, AgentMessageRole.Assistant, call, "maker_operation", "{}");
            services.SessionService.AppendToolResultTurn(session.SessionId, call, "maker_operation", "{}", "Original receipt", "Package operation running",
                null, null, false, false, null, null, "{\"operationStatus\":\"running\",\"operationRef\":\"o-original\"}");
        }
        services.SessionService.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, "The operation was submitted; its final effect has not been verified.");
        var original = JsonSerializer.Serialize(services.SessionService.ListTurns(session.SessionId));
        for (var reload = 0; reload < 2; reload++)
        {
            using var view = CreateAgentChatView(scope, services, profiles);
            var window = new Window { Width = 800, Height = 600, Content = view };
            window.Show();
            try
            {
                await view.OnNavigatedToAsync(new("younics.ai.agent.chat", new Dictionary<string, string?>()));
                await WaitForPendingCoordinatorOperationsAsync(view);
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                var model = Assert.IsType<AgentChatViewModel>(view.DataContext);
                var group = Assert.Single(model.TranscriptItems.OfType<AgentActivityGroupRowViewModel>());
                Assert.False(group.IsRunning);
                Assert.Equal("2 Unverified", group.StatusText);
                Assert.All(model.Messages.OfType<AgentToolInvocationRowViewModel>(), tool =>
                {
                    Assert.False(tool.IsRunning);
                    Assert.Equal("Running (last observed)", tool.StatusText);
                    Assert.False(tool.IsCompleted);
                });
                var header = Assert.Single(view.GetVisualDescendants().OfType<Button>(), button => ReferenceEquals(button.DataContext, group));
                Assert.DoesNotContain(header.GetVisualDescendants().OfType<ProgressBar>(), bar => bar.IsVisible || bar.IsIndeterminate);
            }
            finally { window.Close(); }
        }
        Assert.Equal(original, JsonSerializer.Serialize(services.SessionService.ListTurns(session.SessionId)));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentActivityGroups_KeepCallsLazyAndRevealExactAnchorsWithoutChangingHistory(bool withPaths)
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profiles = services.ProfileService;
        await profiles.CreateProfileAsync("Activity groups");
        var workspace = services.WorkspaceService.CreateWorkspace("Workspace");
        if (withPaths)
        {
            var paths = Enumerable.Range(0, 5).Select(index => Path.Combine(scope.RootPath, "long-workspace-path-" + index)).ToArray();
            foreach (var path in paths) Directory.CreateDirectory(path);
            services.WorkspaceService.SaveWorkspacePaths(workspace.WorkspaceId, paths.Select((path, index) =>
                new AgentWorkspacePathRecord(string.Empty, workspace.WorkspaceId, path, index == 0, index, default, default)).ToArray());
        }
        var session = services.SessionService.CreateSession("Session", workspaceId: workspace.WorkspaceId);
        services.SessionService.AppendTextTurn(session.SessionId, AgentMessageRole.User, "Inspect the selected workspace files.");
        services.SessionService.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, "I will inspect the files.");
        AppendMixedActivity(services.SessionService, session.SessionId);
        services.SessionService.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, "Inspection complete.");
        var history = JsonSerializer.Serialize(services.SessionService.ListTurns(session.SessionId));
        using var view = CreateAgentChatView(scope, services, profiles);
        var window = new Window { Width = 900, Height = 900, Content = view };
        window.Show();
        try
        {
            await view.OnNavigatedToAsync(new("younics.ai.agent.chat", new Dictionary<string, string?>()));
            await WaitForPendingCoordinatorOperationsAsync(view);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var model = Assert.IsType<AgentChatViewModel>(view.DataContext);
            var calls = model.Messages.OfType<AgentToolInvocationRowViewModel>().ToArray();
            var group = Assert.Single(model.TranscriptItems.OfType<AgentActivityGroupRowViewModel>());
            Assert.Equal(8, calls.Length); // A call and its result are one invocation.
            Assert.Equal("1 Read · 1 Glob · 1 Shell · 1 Web Search · 1 Edit · 1 Patch · 1 MCP Tool · 1 Web Fetch", group.Summary);
            Assert.Equal("1 Failed", group.StatusText);
            Assert.True(group.IsRunning);
            Assert.False(group.IsExpanded);
            Assert.All(calls, call => Assert.False(call.HasMaterializedDetails));
            Assert.Empty(FindToolDetailCards(view));
            var header = Assert.Single(view.GetVisualDescendants().OfType<Button>(), button => ReferenceEquals(button.DataContext, group));
            Assert.Equal(0, header.Margin.Left);
            Assert.Equal(HorizontalAlignment.Stretch, header.HorizontalAlignment);
            AssertActivityHeaderMatchesUserMessage(view, header);
            Assert.NotNull(Avalonia.Automation.AutomationProperties.GetHelpText(header));
            Assert.Single(header.GetVisualDescendants().OfType<ProgressBar>(), progress => progress.IsVisible && progress.IsIndeterminate);
            header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForPendingCoordinatorOperationsAsync(view);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.True(group.IsExpanded);
            Assert.All(calls, call => Assert.False(call.HasMaterializedDetails));

            var toolHeader = Assert.Single(view.GetVisualDescendants().OfType<Button>(), button =>
                button.Classes.Contains("tool-step-header") && ReferenceEquals(button.DataContext, calls[0]));
            toolHeader.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => calls[0].IsExpanded);
            Assert.Single(FindToolDetailCards(view));
            Assert.False(calls[1].HasMaterializedDetails);
            header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForPendingCoordinatorOperationsAsync(view);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.False(group.IsExpanded);
            var realized = typeof(AgentChatView).GetMethod("RealizeTranscriptAnchor", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(view, [calls[1].AnchorKey]);
            Assert.True(group.IsExpanded);
            Assert.Same(calls[1], Assert.IsAssignableFrom<Control>(realized).DataContext);
            model.IsDetailedTranscript = true;
            Assert.Empty(model.TranscriptItems.OfType<AgentActivityGroupRowViewModel>());
            Assert.All(calls, call => Assert.Contains(call, model.TranscriptItems));
            model.IsDetailedTranscript = false;
            Assert.Same(group, Assert.Single(model.TranscriptItems.OfType<AgentActivityGroupRowViewModel>()));
            foreach (var width in new[] { 900, 480, 340, 900 })
            {
                window.Width = width;
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                await WaitForPendingCoordinatorOperationsAsync(view);
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                AssertActivityHeaderMatchesUserMessage(view, Assert.Single(view.GetVisualDescendants().OfType<Button>(),
                    button => ReferenceEquals(button.DataContext, group)));
                var wide = width >= 520;
                var row = view.FindControl<Grid>(wide ? "WideWorkspacePathRow" : "NarrowWorkspacePathRow")!;
                var area = view.FindControl<Grid>(wide ? "WideWorkspacePathArea" : "NarrowWorkspacePathArea")!;
                var toggle = view.FindControl<ToggleButton>(wide ? "WideActivityViewToggle" : "NarrowActivityViewToggle")!;
                Assert.True(toggle.IsEffectivelyVisible);
                Assert.Equal("Detailed activity", toggle.Content);
                Assert.InRange(Math.Abs(row.Bounds.Width - toggle.Bounds.Right), 0, 1);
                if (withPaths) Assert.True(area.Bounds.Right <= toggle.Bounds.Left);
                toggle.IsChecked = true;
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                Assert.True(model.IsDetailedTranscript);
                Assert.Equal("Compact activity", toggle.Content);
                toggle.IsChecked = false;
                await WaitForPendingCoordinatorOperationsAsync(view);
            }
            Assert.Equal(history, JsonSerializer.Serialize(services.SessionService.ListTurns(session.SessionId)));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SubsessionActivityGroups_UseTheSameViewOnlyGroupingAndLazyCallExpansion()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profiles = services.ProfileService;
        var profile = await profiles.CreateProfileAsync("Subagent activity");
        var workspace = services.WorkspaceService.CreateWorkspace("Workspace");
        var parent = services.SessionService.CreateSession("Parent", workspaceId: workspace.WorkspaceId);
        var child = services.SessionService.CreateSession("Child", parentSessionId: parent.SessionId,
            rootSessionId: parent.SessionId, profileId: profile.ProfileId, agentKind: "subagent");
        services.SessionService.AppendTextTurn(child.SessionId, AgentMessageRole.User, "Inspect the delegated workspace files.");
        AppendMixedActivity(services.SessionService, child.SessionId);
        services.ExtensionCatalog.AddRuntimeCatalog(new AgentRuntimeCatalog(services.SessionService, profiles, services.WorkspaceService));
        using var adapter = new CatalogBackedSubsessionRuntimeAdapter(scope.Context, services.ExtensionCatalog);
        var model = adapter.CreateViewModel();
        using var view = new SubsessionsView(model);
        var window = new Window { Width = 900, Height = 900, Content = view };
        window.Show();
        try
        {
            await view.OnNavigatedToAsync(new(SubagentConstants.SubsessionsViewId,
                new Dictionary<string, string?> { ["sessionId"] = child.SessionId.ToString("D") }));
            await GetPendingCoordinatorOperations(view);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var group = Assert.Single(model.TranscriptItems.OfType<SubsessionActivityGroupRowViewModel>());
            var calls = model.Messages.OfType<SubsessionToolInvocationRowViewModel>().ToArray();
            Assert.Equal(8, calls.Length);
            Assert.Equal("1 Failed", group.StatusText);
            Assert.True(group.IsRunning);
            Assert.False(group.IsExpanded);
            var header = Assert.Single(view.GetVisualDescendants().OfType<Button>(), button => ReferenceEquals(button.DataContext, group));
            Assert.Equal(0, header.Margin.Left);
            AssertActivityHeaderMatchesUserMessage(view, header);
            header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await GetPendingCoordinatorOperations(view);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.True(group.IsExpanded);
            Assert.All(calls, call => Assert.False(call.HasMaterializedDetails));
            Assert.All(calls, call => Assert.Contains(call, model.TranscriptItems));
            model.IsDetailedTranscript = true;
            Assert.Equal("Compact activity", model.ActivityViewToggleText);
            Assert.Empty(model.TranscriptItems.OfType<SubsessionActivityGroupRowViewModel>());
            model.IsDetailedTranscript = false;
            Assert.Same(group, Assert.Single(model.TranscriptItems.OfType<SubsessionActivityGroupRowViewModel>()));
            group.IsExpanded = false;
            foreach (var width in new[] { 900, 480, 340, 900 })
            {
                window.Width = width;
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                await GetPendingCoordinatorOperations(view);
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                AssertActivityHeaderMatchesUserMessage(view, Assert.Single(view.GetVisualDescendants().OfType<Button>(),
                    button => ReferenceEquals(button.DataContext, group)));
            }
        }
        finally { window.Close(); }
    }

    private static void AssertActivityHeaderMatchesUserMessage(Control view, Button header)
    {
        var user = Assert.Single(view.GetVisualDescendants().OfType<Border>(), border => border.IsEffectivelyVisible
            && border.Classes.Contains("message-bubble") && border.Classes.Contains("user"));
        Assert.True(user.Bounds.Width > 0);
        Assert.InRange(Math.Abs(user.Bounds.Width - header.Bounds.Width), 0, 1);
        var userOrigin = user.TranslatePoint(default, view);
        var headerOrigin = header.TranslatePoint(default, view);
        Assert.NotNull(userOrigin);
        Assert.NotNull(headerOrigin);
        Assert.InRange(Math.Abs(userOrigin.Value.X - headerOrigin.Value.X), 0, 1);
    }

    private static void AppendMixedActivity(AgentSessionService sessions, Guid sessionId)
    {
        foreach (var tool in new[] { "read", "glob", "shell", "web_search", "edit", "apply_patch", McpToolCardPresentation.CreateStableToolId("server", "lookup") })
        {
            sessions.AppendToolCallTurn(sessionId, AgentMessageRole.Assistant, tool + "-call", tool, "{}");
            sessions.AppendToolResultTurn(sessionId, tool + "-call", tool, "{}", "Deferred tool output", "Completed.",
                null, null, wasTruncated: false, isError: tool == "shell", errorCode: null, backendId: null);
        }
        sessions.AppendToolCallTurn(sessionId, AgentMessageRole.Assistant, "fetch-running", "web_fetch", "{}");
    }
}
