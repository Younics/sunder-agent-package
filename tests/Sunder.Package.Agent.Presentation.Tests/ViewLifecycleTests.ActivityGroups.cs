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
    // Tool-detail/viewport regressions first reveal compact members, just as a
    // user now must for a singleton. Individual details remain collapsed/lazy.
    private static async Task RevealCompactCallsAsync(AgentChatView view, IEnumerable<AgentActivityGroupRowViewModel> groups)
    {
        foreach (var group in groups) group.IsExpanded = true;
        await Dispatcher.UIThread.InvokeAsync(view.UpdateLayout, DispatcherPriority.Render);
        await WaitForPendingCoordinatorOperationsAsync(view);
        await Dispatcher.UIThread.InvokeAsync(view.UpdateLayout, DispatcherPriority.Render);
    }

    [AvaloniaFact]
    public async Task AgentActivityGroups_FirstCallIsAlreadyHiddenBehindItsCompactHeader()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profiles = services.ProfileService;
        await profiles.CreateProfileAsync("First call");
        var workspace = services.WorkspaceService.CreateWorkspace("First call");
        var session = services.SessionService.CreateSession("First call", workspaceId: workspace.WorkspaceId);
        services.SessionService.AppendTextTurn(session.SessionId, AgentMessageRole.User, "Inspect one file.");
        services.SessionService.AppendToolCallTurn(session.SessionId, AgentMessageRole.Assistant, "first", "read", "{}");
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
            Assert.Equal("1 Read", group.Summary);
            Assert.False(group.IsExpanded);
            Assert.Empty(model.TranscriptItems.OfType<AgentToolInvocationRowViewModel>());
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), button => button.Classes.Contains("tool-step-header"));
            Assert.Empty(model.Messages.OfType<AgentToolInvocationRowViewModel>());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task AgentActivityGroups_ReceiptMetadataCannotKeepCompletedInvocationsAnimating()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profiles = services.ProfileService;
        await profiles.CreateProfileAsync("Historical operations");
        var workspace = services.WorkspaceService.CreateWorkspace("Historical operations");
        var session = services.SessionService.CreateSession("Historical operations", workspaceId: workspace.WorkspaceId);
        foreach (var call in new[] { "apply", "observe" })
        {
            services.SessionService.AppendToolCallTurn(session.SessionId, AgentMessageRole.Assistant, call, "maker_apply", "{}");
            services.SessionService.AppendToolResultTurn(session.SessionId, call, "maker_apply", "{}", "Original receipt", "Package operation running",
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
                Assert.Empty(group.StatusText);
                Assert.Equal("✓", group.StatusIconText);
                Assert.Empty(model.Messages.OfType<AgentToolInvocationRowViewModel>());
                group.IsExpanded = true;
                await WaitForPendingCoordinatorOperationsAsync(view);
                Assert.Equal(2, model.Messages.OfType<AgentToolInvocationRowViewModel>().Count());
                Assert.All(model.Messages.OfType<AgentToolInvocationRowViewModel>(), tool =>
                {
                    Assert.False(tool.IsRunning);
                    Assert.Equal("Completed", tool.StatusText);
                    Assert.True(tool.IsCompleted);
                });
                var header = Assert.Single(view.GetVisualDescendants().OfType<Button>(), button => ReferenceEquals(button.DataContext, group));
                Assert.DoesNotContain(header.GetVisualDescendants().OfType<ProgressBar>(), bar => bar.IsVisible || bar.IsIndeterminate);
                Assert.DoesNotContain(header.GetVisualDescendants().OfType<TextBlock>(), block => block.IsVisible && block.Text == "Completed");
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
            Assert.Empty(calls); // Collapsed groups do not create invocation headers.
            Assert.Equal("1 Read · 1 Glob · 1 Shell · 1 Web Search · 1 Edit · 1 Patch · 1 MCP Tool · 1 Web Fetch", group.Summary);
            Assert.Equal("1!", group.StatusText);
            Assert.Empty(group.StatusIconText);
            Assert.True(group.IsRunning);
            Assert.False(group.IsExpanded);
            Assert.All(calls, call => Assert.False(call.HasMaterializedDetails));
            Assert.Empty(FindToolDetailCards(view));
            var header = Assert.Single(view.GetVisualDescendants().OfType<Button>(), button => ReferenceEquals(button.DataContext, group));
            Assert.Equal(0, header.Margin.Left);
            Assert.Equal(HorizontalAlignment.Stretch, header.HorizontalAlignment);
            AssertActivityHeaderMatchesUserMessage(view, header);
            Assert.NotNull(Avalonia.Automation.AutomationProperties.GetHelpText(header));
            var progress = Assert.Single(header.GetVisualDescendants().OfType<ProgressBar>(), progress => progress.IsVisible && progress.IsIndeterminate);
            Assert.Equal(12, progress.Width);
            Assert.Single(header.GetVisualDescendants().OfType<TextBlock>(), block => block.IsVisible && block.Text == "1!");
            Assert.DoesNotContain(header.GetVisualDescendants().OfType<TextBlock>(), block => block.IsVisible && block.Text is "Running" or "…" or "!");
            header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForPendingCoordinatorOperationsAsync(view);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.True(group.IsExpanded);
            calls = model.Messages.OfType<AgentToolInvocationRowViewModel>().ToArray();
            Assert.Equal(8, calls.Length); // A call and its result are one invocation.
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
            Assert.Empty(model.Messages.OfType<AgentToolInvocationRowViewModel>());
            var exactAnchor = calls[1].AnchorKey;
            Assert.Equal(group.AnchorKey, model.ResolveVisibleTranscriptAnchor(exactAnchor));
            group.IsExpanded = true;
            await WaitForPendingCoordinatorOperationsAsync(view);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var reloaded = model.Messages.OfType<AgentToolInvocationRowViewModel>().Single(call => Equals(call.AnchorKey, exactAnchor));
            Assert.NotSame(calls[1], reloaded);
            Assert.False(reloaded.HasMaterializedDetails);
            var realized = typeof(AgentChatView).GetMethod("RealizeTranscriptAnchor", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(view, [exactAnchor]);
            Assert.True(group.IsExpanded);
            Assert.Same(reloaded, Assert.IsAssignableFrom<Control>(realized).DataContext);
            model.IsDetailedTranscript = true;
            await WaitForPendingCoordinatorOperationsAsync(view);
            Assert.Empty(model.TranscriptItems.OfType<AgentActivityGroupRowViewModel>());
            Assert.Equal(8, model.TranscriptItems.OfType<AgentToolInvocationRowViewModel>().Count());
            var groupAnchor = group.AnchorKey;
            model.IsDetailedTranscript = false;
            await WaitForPendingCoordinatorOperationsAsync(view);
            group = Assert.Single(model.TranscriptItems.OfType<AgentActivityGroupRowViewModel>());
            Assert.Equal(groupAnchor, group.AnchorKey);
            Assert.Empty(model.Messages.OfType<AgentToolInvocationRowViewModel>());
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
                await WaitForPendingCoordinatorOperationsAsync(view);
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                Assert.True(model.IsDetailedTranscript);
                Assert.Equal("Compact activity", toggle.Content);
                toggle.IsChecked = false;
                await WaitForPendingCoordinatorOperationsAsync(view);
                group = Assert.Single(model.TranscriptItems.OfType<AgentActivityGroupRowViewModel>());
            }
            Assert.Equal(history, JsonSerializer.Serialize(services.SessionService.ListTurns(session.SessionId)));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SubsessionActivityGroups_UseTheSameSummaryPagingAndLazyCallExpansion()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profiles = services.ProfileService;
        var profile = await profiles.CreateProfileAsync("Subagent activity");
        var workspace = services.WorkspaceService.CreateWorkspace("Workspace");
        var parent = services.SessionService.CreateSession("Parent", workspaceId: workspace.WorkspaceId);
        var child = services.SessionService.CreateSession("Child", parentSessionId: parent.SessionId,
            rootSessionId: parent.SessionId, profileId: profile.ProfileId, agentKind: "subagent");
        var attachment = await new AgentAttachmentService(scope.Context).StoreAttachmentAsync(child.SessionId,
            new AgentAttachmentUploadRequest("note.txt", "text/plain", System.Text.Encoding.UTF8.GetBytes("Attachment fixture")));
        services.SessionService.AppendUserTurn(child.SessionId, AgentMessageRole.User, string.Empty, [attachment]);
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
            Assert.Empty(calls);
            Assert.Equal("1!", group.StatusText);
            Assert.Empty(group.StatusIconText);
            Assert.True(group.IsRunning);
            Assert.False(group.IsExpanded);
            var header = Assert.Single(view.GetVisualDescendants().OfType<Button>(), button => ReferenceEquals(button.DataContext, group));
            var progress = Assert.Single(header.GetVisualDescendants().OfType<ProgressBar>(), progress => progress.IsVisible && progress.IsIndeterminate);
            Assert.Equal(12, progress.Width);
            Assert.Single(header.GetVisualDescendants().OfType<TextBlock>(), block => block.IsVisible && block.Text == "1!");
            Assert.Equal(0, header.Margin.Left);
            AssertActivityHeaderMatchesUserMessage(view, header);
            header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await GetPendingCoordinatorOperations(view);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.True(group.IsExpanded);
            calls = model.Messages.OfType<SubsessionToolInvocationRowViewModel>().ToArray();
            Assert.Equal(8, calls.Length);
            Assert.All(calls, call => Assert.False(call.HasMaterializedDetails));
            Assert.All(calls, call => Assert.Contains(call, model.TranscriptItems));
            model.IsDetailedTranscript = true;
            await WaitForPendingCoordinatorOperationsAsync(view);
            Assert.Equal("Compact activity", model.ActivityViewToggleText);
            Assert.Empty(model.TranscriptItems.OfType<SubsessionActivityGroupRowViewModel>());
            var groupAnchor = group.AnchorKey;
            model.IsDetailedTranscript = false;
            await WaitForPendingCoordinatorOperationsAsync(view);
            group = Assert.Single(model.TranscriptItems.OfType<SubsessionActivityGroupRowViewModel>());
            Assert.Equal(groupAnchor, group.AnchorKey);
            Assert.Empty(model.Messages.OfType<SubsessionToolInvocationRowViewModel>());
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

    [AvaloniaFact]
    public async Task SubsessionActivityGroups_CompactHistoryNavigationRevealsACompleteLargeGroupWithoutToolPayloads()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profiles = services.ProfileService;
        var profile = await profiles.CreateProfileAsync("Compact navigation");
        var workspace = services.WorkspaceService.CreateWorkspace("Workspace");
        var parent = services.SessionService.CreateSession("Parent", workspaceId: workspace.WorkspaceId);
        var child = services.SessionService.CreateSession("Child", parentSessionId: parent.SessionId,
            rootSessionId: parent.SessionId, profileId: profile.ProfileId, agentKind: "subagent");
        var calls = Enumerable.Range(0, 123).Select(index => services.SessionService.AppendToolCallTurn(child.SessionId,
            AgentMessageRole.Assistant, "navigate-" + index, "read", "{\"path\":\"file.txt\"}")).ToArray();
        var target = calls[111];
        for (var index = 0; index < 80; index++)
            services.SessionService.AppendTextTurn(child.SessionId, AgentMessageRole.Assistant, "Later message " + index);
        services.ExtensionCatalog.AddRuntimeCatalog(new AgentRuntimeCatalog(services.SessionService, profiles, services.WorkspaceService));
        using var adapter = new CatalogBackedSubsessionRuntimeAdapter(scope.Context, services.ExtensionCatalog);
        var model = adapter.CreateViewModel();
        using var view = new SubsessionsView(model);
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        try
        {
            Assert.True(await view.PrepareNavigationAsync(CreateSubsessionNavigation(child.SessionId, target.TurnId,
                target.Items.Single().ItemId, "Activity", target.CreatedAtUtc, target.Items.Single().CallId)));
            await WaitForPendingCoordinatorOperationsAsync(view);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var group = Assert.Single(model.Messages.OfType<SubsessionActivityGroupRowViewModel>());
            Assert.Equal("123 Reads", group.Summary);
            Assert.True(group.IsExpanded);
            var tools = model.Messages.OfType<SubsessionToolInvocationRowViewModel>().ToArray();
            Assert.Equal(123, tools.Length);
            Assert.All(tools, tool => Assert.False(tool.HasMaterializedDetails));
            var selected = Assert.Single(tools, tool => Equals(tool.AnchorKey, model.NavigationAnchorKey));
            var realized = typeof(SubsessionsView).GetMethod("RealizeTranscriptAnchor", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(view, [selected.AnchorKey]);
            Assert.Same(selected, Assert.IsAssignableFrom<Control>(realized).DataContext);
        }
        finally { window.Close(); }
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
