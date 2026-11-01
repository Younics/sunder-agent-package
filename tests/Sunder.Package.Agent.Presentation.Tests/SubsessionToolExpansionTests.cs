extern alias AgentApp;
extern alias SubagentsApp;

using AgentApp::Sunder.Package.Agent.Shared.PackageViews;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Xunit;
using static Sunder.Package.Agent.Presentation.Tests.RenderedTranscriptGeometry;
using static Sunder.Package.Agent.Presentation.Tests.RenderedViewComposition;
using static Sunder.Package.Agent.Presentation.Tests.RenderedViewWaits;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class SubsessionToolExpansionTests
{
    [AvaloniaFact]
    public async Task SubsessionsView_RealToolExpansionUsesExactHeaderAuthorityInBothModes()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        var profile = await profileService.CreateProfileAsync("Subsession expansion profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Subsession expansion workspace");
        var parent = services.SessionService.CreateSession(
            "Subsession expansion parent",
            workspaceId: workspace.WorkspaceId);
        var child = services.SessionService.CreateSession(
            "Subsession expansion child",
            parentSessionId: parent.SessionId,
            rootSessionId: parent.SessionId,
            profileId: profile.ProfileId,
            agentKind: "subagent");
        for (var index = 0; index < 14; index++)
        {
            services.SessionService.AppendTextTurn(
                child.SessionId,
                AgentMessageRole.Assistant,
                $"Before tool {index}.");
        }
        services.SessionService.AppendToolCallTurn(
            child.SessionId,
            AgentMessageRole.Assistant,
            "subsession-expansion-call",
            "test_tool",
            "{}");
        services.SessionService.AppendToolResultTurn(
            child.SessionId,
            "subsession-expansion-call",
            "test_tool",
            "{}",
            string.Join('\n', Enumerable.Range(0, 70).Select(index => $"Output line {index}")),
            "Completed.",
            null,
            null,
            false,
            false,
            null,
            null);
        for (var index = 0; index < 3; index++)
        {
            services.SessionService.AppendTextTurn(
                child.SessionId,
                AgentMessageRole.Assistant,
                $"After tool {index}: {new string('y', 80)}");
        }
        services.ExtensionCatalog.AddRuntimeCatalog(
            new AgentRuntimeCatalog(services.SessionService, profileService, services.WorkspaceService));

        using var runtimeAdapter = new CatalogBackedSubsessionRuntimeAdapter(scope.Context, services.ExtensionCatalog);
        var viewModel = runtimeAdapter.CreateViewModel();
        using var view = new SubsessionsView(viewModel);
        var window = new Window { Width = 900, Height = 900, Content = view };
        window.Show();
        try
        {
            await view.OnNavigatedToAsync(new PackageViewNavigationContext(
                    SubagentConstants.SubsessionsViewId,
                    new Dictionary<string, string?>
                    {
                        [SubagentConstants.SubsessionNavigationSessionIdKey] = child.SessionId.ToString("D"),
                    }));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await GetPendingCoordinatorOperations(view);
            var transcript = Assert.IsType<ScrollViewer>(
                view.FindControl<ScrollViewer>("TranscriptScrollViewer"));
            var repeater = Assert.IsType<ItemsRepeater>(
                view.FindControl<ItemsRepeater>("TranscriptItemsControl"));
            foreach (var group in viewModel.TranscriptItems.OfType<SubagentsApp::Sunder.Package.Agent.Subagents.PackageViews.SubsessionActivityGroupRowViewModel>().ToArray()) group.IsExpanded = true;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await GetPendingCoordinatorOperations(view);
            var toolRow = Assert.Single(viewModel.Messages.OfType<SubsessionToolInvocationRowViewModel>());
            Assert.Same(viewModel.RunActivityRow, viewModel.TranscriptItems[^2]);
            Assert.IsType<SubsessionTranscriptTailSentinelRowViewModel>(viewModel.TranscriptItems[^1]);
            Button GetToolHeader() => Assert.Single(
                view.GetVisualDescendants().OfType<Button>(),
                button => button.Classes.Contains("tool-step-header")
                          && ReferenceEquals(button.DataContext, toolRow));
            var coordinator = GetTranscriptScrollCoordinator(view);
            transcript.Offset = new Vector(
                0,
                Math.Max(0, transcript.Extent.Height - transcript.Viewport.Height));
            await WaitForTranscriptGeometrySettledAsync(window, transcript, coordinator);
            AssertSubsessionFollowingParity(coordinator, viewModel, expected: true);
            var expectedFollowingWrites = GetCoordinatorProgrammaticOffsetWrites(coordinator);
            var followingOffset = transcript.Offset.Y;
            var followingToolTop = Assert.IsType<Point>(
                GetToolHeader().TranslatePoint(default, transcript)).Y;
            Assert.InRange(
                followingToolTop,
                0,
                transcript.Viewport.Height - GetToolHeader().Bounds.Height);
            var programmaticTrace = new List<bool>();
            var viewportTrace = new List<string>();
            List<ToolExpansionFrame>? activeFrameTrace = null;
            ToolExpansionFrame CaptureToolFrame()
            {
                var header = GetToolHeader();
                var toolIndex = viewModel.TranscriptItems
                    .Select((row, index) => (row.AnchorKey, Index: index))
                    .Single(item => Equals(item.AnchorKey, toolRow.AnchorKey))
                    .Index;
                RecordDiagnostic(viewportTrace, "tool-frame: " + string.Join("; ", viewModel.TranscriptItems
                    .Skip(toolIndex).Select(row =>
                    {
                        var index = viewModel.TranscriptItems.IndexOf(row);
                        var control = repeater.TryGetElement(index) as Control;
                        return $"{row.AnchorKey}: top={control?.TranslatePoint(default, transcript)?.Y:F3}, height={control?.Bounds.Height:F3}, visible={row.IsLayoutVisible}, minimum={row.MinimumLayoutHeight:F3}";
                    })));
                return new ToolExpansionFrame(
                    Assert.IsType<Point>(header.TranslatePoint(default, transcript)).Y,
                    CaptureFollowingRowPositions(
                        viewModel.TranscriptItems,
                        repeater,
                        transcript,
                        toolIndex,
                        static row => row.AnchorKey,
                        // Internal one-pixel activity/tail markers can follow a newly
                        // realized markdown row's final measure without moving content.
                        // Their identities and following state are asserted separately.
                        static row => row.IsLayoutVisible && row is not SubsessionTranscriptTailSentinelRowViewModel));
            }
            string DescribeToolExpansionState(string stage)
                => $"{stage}: expanded={toolRow.IsExpanded}, preparing={toolRow.IsPreparing}, failed={toolRow.IsDetailLoadFailed}, materialized={toolRow.HasMaterializedDetails}; "
                   + DescribeSubsessionTranscriptState(view, transcript, coordinator, stage);
            transcript.PropertyChanged += (_, change) =>
            {
                if (change.Property == ScrollViewer.OffsetProperty)
                {
                    programmaticTrace.Add(GetPrivateBoolean(coordinator, "_isProgrammaticScroll"));
                    RecordDiagnostic(
                        viewportTrace,
                        DescribeSubsessionTranscriptState(
                            view,
                            transcript,
                            coordinator,
                            "offset-changed"));
                }
            };
            var renderedFrameCaptureQueued = false;
            transcript.LayoutUpdated += (_, _) =>
            {
                if (activeFrameTrace is null || renderedFrameCaptureQueued)
                {
                    return;
                }

                renderedFrameCaptureQueued = true;
                Dispatcher.UIThread.Post(() =>
                {
                    renderedFrameCaptureQueued = false;
                    if (activeFrameTrace is not null && view.IsAttachedToVisualTree())
                    {
                        activeFrameTrace.Add(CaptureToolFrame());
                    }
                }, DispatcherPriority.Background);
            };

            for (var iteration = 0; iteration < 2; iteration++)
            {
                var expanding = !toolRow.IsExpanded;
                programmaticTrace.Clear();
                viewportTrace.Clear();
                activeFrameTrace = [CaptureToolFrame()];
                GetToolHeader().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(
                    () => !toolRow.IsPreparing,
                    () => DescribeToolExpansionState("following-toggle-timeout"));
                Assert.True(
                    toolRow.IsExpanded == expanding || toolRow.IsDetailLoadFailed,
                    DescribeToolExpansionState("following-toggle-settled-unexpectedly"));
                Assert.False(
                    toolRow.IsDetailLoadFailed,
                    $"{toolRow.DetailLoadFailureText} {DescribeToolExpansionState("following-toggle-failed")}");
                await GetPendingCoordinatorOperations(view);
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
                activeFrameTrace.Add(CaptureToolFrame());

                Assert.Equal(expanding, toolRow.IsExpanded);
                var followingWrites = GetCoordinatorProgrammaticOffsetWrites(coordinator);
                var collapseClamps = GetCoordinatorMutationCollapseClamps(coordinator);
                if (expanding)
                {
                    Assert.DoesNotContain(true, programmaticTrace);
                    Assert.Equal(expectedFollowingWrites, followingWrites);
                    Assert.Equal(0, collapseClamps);
                }
                else
                {
                    Assert.InRange(programmaticTrace.Count(static value => value), 0, 1);
                    Assert.InRange(followingWrites - expectedFollowingWrites, 0, 1);
                    Assert.Equal(
                        followingWrites - expectedFollowingWrites,
                        collapseClamps);
                }
                expectedFollowingWrites = followingWrites;
                Assert.Equal("Completed", GetCoordinatorMutationStatus(coordinator));
                var followingToolDelta = Math.Abs(Assert.IsType<Point>(
                        GetToolHeader().TranslatePoint(default, transcript)).Y
                    - followingToolTop);
                if (collapseClamps == 0)
                {
                    Assert.True(
                        followingToolDelta <= 0.1,
                        $"Following-mode tool header moved {followingToolDelta:F3}px during "
                        + $"{(expanding ? "expansion" : "collapse")} iteration {iteration}; "
                        + $"offset {followingOffset:F3}->{transcript.Offset.Y:F3}, "
                        + $"current anchor {transcript.CurrentAnchor?.GetType().Name ?? "none"}, "
                        + $"diagnostic {DescribeSubsessionTranscriptState(view, transcript, coordinator, "assertion")}.");
                    AssertToolExpansionFrames(
                        activeFrameTrace,
                        followingToolTop,
                        expanding,
                        viewportTrace);
                }
                else
                {
                    AssertToolCollapseClampFrames(activeFrameTrace, viewportTrace);
                }
                activeFrameTrace = null;
                Assert.Equal("ToolExpansionNativeAnchor", GetCoordinatorMutationMode(coordinator));
                AssertSubsessionFollowingParity(coordinator, viewModel, expected: false);
                Assert.True(view.FindControl<Button>("JumpToLatestTranscriptButton")!.IsVisible);
                Assert.False(GetAnchorHostSuspended(view));
            }

            window.MouseMove(new Point(650, 260), RawInputModifiers.None);
            window.MouseWheel(new Point(650, 260), new Vector(0, 2), RawInputModifiers.None);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await GetPendingCoordinatorOperations(view);
            AssertSubsessionFollowingParity(coordinator, viewModel, expected: false);

            var detachedBottomGap = transcript.Viewport.Height * 0.4;
            transcript.Offset = new Vector(
                0,
                Math.Max(
                    0,
                    transcript.Extent.Height - transcript.Viewport.Height - detachedBottomGap));
            await WaitForTranscriptGeometrySettledAsync(window, transcript, coordinator);
            Assert.True(
                transcript.Extent.Height - transcript.Viewport.Height - transcript.Offset.Y
                > transcript.Viewport.Height * 0.25);
            AssertSubsessionFollowingParity(coordinator, viewModel, expected: false);

            services.SessionService.AppendTextTurn(
                child.SessionId,
                AgentMessageRole.Assistant,
                "Live child update while manually detached.");
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            await GetPendingCoordinatorOperations(view);
            await WaitUntilAsync(
                () => viewModel.HasNewerTranscriptRows,
                () => DescribeSubsessionTranscriptState(
                    view,
                    transcript,
                    coordinator,
                    "waiting-for-live-detached-row"));
            await GetPendingRuntimeRefresh(viewModel).WaitAsync(TimeSpan.FromSeconds(10));
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            await GetPendingCoordinatorOperations(view);
            Assert.True(viewModel.HasNewerTranscriptRows,
                $"Buffered live row was consumed without tail intent; direction={GetPrivateField(coordinator, "_lastUserScrollDirection")}; "
                + DescribeSubsessionTranscriptState(view, transcript, coordinator, "live-buffer-assertion")
                + Environment.NewLine + string.Join(Environment.NewLine, viewportTrace));
            Assert.DoesNotContain(
                viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>(),
                row => row.Content == "Live child update while manually detached.");
            AssertSubsessionFollowingParity(coordinator, viewModel, expected: false);
            await WaitForTranscriptGeometrySettledAsync(window, transcript, coordinator);

            var toolPresenter = Assert.Single(
                GetToolHeader().GetVisualAncestors().OfType<Control>(),
                candidate => candidate.GetType().Name == nameof(TranscriptRowPresenter));
            var currentToolTop = Assert.IsType<Point>(toolPresenter.TranslatePoint(default, transcript)).Y;
            transcript.Offset = new Vector(
                0,
                Math.Clamp(transcript.Offset.Y + currentToolTop - 20, 0, transcript.Extent.Height));
            await WaitForTranscriptGeometrySettledAsync(window, transcript, coordinator);
            var protectedTop = Assert.IsType<Point>(
                GetToolHeader().TranslatePoint(default, transcript)).Y;
            var writesBeforeDetachedMutation = GetCoordinatorProgrammaticOffsetWrites(coordinator);
            activeFrameTrace = [CaptureToolFrame()];
            viewportTrace.Clear();
            RecordDiagnostic(
                viewportTrace,
                DescribeSubsessionTranscriptState(
                    view,
                    transcript,
                    coordinator,
                    $"detached-established/top={protectedTop:F3}"));

            GetToolHeader().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(
                () => toolRow.IsExpanded || toolRow.IsDetailLoadFailed,
                () => DescribeToolExpansionState("detached-expansion-timeout"));
            Assert.False(
                toolRow.IsDetailLoadFailed,
                $"{toolRow.DetailLoadFailureText} {DescribeToolExpansionState("detached-expansion-failed")}");
            RecordDiagnostic(
                viewportTrace,
                DescribeSubsessionTranscriptState(view, transcript, coordinator, "expanded"));
            await GetPendingCoordinatorOperations(view);
            RecordDiagnostic(
                viewportTrace,
                DescribeSubsessionTranscriptState(view, transcript, coordinator, "coordinator-settled"));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            RecordDiagnostic(
                viewportTrace,
                DescribeSubsessionTranscriptState(view, transcript, coordinator, "layout-updated"));

            Assert.True(toolRow.IsExpanded);
            Assert.Equal("Completed", GetCoordinatorMutationStatus(coordinator));
            var restoredTop = Assert.IsType<Point>(
                GetToolHeader().TranslatePoint(default, transcript)).Y;
            activeFrameTrace.Add(CaptureToolFrame());
            Assert.True(
                Math.Abs(restoredTop - protectedTop) <= 0.1,
                $"Detached protected anchor moved {Math.Abs(restoredTop - protectedTop):F3}px ({protectedTop:F3} -> {restoredTop:F3}).{Environment.NewLine}{string.Join(Environment.NewLine, viewportTrace)}");
            AssertToolExpansionFrames(
                activeFrameTrace,
                protectedTop,
                expanding: true,
                viewportTrace);
            activeFrameTrace = null;
            Assert.Equal(
                GetCoordinatorMutationCorrections(coordinator),
                GetCoordinatorProgrammaticOffsetWrites(coordinator) - writesBeforeDetachedMutation);
            Assert.IsNotType<SubsessionActivityTranscriptRowViewModel>(transcript.CurrentAnchor?.DataContext);
            Assert.IsNotType<SubsessionTranscriptTailSentinelRowViewModel>(transcript.CurrentAnchor?.DataContext);
            AssertSubsessionFollowingParity(coordinator, viewModel, expected: false);
            Assert.False(GetAnchorHostSuspended(view));

            var jumpToLatest = Assert.IsType<Button>(
                view.FindControl<Button>("JumpToLatestTranscriptButton"));
            Assert.True(jumpToLatest.IsVisible);
            var tailRestored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void ObserveTailRestored()
            {
                if (GetTranscriptFollowingLatest(viewModel)
                    && !viewModel.HasNewerTranscriptRows
                    && viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>().Any(
                        row => row.Content == "Live child update while manually detached."))
                {
                    tailRestored.TrySetResult();
                }
            }
            viewModel.TranscriptChanged += ObserveTailRestored;
            try
            {
                jumpToLatest.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                ObserveTailRestored();
                await tailRestored.Task;
            }
            finally
            {
                viewModel.TranscriptChanged -= ObserveTailRestored;
            }
            await GetPendingCoordinatorOperations(view);
            await WaitForTranscriptGeometrySettledAsync(window, transcript, coordinator);
            AssertSubsessionFollowingParity(coordinator, viewModel, expected: true);
            await WaitUntilAsync(
                () => transcript.CurrentAnchor?.DataContext is SubsessionTranscriptTailSentinelRowViewModel,
                () => DescribeSubsessionTranscriptState(view, transcript, coordinator, "waiting-for-restored-tail-anchor"));
            Assert.IsType<SubsessionTranscriptTailSentinelRowViewModel>(
                transcript.CurrentAnchor?.DataContext);

            window.MouseMove(new Point(650, 260), RawInputModifiers.None);
            window.MouseWheel(new Point(650, 260), new Vector(0, 2), RawInputModifiers.None);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await GetPendingCoordinatorOperations(view);
            AssertSubsessionFollowingParity(coordinator, viewModel, expected: false);
            await WaitForTranscriptGeometrySettledAsync(window, transcript, coordinator);
            toolRow = Assert.Single(viewModel.Messages.OfType<SubsessionToolInvocationRowViewModel>());
            toolPresenter = Assert.Single(
                GetToolHeader().GetVisualAncestors().OfType<Control>(),
                candidate => candidate.GetType().Name == nameof(TranscriptRowPresenter));
            currentToolTop = Assert.IsType<Point>(toolPresenter.TranslatePoint(default, transcript)).Y;
            transcript.Offset = new Vector(
                0,
                Math.Clamp(transcript.Offset.Y + currentToolTop - 20, 0, transcript.Extent.Height));
            await WaitForTranscriptGeometrySettledAsync(window, transcript, coordinator);
            protectedTop = Assert.IsType<Point>(
                GetToolHeader().TranslatePoint(default, transcript)).Y;
            activeFrameTrace = [CaptureToolFrame()];
            viewportTrace.Clear();
            var writesBeforeDetachedCollapse = GetCoordinatorProgrammaticOffsetWrites(coordinator);
            RecordDiagnostic(
                viewportTrace,
                DescribeSubsessionTranscriptState(
                    view,
                    transcript,
                    coordinator,
                    $"detached-established/top={protectedTop:F3}"));

            GetToolHeader().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            RecordDiagnostic(
                viewportTrace,
                DescribeSubsessionTranscriptState(view, transcript, coordinator, "collapsed"));
            await GetPendingCoordinatorOperations(view);
            RecordDiagnostic(
                viewportTrace,
                DescribeSubsessionTranscriptState(view, transcript, coordinator, "coordinator-settled"));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            RecordDiagnostic(
                viewportTrace,
                DescribeSubsessionTranscriptState(view, transcript, coordinator, "layout-updated"));

            Assert.False(toolRow.IsExpanded);
            Assert.Equal("Completed", GetCoordinatorMutationStatus(coordinator));
            var detachedCollapseClamps = GetCoordinatorMutationCollapseClamps(coordinator);
            var detachedCollapseWrites = GetCoordinatorProgrammaticOffsetWrites(coordinator)
                                        - writesBeforeDetachedCollapse;
            Assert.InRange(detachedCollapseWrites, 0, 1);
            Assert.Equal(detachedCollapseWrites, detachedCollapseClamps);
            restoredTop = Assert.IsType<Point>(
                GetToolHeader().TranslatePoint(default, transcript)).Y;
            activeFrameTrace.Add(CaptureToolFrame());
            if (detachedCollapseClamps == 0)
            {
                Assert.True(
                    Math.Abs(restoredTop - protectedTop) <= 0.1,
                    $"Detached protected anchor moved {Math.Abs(restoredTop - protectedTop):F3}px ({protectedTop:F3} -> {restoredTop:F3}).{Environment.NewLine}{string.Join(Environment.NewLine, viewportTrace)}");
                AssertToolExpansionFrames(
                    activeFrameTrace,
                    protectedTop,
                    expanding: false,
                    viewportTrace);
            }
            else
            {
                AssertToolCollapseClampFrames(activeFrameTrace, viewportTrace);
            }
            activeFrameTrace = null;
            AssertSubsessionFollowingParity(coordinator, viewModel, expected: false);
            Assert.False(GetAnchorHostSuspended(view));
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertToolExpansionFrames(
        IReadOnlyList<ToolExpansionFrame> frames,
        double expectedHeaderY,
        bool expanding,
        IReadOnlyList<string>? details)
    {
        Assert.NotEmpty(frames);
        Assert.True(
            frames.All(frame => Math.Abs(frame.HeaderY - expectedHeaderY) <= 0.1),
            $"The clicked tool header moved between rendered frames.{Environment.NewLine}{string.Join(Environment.NewLine, details ?? [])}");
        AssertFollowingRowsMoveMonotonically(
            frames.Select(frame => frame.FollowingRowPositions).ToArray(),
            expanding,
            details);
    }

    private static void AssertToolCollapseClampFrames(
        IReadOnlyList<ToolExpansionFrame> frames,
        IReadOnlyList<string>? details)
    {
        Assert.True(frames.Count >= 2);
        var direction = Math.Sign(frames[^1].HeaderY - frames[0].HeaderY);
        Assert.True(
            frames.Zip(frames.Skip(1)).All(pair => direction >= 0
                ? pair.Second.HeaderY - pair.First.HeaderY >= -0.1
                : pair.Second.HeaderY - pair.First.HeaderY <= 0.1),
            $"The terminal collapse clamp moved the clicked header non-monotonically "
            + $"({string.Join(", ", frames.Select(frame => frame.HeaderY.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)))})."
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, details ?? [])}");
        AssertFollowingRowsMoveMonotonically(
            frames.Select(frame => frame.FollowingRowPositions).ToArray(),
            expanding: false,
            details);
    }

    private sealed record ToolExpansionFrame(
        double HeaderY,
        IReadOnlyDictionary<object, double> FollowingRowPositions);

    private static int GetCoordinatorMutationCorrections(object coordinator)
    {
        var snapshot = coordinator.GetType()
            .GetProperty(
                "DiagnosticSnapshot",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator)!;
        var mutation = snapshot.GetType().GetProperty("Mutation")!.GetValue(snapshot);
        Assert.NotNull(mutation);
        return Assert.IsType<int>(mutation.GetType().GetProperty("Corrections")!.GetValue(mutation));
    }

    private static void AssertSubsessionFollowingParity(
        object coordinator,
        SubsessionsViewModel viewModel,
        bool expected)
    {
        var coordinatorFollowing = Assert.IsType<bool>(coordinator.GetType()
            .GetProperty(
                "IsFollowingTail",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator));
        Assert.Equal(expected, coordinatorFollowing);
        Assert.Equal(expected, GetTranscriptFollowingLatest(viewModel));
    }
}
