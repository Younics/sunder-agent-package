extern alias AgentApp;

using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AgentApp::Sunder.Package.Agent.PackageViews;
using AgentApp::Sunder.Package.Agent.Shared.PackageViews;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using AgentChatSelectionStateService = AgentApp::Sunder.Package.Agent.Services.AgentChatSelectionStateService;
using AgentToolPresentationService = AgentApp::Sunder.Package.Agent.Services.AgentToolPresentationService;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed partial class ViewLifecycleTests
{
    [AvaloniaTheory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task AgentChatView_DelayedBidirectionalPagingKeepsTheCurrentViewportWithoutLoadingOnlyFrames(bool older, bool detailed)
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profiles = services.ProfileService;
        await profiles.CreateProfileAsync("Delayed paging");
        var workspace = services.WorkspaceService.CreateWorkspace("Delayed paging");
        var session = services.SessionService.CreateSession("Delayed paging", workspaceId: workspace.WorkspaceId);
        for (var index = 0; index < 70; index++)
        {
            services.SessionService.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant,
                $"### Response {index}\n\n" + string.Join(" ", Enumerable.Repeat("Variable-height **historical** content with `code`.", 3 + index % 8)));
            var call = $"page:{index}";
            services.SessionService.AppendToolCallTurn(session.SessionId, AgentMessageRole.Assistant, call, "read", "{}");
            services.SessionService.AppendToolResultTurn(session.SessionId, call, "read", "{}", "Retained tool result", "Completed.", null, null, false, false, null, null);
        }
        var history = JsonSerializer.Serialize(services.SessionService.ListTurns(session.SessionId));
        var gateway = new BlockingPageSessionGateway(services.SessionService, older ? BlockedPageDirection.Older : BlockedPageDirection.Newer);
        using var view = RegressionTestAppViews.ChatView(profiles, services.WorkspaceService, gateway,
            NoOpPermissionGateway.Instance, NoOpRunGateway.Instance, new AgentChatSelectionStateService(scope.Context),
            new AgentToolPresentationService(), new AgentExecutionTargetWarmupService(services.WorkspaceService, services.TargetService),
            null!, new AgentAttachmentService(scope.Context), NullPackageNotificationService.Instance);
        var window = new Window { Width = 780, Height = 560, Content = view };
        window.Show();
        try
        {
            await view.OnNavigatedToAsync(new("younics.ai.agent.chat", new Dictionary<string, string?>()));
            await WaitForPendingCoordinatorOperationsAsync(view);
            var model = Assert.IsType<AgentChatViewModel>(view.DataContext);
            model.IsDetailedTranscript = detailed;
            await WaitForPendingCoordinatorOperationsAsync(view);
            if (!older)
            {
                for (var page = 0; page < 3 && !model.HasNewerTranscriptRows; page++)
                {
                    Assert.True(await model.LoadOlderTranscriptRowsAsync(model.Messages[0].AnchorKey));
                    await WaitForPendingCoordinatorOperationsAsync(view);
                }
                Assert.True(model.HasNewerTranscriptRows);
            }
            var repeater = view.FindControl<ItemsRepeater>("TranscriptItemsControl")!;
            var viewer = GetTranscriptScrollViewer(view);
            var coordinator = GetTranscriptScrollCoordinator(view);
            // Stay clear of automatic edge loading during setup, then exercise
            // actual wheel input while the selected direction's read is blocked.
            var target = model.TranscriptItems[model.TranscriptItems.Count / 2];
            var targetVisual = repeater.GetOrCreateElement(model.TranscriptItems.IndexOf(target));
            targetVisual.BringIntoView();
            await Settle();
            Assert.True(older ? InvokeQueueLoadOlderRows(coordinator) : InvokeQueueLoadNewerRows(coordinator));
            await gateway.BlockedLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            window.MouseWheel(new Point(400, 260), new Vector(0, older ? -1 : 1), RawInputModifiers.None);
            await Settle();
            var key = Assert.IsAssignableFrom<object>(CaptureCurrentScrollAnchorKey(coordinator));
            var index = model.TranscriptItems.ToList().FindIndex(row => Equals(row.AnchorKey, key));
            var visual = Assert.IsAssignableFrom<Control>(repeater.TryGetElement(index));
            var top = visual.TranslatePoint(default, viewer)!.Value.Y;
            var failures = new List<string>();
            var observing = true;
            var queued = false;
            var frames = 0;
            void QueueFrame(object? _, EventArgs __)
            {
                if (queued || !observing) return;
                queued = true;
                Dispatcher.UIThread.Post(() =>
                {
                    queued = false;
                    if (!observing) return;
                    frames++;
                    var currentIndex = model.TranscriptItems.ToList().FindIndex(row => Equals(row.AnchorKey, key));
                    var current = currentIndex < 0 ? null : repeater.TryGetElement(currentIndex);
                    var currentTop = current?.TranslatePoint(default, viewer)?.Y;
                    if (model.IsTranscriptLoading || viewer.Opacity != 1 || currentTop is null || Math.Abs(currentTop.Value - top) > 1)
                        failures.Add($"anchor={key},top={currentTop},expected={top},offset={viewer.Offset.Y},extent={viewer.Extent.Height},opacity={viewer.Opacity},initial={model.IsTranscriptLoading}");
                }, DispatcherPriority.Render);
            }
            viewer.LayoutUpdated += QueueFrame;
            try
            {
                gateway.ReleaseBlockedLoad.TrySetResult();
                await WaitForPendingCoordinatorOperationsAsync(view).WaitAsync(TimeSpan.FromSeconds(5));
                await Settle();
                Assert.True(frames > 0);
                Assert.True(failures.Count == 0, string.Join('\n', failures.Take(12)));
                var retained = model.TranscriptItems.ToList().FindIndex(row => Equals(row.AnchorKey, key));
                Assert.Same(visual, repeater.TryGetElement(retained));
                Assert.False(model.IsLoadingOlderTranscriptRows);
                Assert.False(model.IsLoadingNewerTranscriptRows);
                Assert.False(GetAnchorHostSuspended(view));
                Assert.InRange(model.Messages.Count, 1, 60);
                Assert.Equal(history, JsonSerializer.Serialize(services.SessionService.ListTurns(session.SessionId)));
            }
            finally { observing = false; viewer.LayoutUpdated -= QueueFrame; }

            async Task Settle()
            {
                for (var pass = 0; pass < 12; pass++)
                {
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                    await Task.WhenAll(view.GetVisualDescendants().OfType<StreamingMarkdownPresenter>()
                        .Where(presenter => presenter.IsEffectivelyVisible).Select(presenter => presenter.PendingRenderOperations))
                        .WaitAsync(TimeSpan.FromSeconds(3));
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    if (!view.GetVisualDescendants().OfType<StreamingMarkdownPresenter>()
                        .Any(presenter => presenter.IsEffectivelyVisible && presenter.IsRenderPending)) return;
                }
                Assert.Fail("Visible page Markdown did not settle.");
            }
        }
        finally { gateway.ReleaseBlockedLoad.TrySetResult(); window.Close(); }
    }
}
