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
using Sunder.Package.Agent.Models;
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

public sealed class AgentChatToolExpansionTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AgentChatView_LiveWriteRefreshStreamingThinkingAndCollapsePreserveCurrentHeader(bool detailed, bool userScrolls)
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profiles = services.ProfileService;
        var profile = await profiles.CreateProfileAsync("Live expansion");
        var workspace = services.WorkspaceService.CreateWorkspace("Live expansion");
        var session = services.SessionService.CreateSession("Live expansion", workspaceId: workspace.WorkspaceId);
        for (var index = 0; index < 12; index++) services.SessionService.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant,
            $"### Earlier {index}\n\n" + new string('a', 160 + index * 17));
        var content = string.Join('\n', Enumerable.Range(0, 90).Select(index => $"recorded line {index}"));
        var args = JsonSerializer.Serialize(new { path = "Views/DioramaControl.cs", content });
        var call = services.SessionService.AppendToolCallTurn(session.SessionId, AgentMessageRole.Assistant, "live-write", "write", args);
        var lease = new AgentDurableRunLease(services.SessionService.Store.ReserveRun(session.SessionId, profile.ProfileId, "Live response"));
        Assert.NotNull(services.SessionService.TryTransitionRun(lease, AgentRunStatus.Running, "Thinking."));
        var stream = services.SessionService.AppendTextTurn(lease, AgentMessageRole.Assistant, "Live response");
        for (var index = 0; index < 8; index++) services.SessionService.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant,
            $"Trailing context {index}\n\n" + new string('b', 240));
        var revision = lease.Key.RunRevision;
        var transcriptResets = 0;
        services.SessionService.TranscriptReset += _ => transcriptResets++;
        var gateway = new BlockingToolDetailGateway(services.SessionService);
        using var view = RegressionTestAppViews.ChatView(profiles, services.WorkspaceService, gateway,
            NoOpPermissionGateway.Instance, NoOpRunGateway.Instance, new AgentChatSelectionStateService(scope.Context),
            new AgentToolPresentationService(), new AgentExecutionTargetWarmupService(services.WorkspaceService, services.TargetService),
            null!, new AgentAttachmentService(scope.Context), NullPackageNotificationService.Instance);
        var window = new Window { Width = 820, Height = 600, Content = view };
        window.Show();
        try
        {
            await view.OnNavigatedToAsync(new("younics.ai.agent.chat", new Dictionary<string, string?>()));
            await Settle();
            var model = Assert.IsType<AgentChatViewModel>(view.DataContext);
            var loadingTrace = string.Empty;
            model.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(model.IsTranscriptLoading) && model.IsTranscriptLoading)
                    loadingTrace = Environment.StackTrace;
            };
            model.IsDetailedTranscript = detailed;
            if (!detailed) await RevealCompactCallsAsync(view, model.TranscriptItems.OfType<AgentActivityGroupRowViewModel>().ToArray());
            var row = Assert.Single(model.Messages.OfType<AgentToolInvocationRowViewModel>());
            var viewer = GetTranscriptScrollViewer(view);
            window.MouseMove(new Point(400, 260), RawInputModifiers.None);
            Button? header = null;
            // Reach the row with real scroll input. Manufacturing an off-screen
            // repeater element is not a click a user could have performed.
            for (var pass = 0; pass < 48; pass++)
            {
                var candidate = view.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
                    button.Classes.Contains("tool-step-header") && ReferenceEquals(button.DataContext, row));
                var top = candidate?.TranslatePoint(default, viewer)?.Y;
                if (candidate is not null && top >= 0 && top + candidate.Bounds.Height <= viewer.Viewport.Height)
                { header = candidate; break; }
                window.MouseWheel(new Point(400, 260), new Vector(0, top > viewer.Viewport.Height ? -1 : 2), RawInputModifiers.None);
                await Settle();
            }
            Assert.NotNull(header);
            Assert.InRange(header.TranslatePoint(default, viewer)!.Value.Y, 0, viewer.Viewport.Height - header.Bounds.Height);
            var stage = "initial expansion";
            var invalidation = string.Empty;
            row.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(row.IsExpanded) && !row.IsExpanded)
                    invalidation = $"stage={stage},attached={header.IsAttachedToVisualTree()},offset={viewer.Offset.Y},extent={viewer.Extent.Height},diag={GetCoordinatorDiagnosticSnapshot(GetTranscriptScrollCoordinator(view))}\n{Environment.StackTrace}";
            };
            header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => row.IsExpanded || row.IsDetailLoadFailed);
            Assert.True(row.IsExpanded, row.DetailLoadFailureText);
            await Settle();
            var oldDetails = row.ExpandedDetails;
            Assert.NotNull(oldDetails);
            var payload = JsonSerializer.Serialize(new
            {
                schema = "younics.ai.agent.tools.files.diff.v1",
                files = new[] { new { path = "Views/DioramaControl.cs", operation = "Add", addedLineCount = 90, deletedLineCount = 0,
                    lines = Enumerable.Range(0, 90).Select(index => new { kind = "added", lineNumber = index + 1, text = $"recorded line {index}" }).ToArray() } },
            });
            gateway.BlockNextDetail = true;
            services.SessionService.AppendToolResultTurn(session.SessionId, "live-write", "write", args,
                "Write completed", "Wrote recorded content", null, null, false, false, null, null, payload);
            await gateway.DetailStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var update = 0; update < 4; update++)
            {
                services.SessionService.UpdateTextTurn(lease, stream.TurnId, "Response while refreshing " + new string('s', 120 * (update + 1)));
                services.SessionService.ReportRunActivity(session.SessionId, revision, AgentRunActivityKind.Reasoning, $"Thinking during refresh {update}");
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Assert.True(row.IsExpanded);
                Assert.Same(oldDetails, row.ExpandedDetails);
                Assert.True(row.IsPreparing, row.DetailLoadFailureText);
                Assert.False(gateway.DetailCanceled);
            }
            gateway.ReleaseDetail.TrySetResult();
            await WaitUntilAsync(() => row.IsExpanded && !row.IsPreparing && !ReferenceEquals(row.ExpandedDetails, oldDetails),
                () => $"state={row.ExpansionState}, revision={row.DetailRevision}, failure={row.DetailLoadFailureText}, cancelled={gateway.DetailCanceled}, retained={model.Messages.Contains(row)}");
            Assert.Same(row, Assert.Single(model.Messages.OfType<AgentToolInvocationRowViewModel>()));
            var recorded = services.SessionService.Store.GetTranscriptToolDetail(new AgentTranscriptToolDetailRequest(session.SessionId, ToolExecutionId: call.Items[0].ToolExecutionId));
            Assert.True(row.ExpandedDetails!.HasToolDiff, $"status={row.StatusText},tool={recorded?.ToolId},args={recorded?.ArgumentsJson?.Length},payload={recorded?.PresentationPayloadJson?.Length},failure={row.DetailLoadFailureText}");
            for (var update = 0; update < 4; update++)
            {
                stage = $"live update {update}";
                services.SessionService.UpdateTextTurn(lease, stream.TurnId, "Live response " + new string('s', 120 * (update + 1)));
                services.SessionService.ReportRunActivity(session.SessionId, revision, AgentRunActivityKind.Reasoning, $"Thinking update {update}");
                services.SessionService.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, $"New row {update}");
                await Settle();
                Assert.True(row.IsExpanded, invalidation);
                Assert.True(row.HasMaterializedDetails);
            }
            if (userScrolls)
            {
                window.MouseWheel(new Point(400, 260), new Vector(0, 1), RawInputModifiers.None);
                await Settle();
            }
            var before = header.TranslatePoint(default, viewer)!.Value.Y;
            var frames = new List<double>();
            var observing = true;
            var queued = false;
            void Observe(object? _, EventArgs __)
            {
                if (queued || !observing) return;
                queued = true;
                Dispatcher.UIThread.Post(() =>
                {
                    queued = false;
                    if (!observing) return;
                    Assert.True(header.IsAttachedToVisualTree());
                    Assert.False(model.IsTranscriptLoading, loadingTrace);
                    frames.Add(header.TranslatePoint(default, viewer)!.Value.Y);
                }, DispatcherPriority.Render);
            }
            viewer.LayoutUpdated += Observe;
            try
            {
                header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => !row.IsExpanded);
                await Settle();
                for (var update = 4; update < 8; update++)
                {
                    services.SessionService.UpdateTextTurn(lease, stream.TurnId, "Continued stream " + new string('s', 120 * (update + 1)));
                    services.SessionService.ReportRunActivity(session.SessionId, revision, AgentRunActivityKind.Reasoning, $"Thinking after collapse {update}");
                    services.SessionService.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, $"New row {update}");
                    await Settle();
                }
                Assert.NotEmpty(frames);
                Assert.All(frames, top => Assert.InRange(Math.Abs(top - before), 0, 1));
                Assert.False(row.HasMaterializedDetails);
                Assert.Equal(0, transcriptResets);
            }
            finally { observing = false; viewer.LayoutUpdated -= Observe; }
        }
        finally { gateway.ReleaseDetail.TrySetResult(); window.Close(); }

        async Task Settle()
        {
            await WaitForPendingCoordinatorOperationsAsync(view).WaitAsync(TimeSpan.FromSeconds(5));
            for (var pass = 0; pass < 100; pass++)
            {
                var presenters = view.GetVisualDescendants().OfType<AgentApp::Sunder.Package.Agent.Shared.PackageViews.StreamingMarkdownPresenter>()
                    .Where(presenter => presenter.IsEffectivelyVisible).ToArray();
                await Task.WhenAll(presenters.Select(presenter => presenter.PendingRenderOperations)).WaitAsync(TimeSpan.FromSeconds(5));
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                if (view.GetVisualDescendants().OfType<AgentApp::Sunder.Package.Agent.Shared.PackageViews.StreamingMarkdownPresenter>()
                    .Where(presenter => presenter.IsEffectivelyVisible).All(presenter => !presenter.IsRenderPending)) return;
                await Task.Delay(5);
            }
            Assert.Fail("Live transcript Markdown did not settle within the bounded preparation budget.");
        }
    }

    private sealed class BlockingToolDetailGateway(IAgentSessionGateway inner) : CountingSessionGateway(inner)
    {
        public bool BlockNextDetail { get; set; }
        public bool DetailCanceled { get; private set; }
        public TaskCompletionSource DetailStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDetail { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<AgentTranscriptToolDetailRecord?> LoadToolDetailAsync(AgentTranscriptToolDetailRequest request,
            CancellationToken cancellationToken = default)
        {
            if (BlockNextDetail)
            {
                BlockNextDetail = false;
                DetailStarted.TrySetResult();
                try { await ReleaseDetail.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { DetailCanceled = true; throw; }
            }
            return await base.LoadToolDetailAsync(request, cancellationToken);
        }
    }

    [AvaloniaFact]
    public async Task AgentChatView_RealToolExpansionKeepsClickedHeaderFixedAndDetachesTail()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Expansion profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Expansion workspace");
        var session = services.SessionService.CreateSession(
            "Expansion session",
            workspaceId: workspace.WorkspaceId);
        for (var index = 0; index < 24; index++)
        {
            services.SessionService.AppendTextTurn(
                session.SessionId,
                AgentMessageRole.Assistant,
                $"Variable response {index}: {new string('x', 80 + index * 9)}");
        }
        services.SessionService.AppendToolCallTurn(
            session.SessionId,
            AgentMessageRole.Assistant,
            "expansion-call",
            "test_tool",
            "{}");
        services.SessionService.AppendToolResultTurn(
            session.SessionId,
            "expansion-call",
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

        using var view = CreateAgentChatView(scope, services, profileService);
        var window = new Window { Width = 820, Height = 480, Content = view };
        window.Show();
        try
        {
            await Task.Run(async () => await view.OnNavigatedToAsync(
                new PackageViewNavigationContext(
                    "younics.ai.agent.chat",
                    new Dictionary<string, string?>())));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
            var transcript = GetTranscriptScrollViewer(view);
            var repeater = Assert.IsType<ItemsRepeater>(
                view.FindControl<ItemsRepeater>("TranscriptItemsControl"));
            await RevealCompactCallsAsync(view, viewModel.TranscriptItems.OfType<AgentActivityGroupRowViewModel>().ToArray());
            var toolRow = Assert.Single(viewModel.Messages.OfType<AgentToolInvocationRowViewModel>());
            Assert.True(toolRow.HasDetails);
            Button GetToolHeader() => Assert.Single(
                    view.GetVisualDescendants().OfType<Button>(),
                    button => button.Classes.Contains("tool-step-header")
                              && ReferenceEquals(button.DataContext, toolRow));
            var toolHeader = GetToolHeader();
            var coordinator = GetTranscriptScrollCoordinator(view);
            var expectedFollowingWrites = GetCoordinatorProgrammaticOffsetWrites(coordinator);
            var extentRevision = 0L;
            var revisionExtentHeight = transcript.Extent.Height;
            object? activeLogicalAnchorKey = null;
            List<TranscriptLogicalAnchorTracePoint>? activeTrace = null;
            List<string>? activeTraceDetails = null;
            List<bool>? activeProgrammaticTrace = null;
            var traceGeneration = 0L;
            var queuedRenderedCaptureGeneration = -1L;
            void QueueRenderedTraceCapture()
            {
                if (activeTrace is null
                    || activeLogicalAnchorKey is null
                    || queuedRenderedCaptureGeneration == traceGeneration)
                {
                    return;
                }

                var queuedGeneration = traceGeneration;
                queuedRenderedCaptureGeneration = queuedGeneration;
                Dispatcher.UIThread.Post(() =>
                {
                    if (queuedRenderedCaptureGeneration == queuedGeneration)
                    {
                        queuedRenderedCaptureGeneration = -1;
                    }
                    if (queuedGeneration != traceGeneration
                        || activeTrace is null
                        || activeLogicalAnchorKey is null)
                    {
                        return;
                    }

                    var point = CaptureLogicalAnchorTracePoint(
                        viewModel,
                        repeater,
                        transcript,
                        coordinator,
                        activeLogicalAnchorKey,
                        extentRevision,
                        isRenderedCheckpoint: true);
                    activeTrace.Add(point);
                    activeTraceDetails?.Add(DescribeLogicalAnchorTracePoint(point));
                }, DispatcherPriority.Background);
            }
            transcript.PropertyChanged += (_, change) =>
            {
                if (change.Property == ScrollViewer.OffsetProperty
                    || change.Property == ScrollViewer.ExtentProperty)
                {
                    var currentExtentHeight = transcript.Extent.Height;
                    if (currentExtentHeight != revisionExtentHeight)
                    {
                        revisionExtentHeight = currentExtentHeight;
                        extentRevision++;
                    }
                }
                if (change.Property == ScrollViewer.OffsetProperty)
                {
                    activeProgrammaticTrace?.Add(GetPrivateBoolean(coordinator, "_isProgrammaticScroll"));
                }
                if ((change.Property == ScrollViewer.OffsetProperty
                     || change.Property == ScrollViewer.ExtentProperty)
                    && activeTrace is not null
                    && activeLogicalAnchorKey is not null)
                {
                    var point = CaptureLogicalAnchorTracePoint(
                        viewModel,
                        repeater,
                        transcript,
                        coordinator,
                        activeLogicalAnchorKey,
                        extentRevision,
                        isRenderedCheckpoint: false);
                    activeTrace.Add(point);
                    activeTraceDetails?.Add(DescribeLogicalAnchorTracePoint(point));
                    QueueRenderedTraceCapture();
                }
            };
            transcript.LayoutUpdated += (_, _) =>
            {
                if (activeTrace is null || activeLogicalAnchorKey is null)
                {
                    return;
                }

                var point = CaptureLogicalAnchorTracePoint(
                    viewModel,
                    repeater,
                    transcript,
                    coordinator,
                    activeLogicalAnchorKey,
                    extentRevision,
                    isRenderedCheckpoint: true);
                activeTrace.Add(point);
                activeTraceDetails?.Add(DescribeLogicalAnchorTracePoint(point));
            };
            var toolAnchorKey = toolRow.AnchorKey;

            for (var iteration = 0; iteration < 4; iteration++)
            {
                var expanding = !toolRow.IsExpanded;
                traceGeneration++;
                activeLogicalAnchorKey = toolAnchorKey;
                var initialPoint = CaptureLogicalAnchorTracePoint(
                    viewModel,
                    repeater,
                    transcript,
                    coordinator,
                    toolAnchorKey,
                    extentRevision,
                    isRenderedCheckpoint: true);
                activeTrace = [initialPoint];
                activeTraceDetails = [DescribeLogicalAnchorTracePoint(initialPoint)];
                activeProgrammaticTrace = [];
                GetToolHeader().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => toolRow.IsExpanded == expanding || toolRow.IsDetailLoadFailed);
                Assert.False(toolRow.IsDetailLoadFailed, toolRow.DetailLoadFailureText);
                await WaitForPendingCoordinatorOperationsAsync(view);
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
                var finalPoint = CaptureLogicalAnchorTracePoint(
                    viewModel,
                    repeater,
                    transcript,
                    coordinator,
                    toolAnchorKey,
                    extentRevision,
                    isRenderedCheckpoint: true);
                activeTrace!.Add(finalPoint);
                activeTraceDetails!.Add(DescribeLogicalAnchorTracePoint(finalPoint));

                Assert.Equal(expanding, toolRow.IsExpanded);
                var followingWrites = GetCoordinatorProgrammaticOffsetWrites(coordinator);
                var collapseClamps = GetCoordinatorMutationCollapseClamps(coordinator);
                if (collapseClamps == 0)
                {
                    AssertLogicalAnchorDoesNotBounce(
                        activeTrace,
                        activeTraceDetails,
                        expanding);
                    Assert.DoesNotContain(true, activeProgrammaticTrace);
                    Assert.Equal(expectedFollowingWrites, followingWrites);
                }
                else
                {
                    Assert.False(expanding);
                    AssertLogicalAnchorReturnsAfterCollapseClamp(activeTrace, activeTraceDetails);
                    Assert.InRange(activeProgrammaticTrace.Count(static value => value), 0, 1);
                    Assert.InRange(followingWrites - expectedFollowingWrites, 0, 1);
                    Assert.Equal(followingWrites - expectedFollowingWrites, collapseClamps);
                }
                expectedFollowingWrites = followingWrites;
                Assert.Equal("Completed", GetCoordinatorMutationStatus(coordinator));
                Assert.Equal("ToolExpansionNativeAnchor", GetCoordinatorMutationMode(coordinator));
                Assert.False(GetTranscriptFollowingLatest(viewModel));
                Assert.True(view.FindControl<Button>("JumpToLatestTranscriptButton")!.IsVisible);
                Assert.False(GetAnchorHostSuspended(view));
                Assert.Equal(0, repeater.MinHeight);
            }
            traceGeneration++;
            activeTrace = null;
            activeLogicalAnchorKey = null;
            activeTraceDetails = null;
            activeProgrammaticTrace = null;

            window.Width = 620;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            window.MouseMove(new Point(410, 240), RawInputModifiers.None);
            window.MouseWheel(
                new Point(410, 240),
                new Vector(0, 2),
                RawInputModifiers.None);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var anchorKey = toolRow.AnchorKey;
            var anchorIndex = viewModel.Messages
                .Select((row, index) => (row.AnchorKey, Index: index))
                .Single(item => Equals(item.AnchorKey, anchorKey))
                .Index;
            Assert.IsAssignableFrom<Control>(repeater.TryGetElement(anchorIndex));
            var currentToolTop = Assert.IsType<Point>(
                GetToolHeader().TranslatePoint(default, transcript)).Y;
            transcript.Offset = new Vector(
                transcript.Offset.X,
                Math.Clamp(
                    transcript.Offset.Y + currentToolTop - 40,
                    0,
                    transcript.Extent.Height - transcript.Viewport.Height));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var anchorTop = Assert.IsType<Point>(
                GetToolHeader().TranslatePoint(default, transcript)).Y;
            Assert.InRange(anchorTop, 0, transcript.Viewport.Height - 1);

            for (var iteration = 0; iteration < 4; iteration++)
            {
                if (iteration == 2)
                {
                    window.Width = 980;
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                    currentToolTop = Assert.IsType<Point>(
                        GetToolHeader().TranslatePoint(default, transcript)).Y;
                    transcript.Offset = new Vector(
                        transcript.Offset.X,
                        Math.Clamp(
                            transcript.Offset.Y + currentToolTop - 40,
                            0,
                            transcript.Extent.Height - transcript.Viewport.Height));
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                    anchorTop = Assert.IsType<Point>(
                        GetToolHeader().TranslatePoint(default, transcript)).Y;
                }

                traceGeneration++;
                activeLogicalAnchorKey = anchorKey;
                var initialPoint = CaptureLogicalAnchorTracePoint(
                    viewModel,
                    repeater,
                    transcript,
                    coordinator,
                    anchorKey,
                    extentRevision,
                    isRenderedCheckpoint: true);
                activeTrace = [initialPoint];
                activeTraceDetails = [DescribeLogicalAnchorTracePoint(initialPoint)];
                activeProgrammaticTrace = [];
                GetToolHeader().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await WaitForPendingCoordinatorOperationsAsync(view);
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
                var finalPoint = CaptureLogicalAnchorTracePoint(
                    viewModel,
                    repeater,
                    transcript,
                    coordinator,
                    anchorKey,
                    extentRevision,
                    isRenderedCheckpoint: true);
                activeTrace!.Add(finalPoint);
                activeTraceDetails!.Add(DescribeLogicalAnchorTracePoint(finalPoint));

                var compactCollapseClamps = GetCoordinatorMutationCollapseClamps(coordinator);
                if (toolRow.IsExpanded || compactCollapseClamps == 0)
                {
                    AssertLogicalAnchorDoesNotBounce(
                        activeTrace,
                        activeTraceDetails,
                        toolRow.IsExpanded);
                }
                else
                {
                    Assert.Equal(1, compactCollapseClamps);
                    AssertLogicalAnchorReturnsAfterCollapseClamp(activeTrace, activeTraceDetails);
                }
                var restoredTop = Assert.IsType<Point>(
                    GetToolHeader().TranslatePoint(default, transcript)).Y;
                Assert.InRange(Math.Abs(restoredTop - anchorTop), 0, 0.1);
                Assert.Equal("Completed", GetCoordinatorMutationStatus(coordinator));
                Assert.False(GetAnchorHostSuspended(view));
                Assert.Equal(0, repeater.MinHeight);
                Assert.IsNotType<AgentActivityTranscriptRowViewModel>(transcript.CurrentAnchor?.DataContext);
                Assert.IsNotType<AgentTranscriptTailSentinelRowViewModel>(transcript.CurrentAnchor?.DataContext);
            }
            traceGeneration++;
            activeTrace = null;
            activeLogicalAnchorKey = null;
            activeTraceDetails = null;
            activeProgrammaticTrace = null;
        }
        finally
        {
            window.Close();
        }
    }

    private static TranscriptLogicalAnchorTracePoint CaptureLogicalAnchorTracePoint(
        AgentChatViewModel viewModel,
        ItemsRepeater repeater,
        ScrollViewer transcript,
        object coordinator,
        object logicalAnchorKey,
        long extentRevision,
        bool isRenderedCheckpoint)
    {
        var anchorIndex = -1;
        for (var index = 0; index < viewModel.TranscriptItems.Count; index++)
        {
            if (Equals(viewModel.TranscriptItems[index].AnchorKey, logicalAnchorKey))
            {
                anchorIndex = index;
                break;
            }
        }

        var anchor = anchorIndex >= 0 ? repeater.TryGetElement(anchorIndex) as Control : null;
        var header = anchor?.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains("tool-step-header")
                                      && ReferenceEquals(button.DataContext, anchor.DataContext));
        var relativeY = (header ?? anchor)?.TranslatePoint(default, transcript)?.Y;
        var realizedAnchorKey = (anchor?.DataContext as AgentTranscriptRowViewModel)?.AnchorKey;
        var details = anchor?.GetVisualDescendants()
            .OfType<TranscriptToolDetailHost>()
            .SingleOrDefault();
        return new TranscriptLogicalAnchorTracePoint(
            transcript.Offset.Y,
            transcript.Extent.Height,
            transcript.Viewport.Height,
            extentRevision,
            logicalAnchorKey,
            realizedAnchorKey,
            CaptureCurrentScrollAnchorKey(coordinator),
            relativeY,
            details?.Bounds.Height,
            details?.MinHeight,
            (anchor?.DataContext as AgentToolInvocationRowViewModel)?.IsExpanded,
            CaptureFollowingRowPositions(
                viewModel.TranscriptItems,
                repeater,
                transcript,
                anchorIndex,
                static row => row.AnchorKey),
            isRenderedCheckpoint,
            GetPrivateBoolean(coordinator, "_isProgrammaticScroll"),
            GetPrivateBoolean(coordinator, "_isRestoringAnchor"));
    }

    private static string DescribeLogicalAnchorTracePoint(TranscriptLogicalAnchorTracePoint point)
        => $"phase={(point.IsRenderedCheckpoint ? "rendered" : "property")}, extentRevision={point.ExtentRevision}, anchor={point.LogicalAnchorKey}, realizedAnchor={point.RealizedAnchorKey}, viewportAnchor={point.ViewportAnchorKey}, relativeY={point.AnchorRelativeY?.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"}, followingRows={string.Join(",", point.FollowingRowPositions.Select(row => $"{row.Key}:{row.Value:F3}"))}, detailsHeight={point.DetailsHeight?.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"}, detailsMinHeight={point.DetailsMinHeight?.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"}, detailsExpanded={point.DetailsExpanded}, offset={point.OffsetY:F3}, extent={point.ExtentHeight:F3}, viewport={point.ViewportHeight:F3}, programmatic={point.IsProgrammatic}, restoring={point.IsRestoring}";

    private static void AssertLogicalAnchorDoesNotBounce(
        IReadOnlyList<TranscriptLogicalAnchorTracePoint> trace,
        IReadOnlyList<string>? details,
        bool expanding)
    {
        Assert.NotEmpty(trace);
        var logicalAnchorKey = trace[0].LogicalAnchorKey;
        var renderedTrace = trace.Where(point => point.IsRenderedCheckpoint).ToArray();
        Assert.NotEmpty(renderedTrace);
        Assert.True(
            renderedTrace.All(point => Equals(point.LogicalAnchorKey, logicalAnchorKey)
                                       && Equals(point.RealizedAnchorKey, logicalAnchorKey)
                                       && point.AnchorRelativeY is not null),
            $"Logical anchor identity was not continuously realized.{Environment.NewLine}{string.Join(Environment.NewLine, details ?? [])}");

        var initialRelativeY = renderedTrace[0].AnchorRelativeY!.Value;
        Assert.True(
            renderedTrace.All(point => Math.Abs(point.AnchorRelativeY!.Value - initialRelativeY) <= 0.1),
            $"Logical anchor moved between rendered extent revisions.{Environment.NewLine}{string.Join(Environment.NewLine, details ?? [])}");

        AssertFollowingRowsMoveMonotonically(
            renderedTrace.Select(point => point.FollowingRowPositions).ToArray(),
            expanding,
            details);
    }

    private static void AssertLogicalAnchorReturnsAfterCollapseClamp(
        IReadOnlyList<TranscriptLogicalAnchorTracePoint> trace,
        IReadOnlyList<string>? details)
    {
        Assert.NotEmpty(trace);
        var logicalAnchorKey = trace[0].LogicalAnchorKey;
        var renderedTrace = trace.Where(point => point.IsRenderedCheckpoint).ToArray();
        Assert.True(renderedTrace.Length >= 2);
        Assert.True(
            renderedTrace.All(point => Equals(point.LogicalAnchorKey, logicalAnchorKey)
                                       && Equals(point.RealizedAnchorKey, logicalAnchorKey)
                                       && point.AnchorRelativeY is not null),
            $"Logical anchor identity was not continuously realized.{Environment.NewLine}{string.Join(Environment.NewLine, details ?? [])}");
        Assert.True(
            Math.Abs(renderedTrace[^1].AnchorRelativeY!.Value - renderedTrace[0].AnchorRelativeY!.Value) <= 0.1,
            $"The terminal collapse clamp did not restore the clicked header.{Environment.NewLine}{string.Join(Environment.NewLine, details ?? [])}");
        foreach (var (key, initialY) in renderedTrace[0].FollowingRowPositions)
        {
            if (renderedTrace[^1].FollowingRowPositions.TryGetValue(key, out var finalY))
            {
                Assert.True(
                    finalY <= initialY + 0.1,
                    $"A row below the collapsed tool finished in the wrong direction ({key}: {initialY:F3} -> {finalY:F3}).{Environment.NewLine}{string.Join(Environment.NewLine, details ?? [])}");
            }
        }
    }

    private sealed record TranscriptLogicalAnchorTracePoint(
        double OffsetY,
        double ExtentHeight,
        double ViewportHeight,
        long ExtentRevision,
        object LogicalAnchorKey,
        object? RealizedAnchorKey,
        object? ViewportAnchorKey,
        double? AnchorRelativeY,
        double? DetailsHeight,
        double? DetailsMinHeight,
        bool? DetailsExpanded,
        IReadOnlyDictionary<object, double> FollowingRowPositions,
        bool IsRenderedCheckpoint,
        bool IsProgrammatic,
        bool IsRestoring);
}
