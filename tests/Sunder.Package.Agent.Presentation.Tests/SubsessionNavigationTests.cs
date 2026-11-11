extern alias AgentApp;

using System.Collections.Specialized;
using AgentApp::Sunder.Package.Agent.Shared.PackageViews;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Xunit;
using AppModels = AgentApp::Sunder.Package.Agent.Contracts.Models;
using static Sunder.Package.Agent.Presentation.Tests.RenderedExtensionFixture;
using static Sunder.Package.Agent.Presentation.Tests.RenderedSubsessionFixture;
using static Sunder.Package.Agent.Presentation.Tests.RenderedChatFixture;
using static Sunder.Package.Agent.Presentation.Tests.RenderedTranscriptGeometry;
using static Sunder.Package.Agent.Presentation.Tests.RenderedViewComposition;
using static Sunder.Package.Agent.Presentation.Tests.RenderedViewWaits;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class SubsessionNavigationTests
{
    [AvaloniaFact]
    public async Task SubsessionsView_AroundTurnPreparationPublishesAndPlacesParallelToolsExactlyOnce()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        var profile = await profileService.CreateProfileAsync("Subsession navigation profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Subsession navigation workspace");
        var parent = services.SessionService.CreateSession(
            "Subsession navigation parent",
            workspaceId: workspace.WorkspaceId);
        var child = services.SessionService.CreateSession(
            "Subsession navigation child",
            parentSessionId: parent.SessionId,
            rootSessionId: parent.SessionId,
            profileId: profile.ProfileId,
            agentKind: "subagent");
        var parallelTurn = CreateParallelToolTurn(child.SessionId, itemCount: 90);
        var targetItem = parallelTurn.Items[10];
        var runtime = new TestSubsessionRuntimeCatalog(
            [parent, child],
            [profile],
            [parallelTurn]);
        using var catalog = new RegressionTestExtensionCatalog();
        catalog.AddRuntimeCatalog(new SubsessionRuntimeCatalogServiceProxy(runtime));
        using var runtimeAdapter = new CatalogBackedSubsessionRuntimeAdapter(scope.Context, catalog);
        var viewModel = runtimeAdapter.CreateViewModel();
        // This legacy flat-page fixture owns Detailed's single-publication contract.
        viewModel.IsDetailedTranscript = true;
        using var view = new SubsessionsView(viewModel);
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
            var coordinator = GetTranscriptScrollCoordinator(view);
            var writesBeforePreparation = GetCoordinatorProgrammaticOffsetWrites(coordinator);
            var messageChanges = new List<NotifyCollectionChangedAction>();
            var itemChanges = new List<NotifyCollectionChangedAction>();
            var messageChangeStacks = new List<string>();
            viewModel.Messages.CollectionChanged += (_, change) =>
            {
                messageChanges.Add(change.Action);
                messageChangeStacks.Add($"{change.Action}, count={viewModel.Messages.Count}: {Environment.StackTrace}");
            };
            ((INotifyCollectionChanged)viewModel.TranscriptItems).CollectionChanged +=
                (_, change) => itemChanges.Add(change.Action);
            var context = CreateSubsessionNavigation(
                child.SessionId,
                parallelTurn.TurnId,
                targetItem.ItemId,
                "Activity",
                parallelTurn.CreatedAtUtc,
                targetItem.CallId);

            Assert.True(await view.PrepareNavigationAsync(context),
                $"status={viewModel.StatusText}; rpc failure={catalog.LastInvocationFailure}; "
                + $"header calls={string.Join(" | ", runtime.HeaderCalls)}");
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await GetPendingCoordinatorOperations(view);

            var targetAnchorKey = TranscriptRowAnchorKey.Tool(AgentRoleBoundary.As<AppModels.AgentTurnRecord>(parallelTurn),
                AgentRoleBoundary.As<AppModels.AgentTurnItemRecord>(targetItem)).ToString();
            Assert.True(source.IsEffectivelyVisible);
            Assert.Equal(0, candidateHost.Opacity);
            Assert.False(candidateHost.IsHitTestVisible);
            Assert.True(runtime.RecentTranscriptReadCount == 0,
                $"unexpected header calls: {string.Join(" | ", runtime.HeaderCalls)}");
            Assert.True(messageChanges.SequenceEqual([NotifyCollectionChangedAction.Reset]), string.Join("\n", messageChangeStacks));
            Assert.Equal([NotifyCollectionChangedAction.Reset], itemChanges);
            Assert.Equal(
                writesBeforePreparation + 1,
                GetCoordinatorProgrammaticOffsetWrites(coordinator));
            Assert.Equal(parallelTurn.Items.Count, viewModel.Messages.Count);
            Assert.All(parallelTurn.Items, item => Assert.Contains(
                viewModel.Messages,
                row => string.Equals(
                    row.AnchorKey.ToString(),
                    TranscriptRowAnchorKey.Tool(AgentRoleBoundary.As<AppModels.AgentTurnRecord>(parallelTurn),
                        AgentRoleBoundary.As<AppModels.AgentTurnItemRecord>(item)).ToString(),
                    StringComparison.Ordinal)));
            var targetIndex = viewModel.Messages
                .Select((row, index) => (row, index))
                .Single(item => string.Equals(
                    item.row.AnchorKey.ToString(),
                    targetAnchorKey,
                    StringComparison.Ordinal))
                .index;
            Assert.False(viewModel.Messages[targetIndex].IsNavigationTargetHighlighted);
            Assert.False(viewModel.Messages[targetIndex].IsNavigationTargetFading);
            Assert.Null(GetPrivateField(viewModel, "_navigationHighlightCancellation"));
            var repeater = Assert.IsType<ItemsRepeater>(
                view.FindControl<ItemsRepeater>("TranscriptItemsControl"));
            var targetPresentationIndex = viewModel.TranscriptItems
                .Select((row, index) => (row, index))
                .Single(item => string.Equals(item.row.AnchorKey.ToString(), targetAnchorKey, StringComparison.Ordinal)).index;
            var targetPresenter = Assert.IsAssignableFrom<Control>(
                repeater.TryGetElement(targetPresentationIndex));
            Assert.Equal(nameof(TranscriptRowPresenter), targetPresenter.GetType().Name);
            Assert.Same(viewModel.Messages[targetIndex], targetPresenter.DataContext);
            Assert.All(viewModel.Messages.OfType<SubsessionToolInvocationRowViewModel>(), call => Assert.False(call.HasMaterializedDetails));
            var transcript = Assert.IsType<ScrollViewer>(
                view.FindControl<ScrollViewer>("TranscriptScrollViewer"));
            var targetTop = Assert.IsType<Point>(
                targetPresenter.TranslatePoint(default, transcript)).Y;
            Assert.InRange(targetTop, 27, 29);
            Assert.False(GetTranscriptFollowingLatest(viewModel));

            var realized = Enumerable.Range(0, viewModel.TranscriptItems.Count)
                .Select(repeater.TryGetElement)
                .OfType<Control>()
                .Where(control => control.GetType().Name == nameof(TranscriptRowPresenter))
                .ToArray();
            Assert.InRange(realized.Length, 1, 24);
            Assert.Contains(targetPresenter, realized);
            Assert.All(realized, presenter => Assert.True(Assert.IsType<bool>(presenter.GetType()
                .GetProperty(nameof(TranscriptRowPresenter.IsRepeaterHosted))!
                .GetValue(presenter))));
            var settledOffset = transcript.Offset;
            var settledExtent = transcript.Extent;
            var settledBounds = targetPresenter.Bounds;
            var settledWrites = GetCoordinatorProgrammaticOffsetWrites(coordinator);

            candidateHost.Opacity = 1;
            candidateHost.IsHitTestVisible = true;
            await view.OnNavigationPresentedAsync(context);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

            Assert.NotNull(GetPrivateField(viewModel, "_navigationHighlightCancellation"));
            Assert.True(viewModel.Messages[targetIndex].IsNavigationTargetHighlighted);
            Assert.False(viewModel.Messages[targetIndex].IsNavigationTargetFading);
            Assert.Equal(settledWrites, GetCoordinatorProgrammaticOffsetWrites(coordinator));
            Assert.Equal(settledOffset, transcript.Offset);
            Assert.Equal(settledExtent, transcript.Extent);
            Assert.Equal(settledBounds, targetPresenter.Bounds);
            Assert.Equal(
                targetTop,
                Assert.IsType<Point>(targetPresenter.TranslatePoint(default, transcript)).Y,
                precision: 3);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task SubsessionsView_SameSessionNonAnchorClearsHighlightBeforeAnchorAndAccessibilityLabel()
    {
        var harness = CreateSubsessionHistoryHighlightHarness();
        using var scope = harness.Scope;
        using var catalog = harness.Catalog;
        using var runtimeAdapter = harness.RuntimeAdapter;
        using var view = harness.View;
        var window = harness.Window;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var turn = harness.Turns[0];
            var item = Assert.Single(turn.Items);
            var anchorContext = CreateSubsessionNavigation(
                harness.Child.SessionId,
                turn.TurnId,
                item.ItemId,
                "Text",
                turn.CreatedAtUtc);

            await view.OnNavigatedToAsync(anchorContext);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

            var row = Assert.Single(
                harness.ViewModel.Messages,
                candidate => string.Equals(
                    candidate.AnchorKey.ToString(),
                    $"text:{turn.TurnId:N}",
                    StringComparison.Ordinal));
            var anchorKey = GetNonPublicProperty(harness.ViewModel, "NavigationAnchorKey");
            Assert.NotNull(anchorKey);
            var repeater = Assert.IsType<ItemsRepeater>(
                view.FindControl<ItemsRepeater>("TranscriptItemsControl"));
            var presenter = Assert.IsAssignableFrom<Control>(
                repeater.TryGetElement(harness.ViewModel.Messages.IndexOf(row)));
            Assert.True(row.IsNavigationTargetHighlighted);
            Assert.Equal("Search result target", row.NavigationHighlightHelpText);
            Assert.Equal(
                "Search result target",
                Avalonia.Automation.AutomationProperties.GetHelpText(presenter));

            object? anchorObservedWhenHighlightCleared = null;
            row.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(row.IsNavigationTargetHighlighted)
                    && !row.IsNavigationTargetHighlighted)
                {
                    anchorObservedWhenHighlightCleared = GetNonPublicProperty(
                        harness.ViewModel,
                        "NavigationAnchorKey");
                }
            };
            var nonAnchorContext = new PackageViewNavigationContext(
                SubagentConstants.SubsessionsViewId,
                new Dictionary<string, string?>
                {
                    ["sessionId"] = harness.Child.SessionId.ToString("D"),
                });

            Assert.True(await view.PrepareNavigationAsync(nonAnchorContext));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

            Assert.Equal(anchorKey, anchorObservedWhenHighlightCleared);
            Assert.Null(GetNonPublicProperty(harness.ViewModel, "NavigationAnchorKey"));
            Assert.False(row.IsNavigationTargetHighlighted);
            Assert.False(row.IsNavigationTargetFading);
            Assert.Null(row.NavigationHighlightHelpText);
            Assert.Null(Avalonia.Automation.AutomationProperties.GetHelpText(presenter));
            Assert.Null(GetPrivateField(harness.ViewModel, "_navigationHighlightCancellation"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task SubsessionsView_RapidRepeatHistoryHighlightIgnoresOlderClearCallback()
    {
        var harness = CreateSubsessionHistoryHighlightHarness();
        using var scope = harness.Scope;
        using var catalog = harness.Catalog;
        using var runtimeAdapter = harness.RuntimeAdapter;
        using var view = harness.View;
        var window = harness.Window;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var turn = harness.Turns[1];
            var item = Assert.Single(turn.Items);
            var context = CreateSubsessionNavigation(
                harness.Child.SessionId,
                turn.TurnId,
                item.ItemId,
                "Text",
                turn.CreatedAtUtc);

            await view.OnNavigatedToAsync(context);
            var firstCancellation = Assert.IsType<CancellationTokenSource>(
                GetPrivateField(harness.ViewModel, "_navigationHighlightCancellation"));
            var firstGeneration = Assert.IsType<long>(
                GetPrivateField(harness.ViewModel, "_navigationHighlightGeneration"));
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            await view.OnNavigatedToAsync(context);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var latestRow = Assert.Single(
                harness.ViewModel.Messages,
                candidate => string.Equals(
                    candidate.AnchorKey.ToString(),
                    $"text:{turn.TurnId:N}",
                    StringComparison.Ordinal));
            var repeater = Assert.IsType<ItemsRepeater>(
                view.FindControl<ItemsRepeater>("TranscriptItemsControl"));
            var latestPresenter = Assert.IsAssignableFrom<Control>(
                repeater.TryGetElement(harness.ViewModel.Messages.IndexOf(latestRow)));
            var latestGeneration = Assert.IsType<long>(
                GetPrivateField(harness.ViewModel, "_navigationHighlightGeneration"));
            Assert.True(firstCancellation.IsCancellationRequested);
            Assert.True(latestGeneration > firstGeneration);
            Assert.False(IsCurrentSubsessionNavigationHighlight(
                harness.ViewModel,
                latestRow.AnchorKey,
                firstGeneration,
                CancellationToken.None));
            Assert.True(latestRow.IsNavigationTargetHighlighted);

            var fading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            System.ComponentModel.PropertyChangedEventHandler onFading = (_, change) =>
            {
                if (change.PropertyName == nameof(latestRow.IsNavigationTargetFading)
                    && latestRow.IsNavigationTargetFading) fading.TrySetResult();
            };
            latestRow.PropertyChanged += onFading;
            try
            {
                if (latestRow.IsNavigationTargetFading) fading.TrySetResult();
                // Observe the actual transition inside the same budget. Render work
                // alone can overtake the Background callback which applies the fade.
                await fading.Task.WaitAsync(TimeSpan.FromMilliseconds(2100));
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);

                Assert.True(latestRow.IsNavigationTargetHighlighted);
                Assert.True(latestRow.IsNavigationTargetFading);
                Assert.Equal("Search result target", latestRow.NavigationHighlightHelpText);
                Assert.Equal(
                    "Search result target",
                    Avalonia.Automation.AutomationProperties.GetHelpText(latestPresenter));
            }
            finally { latestRow.PropertyChanged -= onFading; }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task SubsessionsView_LocalBlockedPageKeepsDispatcherAndWatchdogResponsive()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        var profile = await profileService.CreateProfileAsync("Subsession blocking profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Subsession blocking workspace");
        var parent = services.SessionService.CreateSession(
            "Subsession blocking parent",
            workspaceId: workspace.WorkspaceId);
        var child = services.SessionService.CreateSession(
            "Subsession blocking child",
            parentSessionId: parent.SessionId,
            rootSessionId: parent.SessionId,
            profileId: profile.ProfileId,
            agentKind: "subagent");
        var turns = Enumerable.Range(0, 90)
            .Select(index => services.SessionService.AppendTextTurn(
                child.SessionId,
                AgentMessageRole.Assistant,
                $"Blocking child response {index}: {new string('x', 180)}"))
            .ToArray();
        var runtime = new TestSubsessionRuntimeCatalog(
            [parent, child],
            [profile],
            turns);
        using var catalog = new RegressionTestExtensionCatalog();
        catalog.AddRuntimeCatalog(new SubsessionRuntimeCatalogServiceProxy(runtime));
        using var runtimeAdapter = new CatalogBackedSubsessionRuntimeAdapter(scope.Context, catalog);
        var viewModel = runtimeAdapter.CreateViewModel();
        viewModel.IsDetailedTranscript = true;
        using var view = new SubsessionsView(viewModel);
        var window = new Window { Width = 900, Height = 500, Content = view };
        window.Show();
        try
        {
            await view.OnNavigatedToAsync(new PackageViewNavigationContext(
                SubagentConstants.SubsessionsViewId,
                new Dictionary<string, string?>
                {
                    [SubagentConstants.SubsessionNavigationSessionIdKey] = child.SessionId.ToString("D"),
                }));
            await WaitUntilAsync(() => viewModel.Messages.Count == 60 && !viewModel.IsTranscriptLoading);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await GetPendingCoordinatorOperations(view);
            var transcript = Assert.IsType<ScrollViewer>(
                view.FindControl<ScrollViewer>("TranscriptScrollViewer"));
            var coordinator = GetTranscriptScrollCoordinator(view);
            transcript.Offset = default;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

            var eventTrace = new List<(string Kind, bool IsUiThread)>();
            viewModel.TranscriptChanging += _ => eventTrace.Add(("changing", Dispatcher.UIThread.CheckAccess()));
            viewModel.Messages.CollectionChanged += (_, _) =>
                eventTrace.Add(("collection", Dispatcher.UIThread.CheckAccess()));
            viewModel.TranscriptChanged += () =>
                eventTrace.Add(("changed", Dispatcher.UIThread.CheckAccess()));
            var uiThreadId = Environment.CurrentManagedThreadId;
            runtime.BlockNextTurnsBefore();

            Assert.True(InvokeQueueLoadOlderRows(coordinator));
            await runtime.BlockingReadStarted.Task;
            Assert.NotEqual(uiThreadId, runtime.BlockingReadThreadId);
            Assert.False(GetPrivateTask(coordinator, "_loadOlderOperation").IsCompleted);

            var inputProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(
                inputProcessed.SetResult,
                DispatcherPriority.Input);
            await inputProcessed.Task;

            var releaseWatchdog = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SetViewportMutationWatchdog(
                coordinator,
                cancellationToken => releaseWatchdog.Task.WaitAsync(cancellationToken));
            BeginViewportMutationWithoutGeometry(coordinator, "StructuralLayout");
            releaseWatchdog.TrySetResult();
            await GetViewportMutationCompletionOperation(coordinator);
            Assert.Contains(
                GetCoordinatorMutationStatus(coordinator),
                new[] { "Completed", "BudgetExhausted" });
            await GetPrivateTask(coordinator, "_viewportMutationWatchdogOperation");
            Assert.False(GetPrivateTask(coordinator, "_loadOlderOperation").IsCompleted);

            runtime.ReleaseBlockingRead.TrySetResult();
            await GetPendingCoordinatorOperations(view);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

            Assert.Equal(60, viewModel.Messages.Count);
            Assert.NotEmpty(eventTrace);
            Assert.All(eventTrace, entry => Assert.True(entry.IsUiThread));
            var firstChanging = eventTrace.FindIndex(entry => entry.Kind == "changing");
            var firstCollection = eventTrace.FindIndex(entry => entry.Kind == "collection");
            var lastCollection = eventTrace.FindLastIndex(entry => entry.Kind == "collection");
            var lastChanged = eventTrace.FindLastIndex(entry => entry.Kind == "changed");
            Assert.True(firstChanging >= 0 && firstChanging < firstCollection);
            Assert.True(lastCollection >= firstCollection && lastCollection < lastChanged);
        }
        finally
        {
            runtime.ReleaseBlockingRead.TrySetResult();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task SubsessionsView_OrdinaryInitializationFailureOpensWithVisibleRetry()
    {
        using var runtimeAdapter = new CatalogBackedSubsessionRuntimeAdapter(new ThrowingRpcClient());
        var viewModel = runtimeAdapter.CreateViewModel();
        using var view = new SubsessionsView(viewModel);
        var window = new Window { Width = 800, Height = 500, Content = view };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        var prepared = await view.PrepareNavigationAsync(new PackageViewNavigationContext(
            SubagentConstants.SubsessionsViewId,
            new Dictionary<string, string?>()));

        Assert.True(prepared);
        Assert.True(viewModel.HasLoadError);
        Assert.True(Assert.IsType<Border>(view.FindControl<Border>("SubsessionLoadError")).IsVisible);
        Assert.True(viewModel.RetryLoadCommand.CanExecute(null));
        Assert.Contains("Injected catalog failure", viewModel.StatusText, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SubsessionsView_AnchoredInitializationFailureCompletesVisibleFallbackPlacement()
    {
        using var runtimeAdapter = new CatalogBackedSubsessionRuntimeAdapter(new ThrowingRpcClient());
        var viewModel = runtimeAdapter.CreateViewModel();
        using var view = new SubsessionsView(viewModel);
        var window = new Window { Width = 800, Height = 500, Content = view };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        var transcript = Assert.IsType<ScrollViewer>(
            view.FindControl<ScrollViewer>("TranscriptScrollViewer"));
        var wasHidden = false;
        transcript.PropertyChanged += (_, change) =>
        {
            if (change.Property == Visual.OpacityProperty && transcript.Opacity == 0)
            {
                wasHidden = true;
            }
        };

        await view.OnNavigatedToAsync(CreateSubsessionNavigation(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Text"))
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(wasHidden);
        Assert.Equal(1, transcript.Opacity);
        Assert.Contains("Injected catalog failure", viewModel.StatusText, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SubsessionsView_MissingExactActivityAnchorCompletesVisibleFallbackPlacement()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        var workspace = services.WorkspaceService.CreateWorkspace("Anchor fallback workspace");
        var parent = services.SessionService.CreateSession(
            "Anchor fallback parent",
            workspaceId: workspace.WorkspaceId);
        var child = services.SessionService.CreateSession(
            "Anchor fallback child",
            parentSessionId: parent.SessionId);
        var turn = services.SessionService.AppendTextTurn(
            child.SessionId,
            AgentMessageRole.Assistant,
            "Child response.");
        services.ExtensionCatalog.AddRuntimeCatalog(
            new AgentRuntimeCatalog(services.SessionService, profileService, services.WorkspaceService));
        using var runtimeAdapter = new CatalogBackedSubsessionRuntimeAdapter(scope.Context, services.ExtensionCatalog);
        var viewModel = runtimeAdapter.CreateViewModel();
        using var view = new SubsessionsView(viewModel);
        var window = new Window { Width = 800, Height = 500, Content = view };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        var transcript = Assert.IsType<ScrollViewer>(
            view.FindControl<ScrollViewer>("TranscriptScrollViewer"));

        await view.OnNavigatedToAsync(CreateSubsessionNavigation(
                child.SessionId,
                turn.TurnId,
                Guid.NewGuid(),
                "Activity",
                turn.CreatedAtUtc))
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, transcript.Opacity);
        Assert.Contains("activity anchor is no longer available", viewModel.StatusText, StringComparison.Ordinal);
        window.Close();
    }

    private static (
        SubsessionsViewModel ViewModel,
        SubsessionsView View,
        Window Window,
        AgentSessionRecord Child,
        AgentTurnRecord[] Turns,
        RegressionTestPackageScope Scope,
        RegressionTestExtensionCatalog Catalog,
        CatalogBackedSubsessionRuntimeAdapter RuntimeAdapter) CreateSubsessionHistoryHighlightHarness()
    {
        var scope = RegressionTestPackageScope.Create();
        var createdAt = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
        var parent = new AgentSessionRecord(
            Guid.NewGuid(),
            "Highlight parent",
            AgentSessionState.Completed,
            createdAt,
            createdAt);
        var child = new AgentSessionRecord(
            Guid.NewGuid(),
            "Highlight child",
            AgentSessionState.Completed,
            createdAt,
            createdAt,
            ParentSessionId: parent.SessionId,
            RootSessionId: parent.SessionId,
            AgentKind: "subagent");
        var turns = Enumerable.Range(0, 3)
            .Select(index => CreateTextTurn(
                child.SessionId,
                createdAt.AddSeconds(index),
                $"Highlight response {index}"))
            .ToArray();
        var runtime = new TestSubsessionRuntimeCatalog([parent, child], [], turns);
        var catalog = new RegressionTestExtensionCatalog();
        catalog.AddRuntimeCatalog(new SubsessionRuntimeCatalogServiceProxy(runtime));
        var runtimeAdapter = new CatalogBackedSubsessionRuntimeAdapter(
            scope.Context,
            catalog);
        var viewModel = runtimeAdapter.CreateViewModel();
        viewModel.IsDetailedTranscript = true;
        var view = new SubsessionsView(viewModel);
        var window = new Window { Width = 900, Height = 500, Content = view };
        window.Show();
        return (viewModel, view, window, child, turns, scope, catalog, runtimeAdapter);
    }

    private static object? GetNonPublicProperty(object instance, string propertyName)
        => instance.GetType()
            .GetProperty(
                propertyName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(instance);

    private static bool IsCurrentSubsessionNavigationHighlight(
        SubsessionsViewModel viewModel,
        object anchorKey,
        long generation,
        CancellationToken cancellationToken)
        => Assert.IsType<bool>(viewModel.GetType()
            .GetMethod(
                "IsCurrentNavigationHighlight",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(viewModel, [anchorKey, generation, cancellationToken]));

    private static void BeginViewportMutationWithoutGeometry(object coordinator, string kindName)
    {
        var method = coordinator.GetType().GetMethod(
            "BeginViewportMutationTransaction",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var kind = Enum.Parse(method.GetParameters()[0].ParameterType, kindName);
        method.Invoke(coordinator, [kind, null, null, null, true]);
    }

    private static void SetViewportMutationWatchdog(
        object coordinator,
        Func<CancellationToken, Task> watchdog)
        => coordinator.GetType()
            .GetField(
                "_waitForViewportMutationWatchdog",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(coordinator, watchdog);

    private static AgentTurnRecord CreateParallelToolTurn(Guid sessionId, int itemCount)
    {
        var turnId = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow;
        return new AgentTurnRecord(
            turnId,
            sessionId,
            AgentMessageRole.Assistant,
            AgentTurnKind.ToolCall,
            Enumerable.Range(0, itemCount)
                .Select(index => new AgentTurnItemRecord(
                    Guid.NewGuid(),
                    turnId,
                    index,
                    AgentTurnItemKind.ToolCall,
                    null,
                    $"parallel-call-{index}",
                    "test_tool",
                    "{}",
                    null,
                    null,
                    null,
                    false,
                    false,
                    null,
                    null))
                .ToArray(),
            timestamp,
            timestamp);
    }

    private sealed class TestSubsessionRuntimeCatalog(
        IReadOnlyList<AgentSessionRecord> sessions,
        IReadOnlyList<AgentProfileRecord> profiles,
        IReadOnlyList<AgentTurnRecord> turns) : IAgentRuntimeCatalog
    {
        private int _blockNextTurnsBefore;
        private int _recentTranscriptReadCount;

        public event Action<Guid>? SessionChanged
        {
            add { }
            remove { }
        }

        public event Action<Guid, AgentTurnRecord>? TurnChanged
        {
            add { }
            remove { }
        }

        public event Action<string>? ProfileChanged
        {
            add { }
            remove { }
        }

        public event Action? ResetRequired
        {
            add { }
            remove { }
        }

        public TaskCompletionSource BlockingReadStarted { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseBlockingRead { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int BlockingReadThreadId { get; private set; }

        public int RecentTranscriptReadCount => Volatile.Read(ref _recentTranscriptReadCount);
        public List<string> HeaderCalls { get; } = [];

        public void BlockNextTurnsBefore() => Interlocked.Exchange(ref _blockNextTurnsBefore, 1);

        public IReadOnlyList<AgentSessionRecord> ListSessions() => sessions;

        public IReadOnlyList<AgentSessionRecord> ListSessionsForProfile(string profileId)
            => sessions.Where(session => string.Equals(
                session.ProfileId,
                profileId,
                StringComparison.OrdinalIgnoreCase)).ToArray();

        public IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(string workspaceId)
            => sessions.Where(session => string.Equals(
                session.WorkspaceId,
                workspaceId,
                StringComparison.OrdinalIgnoreCase)).ToArray();

        public AgentSessionRecord? GetSession(Guid sessionId)
            => sessions.FirstOrDefault(session => session.SessionId == sessionId);

        public ValueTask<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentWorkspaceRecord>>([]);

        public ValueTask<AgentWorkspaceRecord?> GetWorkspaceAsync(
            string workspaceId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentWorkspaceRecord?>(null);

        public AgentProfileRecord? GetSessionProfile(Guid sessionId)
            => GetSession(sessionId) is { ProfileId: { } profileId }
                ? GetProfile(profileId)
                : null;


        public AgentSessionContextCheckpointRecord? GetLatestSessionContextCheckpoint(Guid sessionId) => null;

        public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => null;

        public IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit)
        {
            Interlocked.Increment(ref _recentTranscriptReadCount);
            var result = ForSession(sessionId).TakeLast(Math.Max(0, limit)).ToArray();
            HeaderCalls.Add($"recent({sessionId:N}, {limit})={result.Length}");
            return result;
        }

        public IReadOnlyList<AgentTurnRecord> ListTurnsBefore(
            Guid sessionId,
            DateTimeOffset beforeCreatedAtUtc,
            Guid beforeTurnId,
            int limit)
        {
            if (Interlocked.Exchange(ref _blockNextTurnsBefore, 0) != 0)
            {
                BlockingReadThreadId = Environment.CurrentManagedThreadId;
                BlockingReadStarted.TrySetResult();
                ReleaseBlockingRead.Task.GetAwaiter().GetResult();
            }

            var result = ForSession(sessionId)
                .Where(turn => ComparePosition(turn, beforeCreatedAtUtc, beforeTurnId) < 0)
                .TakeLast(Math.Max(0, limit))
                .ToArray();
            HeaderCalls.Add($"before({sessionId:N}, {beforeCreatedAtUtc:O}, {beforeTurnId:N}, {limit})={result.Length}");
            return result;
        }

        public IReadOnlyList<AgentTurnRecord> ListTurnsAfter(
            Guid sessionId,
            DateTimeOffset afterCreatedAtUtc,
            Guid afterTurnId,
            int limit)
        {
            var result = ForSession(sessionId)
                .Where(turn => ComparePosition(turn, afterCreatedAtUtc, afterTurnId) > 0)
                .Take(Math.Max(0, limit))
                .ToArray();
            HeaderCalls.Add($"after({sessionId:N}, {afterCreatedAtUtc:O}, {afterTurnId:N}, {limit})={result.Length}");
            return result;
        }

        public IReadOnlyList<AgentProfileRecord> ListProfiles() => profiles;

        public AgentProfileRecord? GetProfile(string profileId)
            => profiles.FirstOrDefault(profile => string.Equals(
                profile.ProfileId,
                profileId,
                StringComparison.OrdinalIgnoreCase));

        public AgentProfileModelBindingRecord? GetSessionModelBinding(Guid sessionId, string capabilityKind)
            => null;

        public AgentProfileModelBindingRecord? GetModelBinding(string profileId, string capabilityKind)
            => null;

        private IEnumerable<AgentTurnRecord> ForSession(Guid sessionId)
            => turns
                .Where(turn => turn.SessionId == sessionId)
                .OrderBy(turn => turn.CreatedAtUtc)
                .ThenBy(turn => turn.TurnId);

        private static int ComparePosition(
            AgentTurnRecord turn,
            DateTimeOffset boundaryCreatedAtUtc,
            Guid boundaryTurnId)
        {
            var createdComparison = turn.CreatedAtUtc.CompareTo(boundaryCreatedAtUtc);
            return createdComparison != 0
                ? createdComparison
                : turn.TurnId.CompareTo(boundaryTurnId);
        }
    }
}
