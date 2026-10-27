extern alias AgentApp;

using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AgentApp::Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Runtime;
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
}
