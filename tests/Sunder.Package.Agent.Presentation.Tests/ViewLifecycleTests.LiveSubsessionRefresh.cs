extern alias SubagentsApp;

using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using SubagentsApp::Sunder.Package.Agent.Subagents;
using SubagentsApp::Sunder.Package.Agent.Subagents.PackageViews;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed partial class ViewLifecycleTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubsessionsView_RestoresRetryAndReasoningThenKeepsTheSameLiveFooter(bool detailed)
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profiles = services.ProfileService;
        var profile = await profiles.CreateProfileAsync("Subsession recovery");
        var workspace = services.WorkspaceService.CreateWorkspace("Subsession recovery");
        var parent = services.SessionService.CreateSession("Parent", workspaceId: workspace.WorkspaceId);
        var child = services.SessionService.CreateSession("Child", parentSessionId: parent.SessionId,
            rootSessionId: parent.SessionId, profileId: profile.ProfileId, agentKind: "subagent");
        var revision = services.SessionService.GetNextRunRevision(child.SessionId);
        services.SessionService.SaveCheckpoint(child.SessionId, revision, AgentRunStatus.Running, "Thinking.");
        services.SessionService.ReportRunActivity(child.SessionId, revision, AgentRunActivityKind.Reasoning, "Reviewing saved work.");
        services.SessionService.ReportRunActivity(child.SessionId, revision, AgentRunActivityKind.Retrying, "Servers overloaded",
            new AgentRunRetryState(3, DateTimeOffset.UtcNow.AddMinutes(2), "Servers overloaded", true));
        var catalog = new AgentRuntimeCatalog(services.SessionService, profiles, services.WorkspaceService);
        services.ExtensionCatalog.AddRuntimeCatalog(new SubsessionRuntimeCatalogServiceProxy(
            AgentRoleBoundary.As<Sunder.Generated.Packages.Younics_Ai_Agent_Services.IRuntimeCatalogService>(catalog)));
        using var adapter = new CatalogBackedSubsessionRuntimeAdapter(scope.Context, services.ExtensionCatalog);
        var model = adapter.CreateViewModel();
        using var view = new SubsessionsView(model);
        var window = new Window { Width = 820, Height = 700, Content = view };
        window.Show();
        try
        {
            await view.OnNavigatedToAsync(new PackageViewNavigationContext(SubagentConstants.SubsessionsViewId,
                new Dictionary<string, string?> { [SubagentConstants.SubsessionNavigationSessionIdKey] = child.SessionId.ToString("D") }));
            model.IsDetailedTranscript = detailed;
            await WaitForPendingCoordinatorOperationsAsync(view);
            var footer = model.RunActivityRow;
            Assert.False(model.HasLoadError, model.StatusText);
            Assert.Equal(child.SessionId, model.SelectedSubsession?.SessionId);
            Assert.True(model.IsSelectedSubsessionRunActive, model.StatusText);
            Assert.True(footer.IsVisible, $"Footer: {footer.ThinkingText}; status: {model.StatusText}; transcript rows: {model.Messages.Count}");
            Assert.Contains("Servers overloaded", footer.ThinkingText);
            Assert.Contains("attempt 3", footer.ThinkingText);
            Assert.Equal("Reviewing saved work.", footer.ReasoningText);
            services.SessionService.AppendTextTurn(child.SessionId, AgentMessageRole.Assistant, "Retained partial response");
            services.SessionService.ReportRunActivity(child.SessionId, revision, AgentRunActivityKind.Responding, "Responding");
            await WaitUntilAsync(() => footer.ThinkingText.StartsWith("Responding", StringComparison.Ordinal));
            Assert.True(footer.IsVisible);
            Assert.Same(footer, model.TranscriptItems[^2]);
            Assert.Equal("Reviewing saved work.", footer.ReasoningText);
            services.SessionService.SaveCheckpoint(child.SessionId, revision, AgentRunStatus.Stopped, "Stopped.");
            await WaitUntilAsync(() => !footer.IsVisible);
            services.SessionService.ReportRunActivity(child.SessionId, revision, AgentRunActivityKind.Retrying, "Late retry");
            Assert.False(footer.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubsessionsView_LiveWriteRefreshRetainsVisibleContentAcrossThinkingAndCollapse(bool detailed)
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profiles = services.ProfileService;
        var profile = await profiles.CreateProfileAsync("Subsession live refresh");
        var workspace = services.WorkspaceService.CreateWorkspace("Subsession live refresh");
        var parent = services.SessionService.CreateSession("Parent", workspaceId: workspace.WorkspaceId);
        var child = services.SessionService.CreateSession("Child", parentSessionId: parent.SessionId,
            rootSessionId: parent.SessionId, profileId: profile.ProfileId, agentKind: "subagent");
        for (var index = 0; index < 8; index++) services.SessionService.AppendTextTurn(child.SessionId,
            AgentMessageRole.Assistant, $"Prior response {index}");
        var args = JsonSerializer.Serialize(new { path = "Services/Weather.cs", content = string.Join('\n', Enumerable.Range(0, 60).Select(index => $"line {index}")) });
        services.SessionService.AppendToolCallTurn(child.SessionId, AgentMessageRole.Assistant, "sub-live-write", "write", args);
        var lease = new AgentDurableRunLease(services.SessionService.Store.ReserveRun(child.SessionId, profile.ProfileId, "Streaming response"));
        Assert.NotNull(services.SessionService.TryTransitionRun(lease, AgentRunStatus.Running, "Thinking."));
        var stream = services.SessionService.AppendTextTurn(lease, AgentMessageRole.Assistant, "Streaming response");
        for (var index = 0; index < 4; index++) services.SessionService.AppendTextTurn(child.SessionId,
            AgentMessageRole.Assistant, $"Later response {index}: " + new string('x', 80));
        var revision = lease.Key.RunRevision;
        var transcriptResets = 0;
        services.SessionService.TranscriptReset += _ => transcriptResets++;
        var catalog = new AgentRuntimeCatalog(services.SessionService, profiles, services.WorkspaceService);
        var proxy = new SubsessionRuntimeCatalogServiceProxy(AgentRoleBoundary.As<Sunder.Generated.Packages.Younics_Ai_Agent_Services.IRuntimeCatalogService>(catalog));
        services.ExtensionCatalog.AddRuntimeCatalog(proxy);
        using var adapter = new CatalogBackedSubsessionRuntimeAdapter(scope.Context, services.ExtensionCatalog);
        var model = adapter.CreateViewModel();
        using var view = new SubsessionsView(model);
        var window = new Window { Width = 820, Height = 700, Content = view };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = false;
        window.Show();
        try
        {
            await view.OnNavigatedToAsync(new PackageViewNavigationContext(SubagentConstants.SubsessionsViewId,
                new Dictionary<string, string?> { [SubagentConstants.SubsessionNavigationSessionIdKey] = child.SessionId.ToString("D") }));
            await Settle();
            model.IsDetailedTranscript = detailed;
            await Settle();
            if (!detailed)
            {
                foreach (var group in model.TranscriptItems.OfType<SubsessionActivityGroupRowViewModel>().ToArray()) group.IsExpanded = true;
                await Settle();
            }
            var tools = model.Messages.OfType<SubsessionToolInvocationRowViewModel>().ToArray();
            Assert.True(tools.Length == 1, $"{model.StatusText}; messages={model.Messages.Count}; groups={model.Messages.OfType<SubsessionActivityGroupRowViewModel>().Count()}; detailed={detailed}");
            var row = tools.Single();
            var invalidationTrace = string.Empty;
            row.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(row.IsExpanded) && !row.IsExpanded)
                    invalidationTrace = Environment.StackTrace;
            };
            var viewer = view.FindControl<ScrollViewer>("TranscriptScrollViewer")!;
            var repeater = view.FindControl<ItemsRepeater>("TranscriptItemsControl")!;
            repeater.GetOrCreateElement(model.TranscriptItems.IndexOf(row)).BringIntoView();
            await Settle();
            var header = Assert.Single(view.GetVisualDescendants().OfType<Button>(), button =>
                button.Classes.Contains("tool-step-header") && ReferenceEquals(button.DataContext, row));
            header.BringIntoView();
            await Settle();
            for (var pass = 0; pass < 8; pass++)
            {
                var top = header.TranslatePoint(default, viewer)!.Value.Y;
                if (top >= 24 && top + header.Bounds.Height <= viewer.Viewport.Height - 24) break;
                viewer.Offset = new Vector(0, Math.Clamp(viewer.Offset.Y + top - 100, 0, Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height)));
                await Settle();
            }
            Assert.InRange(header.TranslatePoint(default, viewer)!.Value.Y, 0, viewer.Viewport.Height - header.Bounds.Height);
            header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => row.IsExpanded || row.IsDetailLoadFailed);
            Assert.True(row.IsExpanded, row.DetailLoadFailureText);
            await Settle();
            var previous = row.ExpandedDetails;
            Assert.NotNull(previous);
            proxy.ToolDetailOverride = async (request, token) =>
            {
                started.TrySetResult();
                try { await release.Task.WaitAsync(token); }
                catch (OperationCanceledException) { canceled = true; throw; }
                return services.SessionService.Store.GetTranscriptToolDetail(request);
            };
            var payload = JsonSerializer.Serialize(new { schema = "younics.ai.agent.tools.files.diff.v1", files = new[] {
                new { path = "Services/Weather.cs", operation = "Add", addedLineCount = 1, deletedLineCount = 0,
                    lines = new[] { new { kind = "added", lineNumber = 1, text = "recorded content" } } } } });
            services.SessionService.AppendToolResultTurn(child.SessionId, "sub-live-write", "write", args, "Write completed", "Wrote content",
                null, null, false, false, null, null, payload);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var update = 0; update < 3; update++)
            {
                services.SessionService.UpdateTextTurn(lease, stream.TurnId, $"Stream update {update}");
                services.SessionService.ReportRunActivity(child.SessionId, revision, AgentRunActivityKind.Reasoning, $"Thinking {update}");
                await GetPendingRuntimeRefresh(model).WaitAsync(TimeSpan.FromSeconds(5));
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Assert.True(row.IsExpanded, invalidationTrace);
                Assert.Same(previous, row.ExpandedDetails);
                Assert.False(canceled);
            }
            release.TrySetResult();
            await WaitUntilAsync(() => row.IsExpanded && !row.IsPreparing && !ReferenceEquals(previous, row.ExpandedDetails),
                () => $"state={row.ExpansionState}, revision={row.DetailRevision}, failure={row.DetailLoadFailureText}, cancelled={canceled}, retained={model.Messages.Contains(row)}");
            Assert.True(row.ExpandedDetails!.HasToolDiff);
            await Settle();
            header = CurrentHeader(); // A virtualized presenter may be recreated; the logical row is stable.
            Assert.InRange(header.TranslatePoint(default, viewer)!.Value.Y, 0, viewer.Viewport.Height - header.Bounds.Height);
            var before = header.TranslatePoint(default, viewer)!.Value.Y;
            header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !row.IsExpanded);
            await Settle();
            for (var update = 3; update < 6; update++)
            {
                services.SessionService.UpdateTextTurn(lease, stream.TurnId, $"Stream after collapse {update}");
                services.SessionService.ReportRunActivity(child.SessionId, revision, AgentRunActivityKind.Reasoning, $"Still thinking {update}");
                await GetPendingRuntimeRefresh(model).WaitAsync(TimeSpan.FromSeconds(5));
                await Settle();
                header = CurrentHeader();
                Assert.InRange(Math.Abs(header.TranslatePoint(default, viewer)!.Value.Y - before), 0, 1);
                Assert.False(row.HasMaterializedDetails);
            }
            Assert.Equal(0, transcriptResets);

            Button CurrentHeader()
            {
                var current = view.GetVisualDescendants().OfType<Button>().Where(button =>
                    button.Classes.Contains("tool-step-header") && ReferenceEquals(button.DataContext, row)).ToArray();
                Assert.True(current.Length == 1, $"member={model.Messages.Contains(row)}, item={model.TranscriptItems.IndexOf(row)}, expanded={row.IsExpanded}; "
                    + string.Join(" | ", model.Messages.OfType<SubsessionActivityGroupRowViewModel>().Select(group => $"{group.Summary}: expanded={group.IsExpanded}, {group.LoadingText}"))
                    + "; " + DescribeSubsessionTranscriptState(view, viewer, GetTranscriptScrollCoordinator(view), "after-live-refresh"));
                return current.Single();
            }
        }
        finally { release.TrySetResult(); window.Close(); }

        async Task Settle()
        {
            await WaitForPendingCoordinatorOperationsAsync(view);
            for (var pass = 0; pass < 100; pass++)
            {
                var presenters = view.GetVisualDescendants().OfType<SubagentsApp::Sunder.Package.Agent.Shared.PackageViews.StreamingMarkdownPresenter>()
                    .Where(presenter => presenter.IsEffectivelyVisible).ToArray();
                await Task.WhenAll(presenters.Select(presenter => presenter.PendingRenderOperations)).WaitAsync(TimeSpan.FromSeconds(5));
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                if (view.GetVisualDescendants().OfType<SubagentsApp::Sunder.Package.Agent.Shared.PackageViews.StreamingMarkdownPresenter>()
                    .Where(presenter => presenter.IsEffectivelyVisible).All(presenter => !presenter.IsRenderPending)) return;
            }
            Assert.Fail("Subsession Markdown did not settle.");
        }
    }
}
