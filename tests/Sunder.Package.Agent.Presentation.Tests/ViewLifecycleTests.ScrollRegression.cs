extern alias AgentApp;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using AgentApp::Sunder.Package.Agent.PackageViews;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed partial class ViewLifecycleTests
{
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
}
