extern alias AgentApp;

using System.Text.Json;
using AgentApp::Sunder.Package.Agent.PackageViews;
using AgentApp::Sunder.Package.Agent.Shared.PackageViews;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Xunit;
using AgentChatSelectionStateService = AgentApp::Sunder.Package.Agent.Services.AgentChatSelectionStateService;
using AgentToolPresentationService = AgentApp::Sunder.Package.Agent.Services.AgentToolPresentationService;
using static Sunder.Package.Agent.Presentation.Tests.RenderedChatFixture;
using static Sunder.Package.Agent.Presentation.Tests.RenderedTranscriptGeometry;
using static Sunder.Package.Agent.Presentation.Tests.RenderedViewComposition;
using static Sunder.Package.Agent.Presentation.Tests.RenderedViewWaits;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class AgentChatPagingTests
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

    [AvaloniaFact]
    public async Task AgentChatView_RepeatedPagingResizeAndGroupCollapseKeepContentVisible()
    {
        var phase = "setup";
        void Stage(string stage)
        {
            phase = stage;
            if (Environment.GetEnvironmentVariable("SUNDER_TRANSCRIPT_REGRESSION_TRACE") is { Length: > 0 } path)
                File.AppendAllText(path, stage + Environment.NewLine);
        }
        Stage("setup");
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profiles = services.ProfileService;
        await profiles.CreateProfileAsync("Combined scrolling");
        var workspace = services.WorkspaceService.CreateWorkspace("Combined scrolling");
        var session = services.SessionService.CreateSession("Combined scrolling", workspaceId: workspace.WorkspaceId);
        for (var i = 0; i < 45; i++)
        {
            services.SessionService.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant,
                $"### Section {i}\n\n" + string.Join(" ", Enumerable.Repeat("Variable wrapping text with **Markdown** and `code`.", 3 + i % 9)));
            for (var call = 0; call < 3; call++)
            {
                var id = $"combined-{i}-{call}";
                services.SessionService.AppendToolCallTurn(session.SessionId, AgentMessageRole.Assistant, id, "read", "{}");
                services.SessionService.AppendToolResultTurn(session.SessionId, id, "read", "{}",
                    string.Join('\n', Enumerable.Range(0, 40).Select(line => $"Line {line}")), "Completed.", null, null, false, false, null, null);
            }
        }
        var history = JsonSerializer.Serialize(services.SessionService.ListTurns(session.SessionId));
        Stage("history created");
        using var view = CreateAgentChatView(scope, services, profiles);
        Stage("view created");
        var window = new Window { Width = 820, Height = 560, Content = view };
        window.Show();
        Stage("window shown");
        try
        {
            await view.OnNavigatedToAsync(new("younics.ai.agent.chat", new Dictionary<string, string?>())).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Stage("navigated");
            await WaitForPendingCoordinatorOperationsAsync(view).WaitAsync(TimeSpan.FromSeconds(10));
            await SettleCombinedMarkdownAsync();
            Stage("initial settled");
            var model = Assert.IsType<AgentChatViewModel>(view.DataContext);
            var repeater = view.FindControl<ItemsRepeater>("TranscriptItemsControl")!;
            var viewer = GetTranscriptScrollViewer(view);
            var coordinator = Assert.IsType<AgentApp::Sunder.Package.Agent.Shared.PackageViews.TranscriptScrollCoordinator>(GetTranscriptScrollCoordinator(view));
            void TraceGeometry(string stage)
            {
                if (Environment.GetEnvironmentVariable("SUNDER_TRANSCRIPT_REGRESSION_TRACE") is not { Length: > 0 } path) return;
                var rows = model.TranscriptItems.Select((row, index) => (row, index, visual: repeater.TryGetElement(index)))
                    .Where(pair => pair.visual is not null)
                    .Select(pair => $"{pair.index}:{(pair.row as AgentApp::Sunder.Package.Agent.Shared.PackageViews.ITranscriptAnchorItem)?.AnchorKey},bounds={pair.visual!.Bounds},top={pair.visual.TranslatePoint(default, viewer)?.Y},focus={pair.visual.IsKeyboardFocusWithin}");
                var exactKey = view.FindControl<AgentApp::Sunder.Package.Agent.Shared.PackageViews.TranscriptScrollAnchorHost>("TranscriptAnchorHost")?.ExactAnchorKey;
                var exactIndex = Array.FindIndex(model.TranscriptItems.ToArray(), row =>
                    row is AgentApp::Sunder.Package.Agent.Shared.PackageViews.ITranscriptAnchorItem item && Equals(item.AnchorKey, exactKey));
                var sourceKey = exactKey is AgentApp::Sunder.Package.Agent.Shared.PackageViews.TranscriptActivityGroupAnchor grouped ? grouped.SourceAnchor : exactKey;
                var sourceIndex = Array.FindIndex(model.Messages.ToArray(), row => Equals(row.AnchorKey, sourceKey));
                File.AppendAllText(path, $"{stage}: offset={viewer.Offset.Y},extent={viewer.Extent.Height},viewport={viewer.Viewport.Height},exact={exactKey},index={exactIndex},sourceIndex={sourceIndex},model={model.TranscriptViewportAnchor?.AnchorKey}; {string.Join("; ", rows)}; {coordinator.DiagnosticSnapshot}{Environment.NewLine}");
            }
            async Task SettleCombinedMarkdownAsync()
            {
                for (var pass = 0; pass < 100; pass++)
                {
                    var presenters = view.GetVisualDescendants()
                        .OfType<AgentApp::Sunder.Package.Agent.Shared.PackageViews.StreamingMarkdownPresenter>()
                        .Where(presenter => presenter.IsEffectivelyVisible).ToArray();
                    await Task.WhenAll(presenters.Select(presenter => presenter.PendingRenderOperations)).WaitAsync(TimeSpan.FromSeconds(5));
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    if (view.GetVisualDescendants()
                        .OfType<AgentApp::Sunder.Package.Agent.Shared.PackageViews.StreamingMarkdownPresenter>()
                        .Where(presenter => presenter.IsEffectivelyVisible)
                        .All(presenter => !presenter.IsRenderPending)) return;
                    await Task.Delay(10);
                }
                Assert.Fail("The combined transcript's Markdown did not settle.");
            }
            var frames = new List<string>();
            var blankFrames = new List<string>();
            var frameQueued = false;
            var tracing = true;
            void CaptureFrame()
            {
                if (model.IsTranscriptLoading || viewer.Viewport.Height <= 0) return;
                var visible = model.TranscriptItems.Take(model.TranscriptItems.Count - 2)
                    .Select((row, index) => (row, visual: repeater.TryGetElement(index) as Control))
                    .Where(pair => pair.visual is { IsEffectivelyVisible: true, Bounds.Height: > 0 }
                        && pair.visual.TranslatePoint(default, viewer) is { } point
                        && point.Y < viewer.Viewport.Height && point.Y + pair.visual.Bounds.Height > 0).ToArray();
                var frame = $"{phase}: rows={visible.Length}, offset={viewer.Offset.Y:F1}, extent={viewer.Extent.Height:F1}, width={viewer.Viewport.Width:F1}, opacity={viewer.Opacity}";
                frames.Add(frame);
                if (frames.Count > 256) frames.RemoveAt(0);
                if ((visible.Length == 0 || viewer.Opacity != 1) && blankFrames.Count < 256)
                {
                    blankFrames.Add(frame);
                    if (blankFrames.Count <= 8) TraceGeometry($"blank frame: {phase}");
                }
            }
            void QueueFrame(object? _, EventArgs __)
            {
                if (frameQueued || !tracing) return;
                frameQueued = true;
                Dispatcher.UIThread.Post(() =>
                {
                    frameQueued = false;
                    if (tracing) CaptureFrame();
                }, DispatcherPriority.Background);
            }
            viewer.LayoutUpdated += QueueFrame;
            try
            {
                for (var iteration = 0; iteration < 4; iteration++)
                {
                    Stage($"iteration {iteration}: scroll");
                    window.MouseWheel(new Point(window.Width / 2, 260), new Vector(0, 4), RawInputModifiers.None);
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                    await WaitForPendingCoordinatorOperationsAsync(view).WaitAsync(TimeSpan.FromSeconds(10));
                    await SettleCombinedMarkdownAsync();
                    Stage($"iteration {iteration}: reveal group");
                    AgentActivityGroupRowViewModel? FindVisibleGroup() => view.GetVisualDescendants().OfType<Button>()
                        .Where(button => button.DataContext is AgentActivityGroupRowViewModel
                            && button.TranslatePoint(default, viewer) is { Y: >= 0 } point
                            && point.Y < viewer.Viewport.Height - 50)
                        .Select(button => (AgentActivityGroupRowViewModel)button.DataContext!).FirstOrDefault();
                    var group = FindVisibleGroup();
                    for (var step = 0; group is null && step < 12; step++)
                    {
                        window.MouseWheel(new Point(window.Width / 2, 260), new Vector(0, 1), RawInputModifiers.None);
                        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                        await WaitForPendingCoordinatorOperationsAsync(view);
                        await SettleCombinedMarkdownAsync();
                        group = FindVisibleGroup();
                    }
                    Assert.NotNull(group);
                    Button Header() => Assert.Single(view.GetVisualDescendants().OfType<Button>(), button => ReferenceEquals(button.DataContext, group));
                    var header = Header();
                    var top = header.TranslatePoint(default, viewer)!.Value.Y;
                    viewer.Offset = new Vector(0, Math.Clamp(viewer.Offset.Y + top - 80, 0, viewer.Extent.Height - viewer.Viewport.Height));
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                    Header().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Stage($"iteration {iteration}: expanding");
                    await WaitForPendingCoordinatorOperationsAsync(view).WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.True(group.IsExpanded);
                    TraceGeometry("expanded");
                    var resizeTop = Header().TranslatePoint(default, viewer)!.Value.Y;

                    foreach (var width in new[] { 620, 522, 520, 518, 516, 518, 520, 522, 820 })
                    {
                        Stage($"iteration {iteration}: resize {width}");
                        window.Width = width;
                        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                        TraceGeometry($"resized {width}");
                    }
                    await WaitForPendingCoordinatorOperationsAsync(view).WaitAsync(TimeSpan.FromSeconds(10));
                    TraceGeometry("resize coordinator settled");
                    await SettleCombinedMarkdownAsync();
                    TraceGeometry("resize Markdown settled");
                    header = Header();
                    top = header.TranslatePoint(default, viewer)!.Value.Y;
                    Assert.Equal(AgentApp::Sunder.Package.Agent.Shared.PackageViews.TranscriptViewportMutationStatus.Completed,
                        coordinator.DiagnosticSnapshot.Mutation?.Status);
                    Assert.InRange(Math.Abs(top - resizeTop), 0, 1);
                    viewer.Offset = new Vector(0, Math.Clamp(viewer.Offset.Y + top - 80, 0, viewer.Extent.Height - viewer.Viewport.Height));
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                    top = Header().TranslatePoint(default, viewer)!.Value.Y;
                    Stage($"collapse capture: top={top}, offset={viewer.Offset.Y}, extent={viewer.Extent.Height}, viewport={viewer.Viewport.Height}");
                    Assert.InRange(top, 0, viewer.Viewport.Height - Header().Bounds.Height);
                    Header().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Stage($"iteration {iteration}: collapsing");
                    await WaitForPendingCoordinatorOperationsAsync(view).WaitAsync(TimeSpan.FromSeconds(10));
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                    Assert.False(group.IsExpanded);
                    var finalTop = Header().TranslatePoint(default, viewer)!.Value.Y;
                    var maximum = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
                    var desired = Math.Clamp(viewer.Offset.Y + finalTop - top, 0, maximum);
                    Stage($"collapse result: top={finalTop}, offset={viewer.Offset.Y}, extent={viewer.Extent.Height}, viewport={viewer.Viewport.Height}, desired={desired}");
                    Assert.InRange(Math.Abs(viewer.Offset.Y - desired), 0, 1);
                    Assert.InRange(finalTop, 0, viewer.Viewport.Height - Header().Bounds.Height);
                    Assert.False(GetAnchorHostSuspended(view));
                    Assert.True(InvokeQueueLoadOlderRows(coordinator));
                    Stage($"iteration {iteration}: paging");
                    await WaitForPendingCoordinatorOperationsAsync(view).WaitAsync(TimeSpan.FromSeconds(10));
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                    Assert.False(group.IsExpanded); // Preservation must not reveal its hidden members.
                    Assert.InRange(model.Messages.Count, 1, 60);
                }
                Assert.True(model.HasNewerTranscriptRows);
                for (var iteration = 0; iteration < 3; iteration++)
                {
                    Stage($"newer iteration {iteration}");
                    window.MouseWheel(new Point(window.Width / 2, 260), new Vector(0, -5), RawInputModifiers.None);
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                    await WaitForPendingCoordinatorOperationsAsync(view).WaitAsync(TimeSpan.FromSeconds(10));
                    if (model.CanLoadNewerTranscriptRows)
                    {
                        Assert.True(InvokeQueueLoadNewerRows(coordinator));
                        await WaitForPendingCoordinatorOperationsAsync(view).WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    Assert.InRange(model.Messages.Count, 1, 60);
                }
                Assert.NotEmpty(frames);
                Assert.True(blankFrames.Count == 0, string.Join('\n', blankFrames));
                Assert.Equal(history, JsonSerializer.Serialize(services.SessionService.ListTurns(session.SessionId)));
            }
            finally
            {
                tracing = false;
                viewer.LayoutUpdated -= QueueFrame;
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task AgentChatView_ExpandThenManualScrollThenCollapsePreservesCurrentHeader()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profiles = services.ProfileService;
        await profiles.CreateProfileAsync("Scroll regression");
        var workspace = services.WorkspaceService.CreateWorkspace("Scroll regression");
        var session = services.SessionService.CreateSession("Scroll regression", workspaceId: workspace.WorkspaceId);
        for (var i = 0; i < 20; i++)
            services.SessionService.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, $"Before {i}: {new string('x', 300)}");
        services.SessionService.AppendToolCallTurn(session.SessionId, AgentMessageRole.Assistant, "scroll-call", "test_tool", "{}");
        services.SessionService.AppendToolResultTurn(session.SessionId, "scroll-call", "test_tool", "{}",
            string.Join('\n', Enumerable.Range(0, 70).Select(i => $"Output {i}")), "Completed.", null, null, false, false, null, null);
        for (var i = 0; i < 8; i++)
            services.SessionService.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, $"After {i}: {new string('x', 300)}");
        using var view = CreateAgentChatView(scope, services, profiles);
        var window = new Window { Width = 820, Height = 480, Content = view };
        window.Show();
        try
        {
            await view.OnNavigatedToAsync(new PackageViewNavigationContext("younics.ai.agent.chat", new Dictionary<string, string?>()));
            var model = Assert.IsType<AgentChatViewModel>(view.DataContext);
            await RevealCompactCallsAsync(view, model.TranscriptItems.OfType<AgentActivityGroupRowViewModel>().ToArray());
            var row = Assert.Single(model.Messages.OfType<AgentToolInvocationRowViewModel>());
            var repeater = view.FindControl<ItemsRepeater>("TranscriptItemsControl")!;
            var viewer = GetTranscriptScrollViewer(view);
            var coordinator = GetTranscriptScrollCoordinator(view);
            var element = repeater.GetOrCreateElement(model.TranscriptItems.IndexOf(row));
            element.BringIntoView();
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Button Header() => Assert.Single(view.GetVisualDescendants().OfType<Button>(),
                button => button.Classes.Contains("tool-step-header") && ReferenceEquals(button.DataContext, row));
            var top = Header().TranslatePoint(default, viewer)!.Value.Y;
            viewer.Offset = new Vector(0, Math.Clamp(viewer.Offset.Y + top - 140, 0, viewer.Extent.Height - viewer.Viewport.Height));
            Header().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => row.IsExpanded);
            await WaitForPendingCoordinatorOperationsAsync(view);
            window.MouseWheel(new Point(410, 240), new Vector(0, -2), RawInputModifiers.None);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await GetPrivateTask(coordinator, "_userScrollEvaluationOperation");
            var currentTop = Header().TranslatePoint(default, viewer)!.Value.Y;
            Assert.InRange(currentTop, 0, viewer.Viewport.Height);

            Header().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !row.IsExpanded);
            await WaitForPendingCoordinatorOperationsAsync(view);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

            Assert.InRange(Math.Abs(Header().TranslatePoint(default, viewer)!.Value.Y - currentTop), 0, 1);
            Assert.False(GetAnchorHostSuspended(view));
            Assert.InRange(viewer.Offset.Y, 0, viewer.Extent.Height - viewer.Viewport.Height);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task AgentChatView_PrependPagingRetainsVirtualizedAnchorIdentity()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Anchor profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Anchor workspace");
        var session = services.SessionService.CreateSession(
            "Anchor session",
            workspaceId: workspace.WorkspaceId);
        for (var index = 0; index < 90; index++)
        {
            services.SessionService.AppendTextTurn(
                session.SessionId,
                AgentMessageRole.Assistant,
                $"Response {index}: {new string('x', 300)}");
        }

        using var view = RegressionTestAppViews.ChatView(
            profileService,
            services.WorkspaceService,
            services.SessionService,
            NoOpPermissionGateway.Instance,
            NoOpRunGateway.Instance,
            new AgentChatSelectionStateService(scope.Context),
            new AgentToolPresentationService(),
            new AgentExecutionTargetWarmupService(
                services.WorkspaceService,
                services.TargetService),
            null!,
            new AgentAttachmentService(scope.Context),
            NullPackageNotificationService.Instance);
        var window = new Window { Width = 900, Height = 500, Content = view };
        window.Show();
        await view.OnNavigatedToAsync(new PackageViewNavigationContext(
            "younics.ai.agent.chat",
            new Dictionary<string, string?>()));
        await WaitForPendingCoordinatorOperationsAsync(view);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        var coordinator = GetTranscriptScrollCoordinator(view);
        var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
        var repeater = view.FindControl<ItemsRepeater>("TranscriptItemsControl")!;
        var scrollViewer = view.FindControl<ScrollViewer>("TranscriptScrollViewer")!;

        window.MouseMove(new Point(650, 260), RawInputModifiers.None);
        window.MouseWheel(new Point(650, 260), new Vector(0, 12), RawInputModifiers.None);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await GetPrivateTask(coordinator, "_userScrollEvaluationOperation");
        await WaitForPendingCoordinatorOperationsAsync(view);

        Assert.True(GetPrivateBoolean(coordinator, "_userDetached"));
        Assert.False(GetPrivateBoolean(coordinator, "_bottomPlacementLockActive"));
        Assert.True(
            scrollViewer.Offset.Y < scrollViewer.Extent.Height - scrollViewer.Viewport.Height - 1,
            $"History interaction remained at tail; offset={scrollViewer.Offset.Y}, extent={scrollViewer.Extent.Height}, viewport={scrollViewer.Viewport.Height}.");

        var protectedAnchorKey = coordinator.GetType()
            .GetMethod(
                "CaptureCurrentScrollAnchorKey",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(coordinator, null);
        Assert.NotNull(protectedAnchorKey);
        var activitySuffix = viewModel.TranscriptItems[^2];
        var tailSuffix = viewModel.TranscriptItems[^1];
        Assert.True(scrollViewer.Viewport.Height > 0);
        var initialAnchorIndex = viewModel.Messages
            .Select((row, index) => (row.AnchorKey, Index: index))
            .Single(item => Equals(item.AnchorKey, protectedAnchorKey))
            .Index;
        var initialAnchorElement = Assert.IsAssignableFrom<Control>(
            repeater.TryGetElement(initialAnchorIndex));
        var initialAnchorY = Assert.IsType<Point>(
            initialAnchorElement.TranslatePoint(default, scrollViewer)).Y;

        var queued = Assert.IsType<bool>(coordinator.GetType()
            .GetMethod(
                "QueueLoadOlderRows",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(coordinator, null));
        Assert.True(queued);
        await WaitForPendingCoordinatorOperationsAsync(view);
        var writesAtPageCompletion = GetCoordinatorProgrammaticOffsetWrites(coordinator);
        for (var pass = 0; pass < 3; pass++)
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        Assert.Equal(writesAtPageCompletion, GetCoordinatorProgrammaticOffsetWrites(coordinator));

        var anchorIndex = viewModel.Messages
            .Select((row, index) => (row.AnchorKey, Index: index))
            .Single(item => Equals(item.AnchorKey, protectedAnchorKey))
            .Index;
        var restoredAnchorElement = Assert.IsAssignableFrom<Control>(
            repeater.TryGetElement(anchorIndex));
        var restoredAnchorY = Assert.IsType<Point>(
            restoredAnchorElement.TranslatePoint(default, scrollViewer)).Y;
        Assert.Same(initialAnchorElement, restoredAnchorElement);
        Assert.Equal(protectedAnchorKey, CaptureCurrentScrollAnchorKey(coordinator));
        Assert.True(
            Math.Abs(restoredAnchorY - initialAnchorY) <= 1,
            $"Anchor moved from {initialAnchorY} to {restoredAnchorY}; offset={scrollViewer.Offset.Y}, extent={scrollViewer.Extent.Height}, viewport={scrollViewer.Viewport.Height}.");
        for (var pass = 0; pass < 3; pass++)
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        Assert.Equal(writesAtPageCompletion, GetCoordinatorProgrammaticOffsetWrites(coordinator));
        Assert.Equal(60, viewModel.Messages.Count);
        Assert.Same(activitySuffix, viewModel.TranscriptItems[^2]);
        Assert.Same(tailSuffix, viewModel.TranscriptItems[^1]);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AgentChatView_BlockedOlderPageUsesCurrentManualAnchorForNinetyRowTrim()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Manual page authority profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Manual page authority workspace");
        var session = services.SessionService.CreateSession(
            "Manual page authority session",
            workspaceId: workspace.WorkspaceId);
        for (var index = 0; index < 90; index++)
        {
            services.SessionService.AppendTextTurn(
                session.SessionId,
                AgentMessageRole.Assistant,
                $"Manual authority response {index}: {new string('x', 300)}");
        }

        var sessionGateway = new BlockingPageSessionGateway(
            services.SessionService,
            BlockedPageDirection.Older);
        using var view = RegressionTestAppViews.ChatView(
            profileService,
            services.WorkspaceService,
            sessionGateway,
            NoOpPermissionGateway.Instance,
            NoOpRunGateway.Instance,
            new AgentChatSelectionStateService(scope.Context),
            new AgentToolPresentationService(),
            new AgentExecutionTargetWarmupService(services.WorkspaceService, services.TargetService),
            null!,
            new AgentAttachmentService(scope.Context),
            NullPackageNotificationService.Instance);
        var window = new Window { Width = 900, Height = 500, Content = view };
        window.Show();
        try
        {
            await view.OnNavigatedToAsync(new PackageViewNavigationContext(
                "younics.ai.agent.chat",
                new Dictionary<string, string?>()));
            await GetSettledScrollOperation(view).WaitAsync(TimeSpan.FromSeconds(3));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
            var transcript = GetTranscriptScrollViewer(view);
            var coordinator = GetTranscriptScrollCoordinator(view);

            var queuedAnchorKey = CaptureCurrentScrollAnchorKey(coordinator);
            Assert.NotNull(queuedAnchorKey);
            Assert.True(InvokeQueueLoadOlderRows(coordinator));
            await sessionGateway.BlockedLoadStarted.Task;

            window.MouseMove(new Point(650, 260), RawInputModifiers.None);
            window.MouseWheel(new Point(650, 260), new Vector(0, 12), RawInputModifiers.None);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            await GetPrivateTask(coordinator, "_userScrollEvaluationOperation");
            var currentAnchorKey = GetPrivateField(coordinator, "_activePageProtectedAnchorKey");
            Assert.NotNull(currentAnchorKey);
            Assert.NotEqual(queuedAnchorKey, currentAnchorKey);
            var currentAnchorIndex = viewModel.Messages
                .Select((row, index) => (row.AnchorKey, Index: index))
                .Single(item => Equals(item.AnchorKey, currentAnchorKey))
                .Index;
            Assert.Equal(currentAnchorKey, CaptureCurrentScrollAnchorKey(coordinator));
            var repeater = view.FindControl<ItemsRepeater>("TranscriptItemsControl")!;
            var currentVisual = Assert.IsAssignableFrom<Control>(repeater.TryGetElement(currentAnchorIndex));
            var currentTop = currentVisual.TranslatePoint(default, transcript)!.Value.Y;
            var authorityAfterInteraction = GetCoordinatorAuthorityRevision(coordinator);
            var writesAfterInteraction = GetCoordinatorProgrammaticOffsetWrites(coordinator);

            sessionGateway.ReleaseBlockedLoad.TrySetResult();
            await GetPendingCoordinatorOperations(view);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

            Assert.Equal(60, viewModel.Messages.Count);
            var retainedAnchorIndex = viewModel.Messages
                .Select((row, index) => (row.AnchorKey, Index: index))
                .Single(item => Equals(item.AnchorKey, currentAnchorKey))
                .Index;
            Assert.InRange(retainedAnchorIndex, 0, viewModel.Messages.Count - 1);
            var retainedVisual = Assert.IsAssignableFrom<Control>(repeater.TryGetElement(retainedAnchorIndex));
            Assert.Same(currentVisual, retainedVisual);
            Assert.InRange(Math.Abs(retainedVisual.TranslatePoint(default, transcript)!.Value.Y - currentTop), 0, 1);
            Assert.Equal(authorityAfterInteraction, GetCoordinatorAuthorityRevision(coordinator));
            var diagnostic = Assert.IsType<TranscriptScrollCoordinator>(coordinator).DiagnosticSnapshot;
            if (diagnostic.ProgrammaticOffsetWrites > writesAfterInteraction)
            {
                var correction = diagnostic.LastProgrammaticOffsetWrite!.Value;
                Assert.Equal(TranscriptProgrammaticOffsetWriteSource.AnchorRestoration, correction.Source);
                Assert.Equal(diagnostic.InteractionRevision, correction.InteractionRevision);
                Assert.Equal(authorityAfterInteraction, correction.AuthorityRevision);
            }
            Assert.False(GetAnchorHostSuspended(view));
        }
        finally
        {
            sessionGateway.ReleaseBlockedLoad.TrySetResult();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AgentChatView_BlockedOlderPageCannotOverwriteExpansionAuthority()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Blocked older profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Blocked older workspace");
        var session = services.SessionService.CreateSession(
            "Blocked older session",
            workspaceId: workspace.WorkspaceId);
        for (var index = 0; index < 94; index++)
        {
            services.SessionService.AppendTextTurn(
                session.SessionId,
                AgentMessageRole.Assistant,
                $"Older response {index}: {new string('x', 120)}");
        }
        services.SessionService.AppendToolCallTurn(
            session.SessionId,
            AgentMessageRole.Assistant,
            "blocked-older-call",
            "test_tool",
            "{}");
        services.SessionService.AppendToolResultTurn(
            session.SessionId,
            "blocked-older-call",
            "test_tool",
            "{}",
            string.Join('\n', Enumerable.Range(0, 50).Select(index => $"Older output {index}")),
            "Completed.",
            null,
            null,
            false,
            false,
            null,
            null);
        var sessionGateway = new BlockingPageSessionGateway(
            services.SessionService,
            BlockedPageDirection.Older);
        using var view = RegressionTestAppViews.ChatView(
            profileService,
            services.WorkspaceService,
            sessionGateway,
            NoOpPermissionGateway.Instance,
            NoOpRunGateway.Instance,
            new AgentChatSelectionStateService(scope.Context),
            new AgentToolPresentationService(),
            new AgentExecutionTargetWarmupService(services.WorkspaceService, services.TargetService),
            null!,
            new AgentAttachmentService(scope.Context),
            NullPackageNotificationService.Instance);
        var window = new Window { Width = 900, Height = 520, Content = view };
        window.Show();
        try
        {
            await view.OnNavigatedToAsync(new PackageViewNavigationContext(
                "younics.ai.agent.chat",
                new Dictionary<string, string?>()));
            await GetSettledScrollOperation(view).WaitAsync(TimeSpan.FromSeconds(3));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
            var transcript = GetTranscriptScrollViewer(view);
            var coordinator = GetTranscriptScrollCoordinator(view);
            var toolRow = Assert.Single(viewModel.Messages.OfType<AgentToolInvocationRowViewModel>());
            await RevealCompactCallsAsync(view, viewModel.TranscriptItems.OfType<AgentActivityGroupRowViewModel>().ToArray());
            Button GetToolHeader() => Assert.Single(
                view.GetVisualDescendants().OfType<Button>(),
                button => button.Classes.Contains("tool-step-header")
                          && ReferenceEquals(button.DataContext, toolRow));
            var toolPresenter = Assert.Single(
                GetToolHeader().GetVisualAncestors().OfType<Control>(),
                candidate => candidate.GetType().Name == nameof(TranscriptRowPresenter));
            var pageChangingCount = 0;
            var transcriptChangedCount = 0;
            viewModel.TranscriptChanging += isPageApplication =>
            {
                if (isPageApplication)
                {
                    pageChangingCount++;
                }
            };
            viewModel.TranscriptChanged += () => transcriptChangedCount++;

            Assert.True(InvokeQueueLoadOlderRows(coordinator));
            await sessionGateway.BlockedLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(GetAnchorHostSuspended(view));
            Assert.True(Assert.IsType<TranscriptScrollCoordinator>(coordinator).DiagnosticSnapshot.ExactAnchorLeaseActive);

            GetToolHeader().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !toolRow.IsPreparing);
            Assert.True(
                toolRow.IsExpanded || toolRow.IsDetailLoadFailed,
                $"Tool expansion returned to collapsed state; materialized={toolRow.HasMaterializedDetails}, "
                + $"coordinator={GetCoordinatorDiagnosticSnapshot(coordinator)}.");
            Assert.False(toolRow.IsDetailLoadFailed, toolRow.DetailLoadFailureText);
            await GetViewportMutationCompletionOperation(coordinator).WaitAsync(TimeSpan.FromSeconds(10));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.True(toolRow.IsExpanded);
            var expansionMutation = GetCoordinatorMutationDiagnostic(coordinator);
            var authorityAfterExpansion = GetCoordinatorAuthorityRevision(coordinator);
            var writesAfterExpansion = GetCoordinatorProgrammaticOffsetWrites(coordinator);

            sessionGateway.ReleaseBlockedLoad.TrySetResult();
            await GetPendingCoordinatorOperations(view).WaitAsync(TimeSpan.FromSeconds(10));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

            Assert.True(pageChangingCount > 0);
            Assert.True(transcriptChangedCount > 0);
            Assert.Equal(expansionMutation, GetCoordinatorMutationDiagnostic(coordinator));
            Assert.Equal(authorityAfterExpansion, GetCoordinatorAuthorityRevision(coordinator));
            Assert.Equal(writesAfterExpansion, GetCoordinatorProgrammaticOffsetWrites(coordinator));
            var currentToolRow = Assert.Single(
                viewModel.Messages.OfType<AgentToolInvocationRowViewModel>());
            Assert.Same(toolRow, currentToolRow);
            Assert.True(toolRow.IsExpanded);
            Assert.False(GetAnchorHostSuspended(view));
        }
        finally
        {
            sessionGateway.ReleaseBlockedLoad.TrySetResult();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AgentChatView_BlockedNewerPageCannotOverwriteResizeAuthority()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Blocked newer profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Blocked newer workspace");
        var session = services.SessionService.CreateSession(
            "Blocked newer session",
            workspaceId: workspace.WorkspaceId);
        for (var index = 0; index < 65; index++)
        {
            services.SessionService.AppendTextTurn(
                session.SessionId,
                AgentMessageRole.Assistant,
                $"Newer response {index}: {new string('x', 160)}");
        }
        var sessionGateway = new BlockingPageSessionGateway(
            services.SessionService,
            BlockedPageDirection.Newer);
        using var view = RegressionTestAppViews.ChatView(
            profileService,
            services.WorkspaceService,
            sessionGateway,
            NoOpPermissionGateway.Instance,
            NoOpRunGateway.Instance,
            new AgentChatSelectionStateService(scope.Context),
            new AgentToolPresentationService(),
            new AgentExecutionTargetWarmupService(services.WorkspaceService, services.TargetService),
            null!,
            new AgentAttachmentService(scope.Context),
            NullPackageNotificationService.Instance);
        var window = new Window { Width = 900, Height = 520, Content = view };
        window.Show();
        try
        {
            await view.OnNavigatedToAsync(new PackageViewNavigationContext(
                "younics.ai.agent.chat",
                new Dictionary<string, string?>()));
            await GetSettledScrollOperation(view).WaitAsync(TimeSpan.FromSeconds(3));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
            var transcript = GetTranscriptScrollViewer(view);
            var coordinator = GetTranscriptScrollCoordinator(view);

            window.MouseMove(new Point(650, 260), RawInputModifiers.None);
            window.MouseWheel(new Point(650, 260), new Vector(0, 2), RawInputModifiers.None);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await GetPendingCoordinatorOperations(view).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(GetTranscriptFollowingLatest(viewModel));

            var liveRowBuffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void ObserveBufferedLiveRow()
            {
                if (viewModel.HasNewerTranscriptRows)
                {
                    liveRowBuffered.TrySetResult();
                }
            }
            viewModel.TranscriptChanged += ObserveBufferedLiveRow;
            try
            {
                services.SessionService.AppendTextTurn(
                    session.SessionId,
                    AgentMessageRole.Assistant,
                    "Buffered row for blocked newer page.");
                ObserveBufferedLiveRow();
                await liveRowBuffered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally
            {
                viewModel.TranscriptChanged -= ObserveBufferedLiveRow;
            }
            Assert.True(viewModel.HasNewerTranscriptRows);
            var pageChangingCount = 0;
            var transcriptChangedCount = 0;
            viewModel.TranscriptChanging += isPageApplication =>
            {
                if (isPageApplication)
                {
                    pageChangingCount++;
                }
            };
            viewModel.TranscriptChanged += () => transcriptChangedCount++;

            Assert.True(InvokeQueueLoadNewerRows(coordinator));
            await sessionGateway.BlockedLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(GetAnchorHostSuspended(view));
            Assert.True(Assert.IsType<TranscriptScrollCoordinator>(coordinator).DiagnosticSnapshot.ExactAnchorLeaseActive);

            window.Width = 680;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await GetViewportMutationCompletionOperation(coordinator).WaitAsync(TimeSpan.FromSeconds(3));
            var resizeMutation = GetCoordinatorMutationDiagnostic(coordinator);
            var authorityAfterResize = GetCoordinatorAuthorityRevision(coordinator);
            var writesAfterResize = GetCoordinatorProgrammaticOffsetWrites(coordinator);

            sessionGateway.ReleaseBlockedLoad.TrySetResult();
            await GetPendingCoordinatorOperations(view).WaitAsync(TimeSpan.FromSeconds(3));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

            Assert.True(pageChangingCount > 0);
            Assert.True(transcriptChangedCount > 0);
            Assert.Equal(resizeMutation, GetCoordinatorMutationDiagnostic(coordinator));
            Assert.Equal(authorityAfterResize, GetCoordinatorAuthorityRevision(coordinator));
            Assert.Equal(writesAfterResize, GetCoordinatorProgrammaticOffsetWrites(coordinator));
            Assert.False(GetTranscriptFollowingLatest(viewModel));
            Assert.False(GetAnchorHostSuspended(view));
        }
        finally
        {
            sessionGateway.ReleaseBlockedLoad.TrySetResult();
            window.Close();
        }
    }

    private sealed class BlockingPageSessionGateway(
        IAgentSessionGateway inner,
        BlockedPageDirection blockedDirection)
        : CountingSessionGateway(inner)
    {
        public TaskCompletionSource BlockedLoadStarted { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseBlockedLoad { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override IReadOnlyList<AgentTurnRecord> ListTurnsBefore(
            Guid sessionId,
            DateTimeOffset beforeCreatedAtUtc,
            Guid beforeTurnId,
            int limit)
        {
            if (blockedDirection == BlockedPageDirection.Older)
            {
                BlockedLoadStarted.TrySetResult();
                ReleaseBlockedLoad.Task.GetAwaiter().GetResult();
            }

            return base.ListTurnsBefore(sessionId, beforeCreatedAtUtc, beforeTurnId, limit);
        }

        public override IReadOnlyList<AgentTurnRecord> ListTurnsAfter(
            Guid sessionId,
            DateTimeOffset afterCreatedAtUtc,
            Guid afterTurnId,
            int limit)
        {
            if (blockedDirection == BlockedPageDirection.Newer)
            {
                BlockedLoadStarted.TrySetResult();
                ReleaseBlockedLoad.Task.GetAwaiter().GetResult();
            }

            return base.ListTurnsAfter(sessionId, afterCreatedAtUtc, afterTurnId, limit);
        }
    }

    private enum BlockedPageDirection
    {
        Older,
        Newer,
    }

    private static bool InvokeQueueLoadNewerRows(object coordinator)
        => Assert.IsType<bool>(coordinator.GetType()
            .GetMethod(
                "QueueLoadNewerRows",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(coordinator, [false]));
}
