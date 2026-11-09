extern alias AgentApp;

using System.Collections.Specialized;
using System.Text.Json;
using AgentApp::Sunder.Package.Agent.PackageViews;
using AgentApp::Sunder.Package.Agent.Shared.PackageViews;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Xunit;
using AgentChatSelectionStateService = AgentApp::Sunder.Package.Agent.Services.AgentChatSelectionStateService;
using AgentToolPresentationService = AgentApp::Sunder.Package.Agent.Services.AgentToolPresentationService;
using AppRuntime = AgentApp::Sunder.Package.Agent.Runtime;
using static Sunder.Package.Agent.Presentation.Tests.RenderedChatFixture;
using static Sunder.Package.Agent.Presentation.Tests.RenderedTranscriptGeometry;
using static Sunder.Package.Agent.Presentation.Tests.RenderedViewComposition;
using static Sunder.Package.Agent.Presentation.Tests.RenderedViewWaits;
using static Sunder.Package.Agent.Presentation.Tests.RenderedWorkspaceFixture;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class AgentChatNavigationTests
{
    [AvaloniaFact]
    public async Task AgentChatView_HistoryPreparationPublishesAndPlacesExactlyOnceBeforeReveal()
    {
        using var scope = RegressionTestPackageScope.Create();
        var createdAt = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
        var profile = new AgentProfileRecord(
            "history-profile",
            "History preparation profile",
            null,
            null,
            "test-provider",
            "test-model",
            null,
            null,
            createdAt,
            createdAt);
        var workspace = new AgentWorkspaceRecord(
            "history-workspace",
            "History preparation workspace",
            null,
            createdAt,
            createdAt);
        var session = new AgentSessionRecord(
            Guid.NewGuid(),
            "History preparation session",
            AgentSessionState.Completed,
            createdAt,
            createdAt,
            ProfileId: profile.ProfileId,
            WorkspaceId: workspace.WorkspaceId);
        var turns = Enumerable.Range(0, 80)
            .Select(index => CreateTextTurn(
                session.SessionId,
                createdAt.AddSeconds(index),
                $"History response {index}: {new string('x', 120 + index)}"))
            .ToArray();
        var target = turns[35];
        var targetItem = Assert.Single(target.Items);
        var runtime = new BlockingHistoryNavigationRuntimeClient(
            profile,
            workspace,
            session,
            turns);
        var serviceCollection = CreateHistoryAppServices(scope, runtime);
        await using var provider = serviceCollection.BuildServiceProvider();
        using var view = provider.GetRequiredService<AgentChatView>();
        var source = new Border { Background = Avalonia.Media.Brushes.DimGray };
        var candidateHost = new ContentControl
        {
            Content = view,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        var stage = new Grid { Children = { source, candidateHost } };
        var window = new Window { Width = 900, Height = 500, Content = stage };
        window.Show();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
            viewModel.IsDetailedTranscript = true; // The flat-page fixture owns Detailed's atomic placement contract.
            var selectionState = provider.GetRequiredService<AgentChatSelectionStateService>();
            Assert.Null(await selectionState.GetSelectedProfileIdAsync());
            Assert.Null(await selectionState.GetSelectedWorkspaceIdAsync());
            Assert.Null(await selectionState.GetSelectedSessionIdAsync(workspace.WorkspaceId));
            var transcript = GetTranscriptScrollViewer(view);
            var coordinator = GetTranscriptScrollCoordinator(view);
            var writesBeforePreparation = GetCoordinatorProgrammaticOffsetWrites(coordinator);
            var messageChanges = new List<NotifyCollectionChangedAction>();
            var itemChanges = new List<NotifyCollectionChangedAction>();
            viewModel.Messages.CollectionChanged += (_, change) => messageChanges.Add(change.Action);
            ((INotifyCollectionChanged)viewModel.TranscriptItems).CollectionChanged +=
                (_, change) => itemChanges.Add(change.Action);
            var context = new PackageViewNavigationContext(
                "younics.ai.agent.chat",
                TranscriptAnchorNavigation.ToParameters(new TranscriptNavigationTarget(
                    workspace.WorkspaceId,
                    session.SessionId,
                    target.TurnId,
                    targetItem.ItemId,
                    target.CreatedAtUtc,
                    CallId: null,
                    TranscriptNavigationAnchorKind.Text)));

            var preparation = view.PrepareNavigationAsync(context).AsTask();
            await runtime.AroundLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(preparation.IsCompleted);
            Assert.True(source.IsEffectivelyVisible);
            Assert.Equal(0, candidateHost.Opacity);
            Assert.False(candidateHost.IsHitTestVisible);
            Assert.Empty(viewModel.Messages);
            Assert.Empty(messageChanges);
            Assert.Empty(itemChanges);
            Assert.Equal(1, runtime.ChatSnapshotLoadCount);
            Assert.False(runtime.LastIncludeInitialTranscript);
            Assert.Equal(0, runtime.TranscriptPageLoadCount);
            Assert.Equal(1, runtime.AroundLoadCount);
            Assert.Equal(target.TurnId, runtime.RequestTurnId);
            Assert.Equal(targetItem.ItemId, runtime.RequestItemId);

            runtime.ReleaseAroundLoad.TrySetResult();
            Assert.True(await preparation.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal([NotifyCollectionChangedAction.Reset], messageChanges);
            Assert.Equal([NotifyCollectionChangedAction.Reset], itemChanges);
            Assert.Equal(0, runtime.TranscriptPageLoadCount);
            Assert.Equal(1, runtime.AroundLoadCount);
            Assert.Equal(
                writesBeforePreparation + 1,
                GetCoordinatorProgrammaticOffsetWrites(coordinator));
            var targetRow = Assert.Single(
                viewModel.Messages,
                row => string.Equals(
                    row.AnchorKey.ToString(),
                    $"text:{target.TurnId:N}",
                    StringComparison.Ordinal));
            Assert.False(targetRow.IsNavigationTargetHighlighted);
            Assert.False(targetRow.IsNavigationTargetFading);
            Assert.Null(GetPrivateField(viewModel, "_navigationHighlightCancellation"));
            Assert.Null(await selectionState.GetSelectedProfileIdAsync());
            Assert.Null(await selectionState.GetSelectedWorkspaceIdAsync());
            Assert.Null(await selectionState.GetSelectedSessionIdAsync(workspace.WorkspaceId));
            var repeater = Assert.IsType<ItemsRepeater>(
                view.FindControl<ItemsRepeater>("TranscriptItemsControl"));
            var targetIndex = viewModel.Messages.IndexOf(targetRow);
            var targetVisual = Assert.IsAssignableFrom<Control>(repeater.TryGetElement(targetIndex));
            var targetTop = Assert.IsType<Point>(
                targetVisual.TranslatePoint(default, transcript)).Y;
            Assert.InRange(targetTop, 27, 29);
            var settledOffset = transcript.Offset;
            var settledExtent = transcript.Extent;
            var settledBounds = targetVisual.Bounds;
            var settledWrites = GetCoordinatorProgrammaticOffsetWrites(coordinator);

            candidateHost.Opacity = 1;
            candidateHost.IsHitTestVisible = true;
            await view.OnNavigationPresentedAsync(context);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

            Assert.NotNull(GetPrivateField(viewModel, "_navigationHighlightCancellation"));
            Assert.True(targetRow.IsNavigationTargetHighlighted);
            Assert.False(targetRow.IsNavigationTargetFading);
            Assert.Equal(profile.ProfileId, await selectionState.GetSelectedProfileIdAsync());
            Assert.Equal(workspace.WorkspaceId, await selectionState.GetSelectedWorkspaceIdAsync());
            Assert.Equal(
                session.SessionId,
                await selectionState.GetSelectedSessionIdAsync(workspace.WorkspaceId));
            Assert.Equal(settledWrites, GetCoordinatorProgrammaticOffsetWrites(coordinator));
            Assert.Equal(settledOffset, transcript.Offset);
            Assert.Equal(settledExtent, transcript.Extent);
            Assert.Equal(settledBounds, targetVisual.Bounds);
            Assert.Equal(
                targetTop,
                Assert.IsType<Point>(targetVisual.TranslatePoint(default, transcript)).Y,
                precision: 3);

            var loadedTarget = turns[36];
            var loadedTargetItem = Assert.Single(loadedTarget.Items);
            var loadedContext = new PackageViewNavigationContext(
                "younics.ai.agent.chat",
                TranscriptAnchorNavigation.ToParameters(new TranscriptNavigationTarget(
                    workspace.WorkspaceId,
                    session.SessionId,
                    loadedTarget.TurnId,
                    loadedTargetItem.ItemId,
                    loadedTarget.CreatedAtUtc,
                    CallId: null,
                    TranscriptNavigationAnchorKind.Text)));
            messageChanges.Clear();
            itemChanges.Clear();
            var opacityChanges = 0;
            transcript.PropertyChanged += OnTranscriptPropertyChanged;
            var writesBeforeLoadedPlacement = GetCoordinatorProgrammaticOffsetWrites(coordinator);

            Assert.True(await view.PrepareNavigationAsync(loadedContext));
            await GetPendingCoordinatorOperations(view);
            transcript.PropertyChanged -= OnTranscriptPropertyChanged;

            Assert.Equal(0, opacityChanges);
            Assert.Empty(messageChanges);
            Assert.Empty(itemChanges);
            Assert.Equal(1, runtime.ChatSnapshotLoadCount);
            Assert.Equal(1, runtime.AroundLoadCount);
            Assert.Equal(0, runtime.TranscriptPageLoadCount);
            Assert.Equal(
                writesBeforeLoadedPlacement + 1,
                GetCoordinatorProgrammaticOffsetWrites(coordinator));
            var loadedRow = Assert.Single(
                viewModel.Messages,
                row => string.Equals(
                    row.AnchorKey.ToString(),
                    $"text:{loadedTarget.TurnId:N}",
                    StringComparison.Ordinal));
            Assert.False(targetRow.IsNavigationTargetHighlighted);
            Assert.False(loadedRow.IsNavigationTargetHighlighted);
            Assert.Null(GetPrivateField(viewModel, "_navigationHighlightCancellation"));
            var loadedVisual = Assert.IsAssignableFrom<Control>(
                repeater.TryGetElement(viewModel.Messages.IndexOf(loadedRow)));
            Assert.InRange(
                Assert.IsType<Point>(loadedVisual.TranslatePoint(default, transcript)).Y,
                27,
                29);
            await view.OnNavigationPresentedAsync(loadedContext);
            Assert.False(targetRow.IsNavigationTargetHighlighted);
            Assert.True(loadedRow.IsNavigationTargetHighlighted);

            void OnTranscriptPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
            {
                if (change.Property == Visual.OpacityProperty)
                {
                    opacityChanges++;
                }
            }
        }
        finally
        {
            runtime.ReleaseAroundLoad.TrySetResult();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AgentChatView_RapidHistoryPreparationsCommitOnlyTheLatestTarget()
    {
        using var scope = RegressionTestPackageScope.Create();
        var createdAt = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
        var profile = new AgentProfileRecord(
            "rapid-history-profile",
            "Rapid history profile",
            null,
            null,
            "test-provider",
            "test-model",
            null,
            null,
            createdAt,
            createdAt);
        var workspace = new AgentWorkspaceRecord(
            "rapid-history-workspace",
            "Rapid history workspace",
            null,
            createdAt,
            createdAt);
        var session = new AgentSessionRecord(
            Guid.NewGuid(),
            "Rapid history session",
            AgentSessionState.Completed,
            createdAt,
            createdAt,
            ProfileId: profile.ProfileId,
            WorkspaceId: workspace.WorkspaceId);
        var turns = Enumerable.Range(0, 90)
            .Select(index => CreateTextTurn(
                session.SessionId,
                createdAt.AddSeconds(index),
                $"Rapid history response {index}: {new string('x', 140)}"))
            .ToArray();
        var firstTarget = turns[20];
        var latestTarget = turns[70];
        var runtime = new BlockingHistoryNavigationRuntimeClient(
            profile,
            workspace,
            session,
            turns);
        await using var provider = CreateHistoryAppServices(scope, runtime).BuildServiceProvider();
        using var view = provider.GetRequiredService<AgentChatView>();
        var source = new Border { Background = Avalonia.Media.Brushes.DimGray };
        var candidateHost = new ContentControl
        {
            Content = view,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        var window = new Window
        {
            Width = 900,
            Height = 500,
            Content = new Grid { Children = { source, candidateHost } },
        };
        window.Show();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
            viewModel.IsDetailedTranscript = true;
            var transcript = GetTranscriptScrollViewer(view);
            var coordinator = GetTranscriptScrollCoordinator(view);
            var writesBeforeNavigation = GetCoordinatorProgrammaticOffsetWrites(coordinator);
            var messageChanges = new List<NotifyCollectionChangedAction>();
            viewModel.Messages.CollectionChanged += (_, change) => messageChanges.Add(change.Action);

            var first = view.PrepareNavigationAsync(CreateContext(firstTarget)).AsTask();
            await runtime.AroundLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var latestContext = CreateContext(latestTarget);
            var latest = view.PrepareNavigationAsync(latestContext).AsTask();
            await WaitUntilAsync(() => runtime.AroundLoadCount == 2);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => first.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(latest.IsCompleted);
            Assert.True(source.IsEffectivelyVisible);
            Assert.Equal(0, candidateHost.Opacity);
            Assert.Empty(viewModel.Messages);
            Assert.Empty(messageChanges);
            Assert.Equal(0, runtime.TranscriptPageLoadCount);

            runtime.ReleaseAroundLoad.TrySetResult();
            Assert.True(await latest.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal(2, runtime.ChatSnapshotLoadCount);
            Assert.Equal(2, runtime.AroundLoadCount);
            Assert.Equal(latestTarget.TurnId, runtime.RequestTurnId);
            Assert.Equal([NotifyCollectionChangedAction.Reset], messageChanges);
            Assert.Equal(
                writesBeforeNavigation + 1,
                GetCoordinatorProgrammaticOffsetWrites(coordinator));
            Assert.DoesNotContain(
                viewModel.Messages,
                row => string.Equals(
                    row.AnchorKey.ToString(),
                    $"text:{firstTarget.TurnId:N}",
                    StringComparison.Ordinal));
            var latestRow = Assert.Single(
                viewModel.Messages,
                row => string.Equals(
                    row.AnchorKey.ToString(),
                    $"text:{latestTarget.TurnId:N}",
                    StringComparison.Ordinal));
            Assert.False(latestRow.IsNavigationTargetHighlighted);
            var repeater = Assert.IsType<ItemsRepeater>(
                view.FindControl<ItemsRepeater>("TranscriptItemsControl"));
            var latestVisual = Assert.IsAssignableFrom<Control>(
                repeater.TryGetElement(viewModel.Messages.IndexOf(latestRow)));
            Assert.InRange(
                Assert.IsType<Point>(latestVisual.TranslatePoint(default, transcript)).Y,
                27,
                29);
            var settledWrites = GetCoordinatorProgrammaticOffsetWrites(coordinator);
            var settledOffset = transcript.Offset;
            for (var pass = 0; pass < 3; pass++)
            {
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            }
            Assert.Equal(settledWrites, GetCoordinatorProgrammaticOffsetWrites(coordinator));
            Assert.Equal(settledOffset, transcript.Offset);

            candidateHost.Opacity = 1;
            candidateHost.IsHitTestVisible = true;
            await view.OnNavigationPresentedAsync(latestContext);
            Assert.True(latestRow.IsNavigationTargetHighlighted);

            PackageViewNavigationContext CreateContext(AgentTurnRecord turn)
            {
                var item = Assert.Single(turn.Items);
                return new PackageViewNavigationContext(
                    "younics.ai.agent.chat",
                    TranscriptAnchorNavigation.ToParameters(new TranscriptNavigationTarget(
                        workspace.WorkspaceId,
                        session.SessionId,
                        turn.TurnId,
                        item.ItemId,
                        turn.CreatedAtUtc,
                        CallId: null,
                        TranscriptNavigationAnchorKind.Text)));
            }
        }
        finally
        {
            runtime.ReleaseAroundLoad.TrySetResult();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AgentChatView_NavigationAwaitsAppliedAndPlacedInitialTranscript()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Navigation profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Navigation workspace");
        var session = services.SessionService.CreateSession(
            "Navigation session",
            workspaceId: workspace.WorkspaceId);
        services.SessionService.AppendTextTurn(
            session.SessionId,
            AgentMessageRole.Assistant,
            "Initial response.");
        var selectionState = new AgentChatSelectionStateService(scope.Context);
        var attachmentService = new AgentAttachmentService(scope.Context);
        using var view = RegressionTestAppViews.ChatView(
            profileService,
            services.WorkspaceService,
            services.SessionService,
            NoOpPermissionGateway.Instance,
            NoOpRunGateway.Instance,
            selectionState,
            new AgentToolPresentationService(),
            new AgentExecutionTargetWarmupService(
                services.WorkspaceService,
                services.TargetService),
            null!,
            attachmentService,
            NullPackageNotificationService.Instance);
        var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        Assert.Empty(viewModel.Profiles);
        var transcriptNotificationsOnUi = new List<bool>();
        viewModel.Messages.CollectionChanged += (_, _) =>
            transcriptNotificationsOnUi.Add(Dispatcher.UIThread.CheckAccess());

        await Task.Run(async () => await view.OnNavigatedToAsync(new PackageViewNavigationContext(
            "younics.ai.agent.chat",
            new Dictionary<string, string?>())));

        Assert.Single(viewModel.Messages);
        Assert.Equal("Initial response.", Assert.IsType<AgentTextTranscriptRowViewModel>(
            viewModel.Messages[0]).Content);
        Assert.NotEmpty(transcriptNotificationsOnUi);
        Assert.All(transcriptNotificationsOnUi, Assert.True);
        var transcript = GetTranscriptScrollViewer(view);
        Assert.Equal(1d, transcript.Opacity);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AgentChatView_WarmNavigationLoadsSnapshotExactlyOnceWithoutResetOrSettle()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        var profile = await profileService.CreateProfileAsync("Warm profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Warm workspace");
        var session = services.SessionService.CreateSession(
            "Warm session",
            profileId: profile.ProfileId,
            workspaceId: workspace.WorkspaceId);
        services.SessionService.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, "Warm response.");
        var sessionGateway = new CountingSessionGateway(services.SessionService);
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
        var host = new Border { Child = view };
        var window = new Window { Width = 900, Height = 700, Content = host };
        window.Show();
        var context = new PackageViewNavigationContext(
            "younics.ai.agent.chat",
            new Dictionary<string, string?>());
        await Task.Run(async () => await view.OnNavigatedToAsync(context));
        var transcript = GetTranscriptScrollViewer(view);
        var settledOperation = GetSettledScrollOperation(view);
        var transcriptBehavior = GetTranscriptBehavior(view);
        var opacityChanges = 0;
        var offsetChanges = 0;
        transcript.PropertyChanged += (_, change) =>
        {
            if (change.Property.Name == "Opacity")
            {
                opacityChanges++;
            }
            if (change.Property == ScrollViewer.OffsetProperty)
            {
                offsetChanges++;
            }
        };

        host.IsVisible = false;
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Assert.False(GetPrivateBoolean(transcriptBehavior, "_changedBeforeScrollReady"));
        host.IsVisible = true;
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        await Task.Run(async () => await view.OnNavigatedToAsync(context));

        Assert.Equal(1, sessionGateway.RecentTranscriptReadCount);
        Assert.Equal(0, opacityChanges);
        Assert.Equal(0, offsetChanges);
        Assert.False(GetPrivateBoolean(transcriptBehavior, "_changedBeforeScrollReady"));
        Assert.Same(settledOperation, GetSettledScrollOperation(view));
        Assert.Equal(1d, transcript.Opacity);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AgentChatView_NavigationCancellationStopsInitialLayoutPlacement()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Cancellation profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Cancellation workspace");
        var session = services.SessionService.CreateSession(
            "Cancellation session",
            workspaceId: workspace.WorkspaceId);
        for (var index = 0; index < 60; index++)
        {
            services.SessionService.AppendTextTurn(
                session.SessionId,
                AgentMessageRole.Assistant,
                $"Response {index}: {new string('x', 200)}");
        }

        using var view = RegressionTestAppViews.ChatView(
            profileService,
            services.WorkspaceService,
            services.SessionService,
            NoOpPermissionGateway.Instance,
            NoOpRunGateway.Instance,
            new AgentChatSelectionStateService(scope.Context),
            new AgentToolPresentationService(),
            new AgentExecutionTargetWarmupService(services.WorkspaceService, services.TargetService),
            null!,
            new AgentAttachmentService(scope.Context),
            NullPackageNotificationService.Instance);
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        var transcript = GetTranscriptScrollViewer(view);
        var coordinator = GetTranscriptScrollCoordinator(view);
        using var cancellation = new CancellationTokenSource();
        var placementStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationTriggered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifecycleTrace = new List<string>();
        var layoutUpdates = 0;
        EventHandler? layoutHandler = null;
        layoutHandler = (_, _) =>
        {
            layoutUpdates++;
            RecordDiagnostic(
                lifecycleTrace,
                DescribeAgentTranscriptState(view, transcript, coordinator, $"layout-{layoutUpdates}"));
            if (cancellationTriggered.TrySetResult())
            {
                cancellation.Cancel();
            }
        };
        transcript.PropertyChanged += (_, change) =>
        {
            if (change.Property == ScrollViewer.OffsetProperty)
            {
                RecordDiagnostic(
                    lifecycleTrace,
                    DescribeAgentTranscriptState(view, transcript, coordinator, "offset-changed"));
            }
            if (change.Property.Name == "Opacity" && transcript.Opacity == 0)
            {
                transcript.LayoutUpdated += layoutHandler;
                placementStarted.TrySetResult();
            }
        };
        var context = new PackageViewNavigationContext(
            "younics.ai.agent.chat",
            new Dictionary<string, string?>());

        var navigation = Task.Run(async () => await view.OnNavigatedToAsync(context, cancellation.Token));
        try
        {
            await placementStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                transcript.InvalidateMeasure();
                window.UpdateLayout();
            }, DispatcherPriority.Render);
            await cancellationTriggered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => navigation);
            await GetSettledScrollOperation(view).WaitAsync(TimeSpan.FromSeconds(3));
            await GetPendingCoordinatorOperations(view).WaitAsync(TimeSpan.FromSeconds(3));
            RecordDiagnostic(
                lifecycleTrace,
                DescribeAgentTranscriptState(view, transcript, coordinator, "cancellation-observed"));
            var writesAfterCancellation = GetCoordinatorProgrammaticOffsetWrites(coordinator);
            var authorityAfterCancellation = GetCoordinatorAuthorityRevision(coordinator);
            var presentationAfterCancellation = GetCoordinatorPresentationRevision(coordinator);
            var placementAfterCancellation = GetPrivateField(GetTranscriptBehavior(view), "_initialPlacementVersion");
            await WaitForTranscriptGeometrySettledAsync(window, transcript, coordinator);
            RecordDiagnostic(
                lifecycleTrace,
                DescribeAgentTranscriptState(view, transcript, coordinator, "geometry-established"));
            Assert.True(writesAfterCancellation == GetCoordinatorProgrammaticOffsetWrites(coordinator),
                $"A coordinator write occurred while geometry settled after cancellation.{Environment.NewLine}{string.Join(Environment.NewLine, lifecycleTrace)}");
            Assert.True(GetCoordinatorAuthorityRevision(coordinator) - authorityAfterCancellation
                        == GetCoordinatorPresentationRevision(coordinator) - presentationAfterCancellation,
                $"Viewport authority changed without a presentation transition after cancellation.{Environment.NewLine}{string.Join(Environment.NewLine, lifecycleTrace)}");
            Assert.Equal(placementAfterCancellation, GetPrivateField(GetTranscriptBehavior(view), "_initialPlacementVersion"));
            Assert.False(GetPrivateBoolean(GetTranscriptBehavior(view), "_initialPlacementPending"));
            var settledAuthority = GetCoordinatorAuthorityRevision(coordinator);
            var updatesAfterCancellation = layoutUpdates;
            var offsetAfterCancellation = transcript.Offset.Y;
            for (var pass = 0; pass < 3; pass++)
            {
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }

            Assert.True(layoutUpdates > updatesAfterCancellation);
            Assert.True(
                Math.Round(offsetAfterCancellation, 3) == Math.Round(transcript.Offset.Y, 3),
                $"Offset moved after navigation cancellation: {offsetAfterCancellation:F3} -> {transcript.Offset.Y:F3}.{Environment.NewLine}{string.Join(Environment.NewLine, lifecycleTrace)}");
            Assert.True(
                writesAfterCancellation == GetCoordinatorProgrammaticOffsetWrites(coordinator),
                $"A coordinator write occurred after navigation cancellation.{Environment.NewLine}{string.Join(Environment.NewLine, lifecycleTrace)}");
            Assert.True(
                settledAuthority == GetCoordinatorAuthorityRevision(coordinator),
                $"Viewport authority changed after navigation cancellation.{Environment.NewLine}{string.Join(Environment.NewLine, lifecycleTrace)}");
            Assert.False(GetPrivateBoolean(coordinator, "_bottomPlacementLockActive"));
            Assert.False(GetAnchorHostSuspended(view));
            Assert.True(
                GetInitialPlacementCancellationCallbacks(view) > 0,
                $"Cancellation never claimed initial-placement authority.{Environment.NewLine}{string.Join(Environment.NewLine, lifecycleTrace)}");
            Assert.Equal(1d, transcript.Opacity);
            transcript.LayoutUpdated -= layoutHandler;
            await Task.Run(async () => await view.OnNavigatedToAsync(context));
            await WaitForPendingCoordinatorOperationsAsync(view);
            Assert.Equal(1d, transcript.Opacity);
        }
        finally
        {
            cancellation.Cancel();
            transcript.LayoutUpdated -= layoutHandler;
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AgentChatView_NavigationPresentsRetryableStartupErrorWithoutHostFault()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        var workspaceGateway = new FailOnceWorkspaceGateway(services.WorkspaceService);
        using var view = RegressionTestAppViews.ChatView(
            profileService,
            workspaceGateway,
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
        var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        var context = new PackageViewNavigationContext(
            "younics.ai.agent.chat",
            new Dictionary<string, string?>());
        var failureNotificationsOnUi = new List<bool>();
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName is nameof(AgentChatViewModel.SetupTitle)
                or nameof(AgentChatViewModel.SetupDescription)
                or nameof(AgentChatViewModel.StatusText))
            {
                failureNotificationsOnUi.Add(Dispatcher.UIThread.CheckAccess());
            }
        };

        var firstNavigation = Task.Run(async () => await view.PrepareNavigationAsync(context));
        await workspaceGateway.FirstInitializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(firstNavigation.IsCompleted);
        workspaceGateway.FailFirstInitialization.TrySetResult();
        Assert.False(await firstNavigation);

        Assert.Equal("Unable to load Agent Chat", viewModel.SetupTitle);
        Assert.Contains("return to retry", viewModel.SetupDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unable to load Agent Chat", viewModel.StatusText, StringComparison.Ordinal);
        Assert.NotEmpty(failureNotificationsOnUi);
        Assert.All(failureNotificationsOnUi, Assert.True);

        Assert.True(await Task.Run(async () => await view.PrepareNavigationAsync(context)));
        await view.OnNavigationPresentedAsync(context);

        Assert.Equal(2, workspaceGateway.InitializeCount);
        Assert.Equal("Create an agent before chatting", viewModel.SetupTitle);
        window.Close();
    }

    private sealed class BlockingHistoryNavigationRuntimeClient(
        AgentProfileRecord profile,
        AgentWorkspaceRecord workspace,
        AgentSessionRecord session,
        IReadOnlyList<AgentTurnRecord> turns) : OwnerServiceStub
    {
        private int _aroundLoadCount;
        private int _chatSnapshotLoadCount;
        private int _transcriptPageLoadCount;

        public int AroundLoadCount => Volatile.Read(ref _aroundLoadCount);

        public int ChatSnapshotLoadCount => Volatile.Read(ref _chatSnapshotLoadCount);

        public int TranscriptPageLoadCount => Volatile.Read(ref _transcriptPageLoadCount);

        public bool LastIncludeInitialTranscript { get; private set; }

        public Guid? RequestTurnId { get; private set; }

        public Guid? RequestItemId { get; private set; }

        public TaskCompletionSource AroundLoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseAroundLoad { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request is AppRuntime.AgentChatSnapshotRequest)
            {
                return new ValueTask<TResponse>(CreateChatSnapshot<TRequest, TResponse>(request));
            }
            if (request is AppRuntime.AgentTranscriptAroundTurnRequest)
            {
                return new ValueTask<TResponse>(
                    LoadAroundTurnAsync<TRequest, TResponse>(request, cancellationToken));
            }
            if (request is AppRuntime.AgentTranscriptPageRequest)
            {
                return new ValueTask<TResponse>(CreateTranscriptPage<TResponse>());
            }
            if (request is AppRuntime.AgentWorkspaceCommand)
            {
                return new ValueTask<TResponse>(CreateResponse<TResponse>(new
                {
                    Revision = 1L,
                    Workspace = (object?)null,
                    Warmup = AgentExecutionTargetWarmupResult.Skipped(
                        "No execution target is required for this test."),
                }));
            }
            if (request is AppRuntime.AgentDashboardRequest)
            {
                return new ValueTask<TResponse>(CreateResponse<TResponse>(new
                {
                    Revision = 1L,
                    Profiles = new[] { profile },
                    Workspaces = new[] { workspace },
                    WorkspaceBindings = Array.Empty<AgentWorkspaceBindingRecord>(),
                }));
            }
            throw new NotSupportedException(typeof(TRequest).Name);
        }

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
            TRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        private TResponse CreateChatSnapshot<TRequest, TResponse>(TRequest request)
            where TRequest : class
            where TResponse : class
        {
            Interlocked.Increment(ref _chatSnapshotLoadCount);
            LastIncludeInitialTranscript = Assert.IsType<bool>(request.GetType()
                .GetProperty("IncludeInitialTranscript")!
                .GetValue(request));
            var sessionSnapshot = new
            {
                Session = session,
                Checkpoint = (object?)null,
            };
            return CreateResponse<TResponse>(new
            {
                Revision = 1L,
                Profiles = new[] { profile },
                Workspaces = new[] { workspace },
                WorkspaceBindings = Array.Empty<AgentWorkspaceBindingRecord>(),
                SelectedProfile = profile,
                SelectedWorkspace = workspace,
                SelectedSession = sessionSnapshot,
                WorkspaceSessions = new[] { sessionSnapshot },
                InitialTranscript = new
                {
                    Revision = 1L,
                    Turns = Array.Empty<AgentTurnRecord>(),
                    HasMore = false,
                    Continuation = (object?)null,
                },
                Permissions = new
                {
                    Revision = 1L,
                    SessionState = (object?)null,
                    PendingRequests = Array.Empty<object>(),
                },
                RuntimeInstanceId = "history-navigation-test",
            });
        }

        private async Task<TResponse> LoadAroundTurnAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken)
            where TRequest : class
            where TResponse : class
        {
            var requestTurnId = Assert.IsType<Guid>(request.GetType()
                .GetProperty("TurnId")!
                .GetValue(request));
            RequestTurnId = requestTurnId;
            RequestItemId = request.GetType().GetProperty("ItemId")!.GetValue(request) is Guid itemId
                ? itemId
                : null;
            Interlocked.Increment(ref _aroundLoadCount);
            AroundLoadStarted.TrySetResult();
            await ReleaseAroundLoad.Task.WaitAsync(cancellationToken);
            var anchorIndex = turns
                .Select((turn, index) => (turn.TurnId, Index: index))
                .Single(item => item.TurnId == requestTurnId)
                .Index;
            var startIndex = Math.Max(0, anchorIndex - 20);
            var pageTurns = turns.Skip(startIndex).Take(41).ToArray();
            return CreateResponse<TResponse>(new
            {
                Revision = 1L,
                Turns = pageTurns,
                HasOlder = startIndex > 0,
                HasNewer = startIndex + pageTurns.Length < turns.Count,
                AnchorTurnId = requestTurnId,
            });
        }

        private TResponse CreateTranscriptPage<TResponse>() where TResponse : class
        {
            Interlocked.Increment(ref _transcriptPageLoadCount);
            return CreateResponse<TResponse>(new
            {
                Revision = 1L,
                Turns = Array.Empty<AgentTurnRecord>(),
                HasMore = false,
                Continuation = (object?)null,
            });
        }

        private static TResponse CreateResponse<TResponse>(object value) where TResponse : class
            => JsonSerializer.Deserialize<TResponse>(
                JsonSerializer.Serialize(value),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException(
                   $"Could not create Runtime response '{typeof(TResponse).Name}'.");
    }

    private static string DescribeAgentTranscriptState(
        AgentChatView view,
        ScrollViewer transcript,
        object coordinator,
        string stage)
        => DescribeTranscriptState(
            stage,
            transcript,
            coordinator,
            GetTranscriptBehavior(view),
            GetPrivateField(view, "_navigationGeneration"));

    private static int GetInitialPlacementCancellationCallbacks(AgentChatView view)
    {
        var behavior = GetTranscriptBehavior(view);
        var snapshot = behavior.GetType()
            .GetProperty(
                "DiagnosticSnapshot",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(behavior)!;
        return Assert.IsType<int>(snapshot.GetType()
            .GetProperty("CancellationCallbacks")!
            .GetValue(snapshot));
    }
}
