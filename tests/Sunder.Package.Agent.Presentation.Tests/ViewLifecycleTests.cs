using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using System.Text.Json;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Builder;
using Sunder.Package.Agent.Execution.Docker;
using Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.Mcp;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Package.Agent.Memory.Semantic;
using Sunder.Package.Agent.Memory.Semantic.PackageViews;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Shared.PackageViews;
using Sunder.Package.Agent.Storage;
using Sunder.Package.Agent.Provider.Anthropic;
using Sunder.Package.Agent.Provider.Gemini;
using Sunder.Package.Agent.Provider.LMStudio;
using Sunder.Package.Agent.Provider.OpenAI;
using Sunder.Package.Agent.Skills.PackageViews;
using Sunder.Package.Agent.Skills.Services;
using Sunder.Package.Agent.Subagents.PackageViews;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Runtime;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class ViewLifecycleTests
{
    [AvaloniaFact]
    public async Task HistorySearchView_UsesLockedBreakpointsWithoutHorizontalOverflow()
    {
        using var scope = RegressionTestPackageScope.Create();
        var runtime = new HistoryRuntimeClient();
        var services = CreateHistoryAppServices(scope, runtime);
        await using var provider = services.BuildServiceProvider();
        using var view = ActivatorUtilities.CreateInstance<AgentHistorySearchView>(provider);
        var viewModel = Assert.IsType<AgentHistorySearchViewModel>(view.DataContext);
        var window = new Window { Width = 180, Height = 600, Content = view };
        window.Show();
        await view.WarmupAsync();
        Assert.False(view.FindControl<Border>("ScopeSection")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<ComboBox>("HistoryScopeComboBox")!.IsEffectivelyVisible);
        viewModel.ToggleAdvancedCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionOptions.Count > 0);
        viewModel.SelectedWhen = viewModel.WhenOptions.Single(option => option.Id == "custom");
        viewModel.FromDate = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        viewModel.ToDate = new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero);

        foreach (var width in new[] { 180d, 360d, 519d, 520d, 819d, 820d })
        {
            window.Width = width;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

            var scrollViewer = view.FindControl<ScrollViewer>("HistoryScrollViewer")!;
            var layout = view.FindControl<Border>("HistoryLayoutRoot")!;
            var advanced = view.FindControl<Grid>("AdvancedSectionsGrid")!;
            Assert.True(scrollViewer.Extent.Width <= scrollViewer.Viewport.Width + 0.5,
                $"History content overflowed at {width}px: {scrollViewer.Extent.Width} > {scrollViewer.Viewport.Width}.");
            Assert.Equal(width < 520, layout.Classes.Contains("compact"));
            Assert.Equal(
                width >= 520 && width < 820,
                layout.Classes.Contains("medium"));
            Assert.Equal(width >= 820, layout.Classes.Contains("wide"));
            Assert.Equal(width < 520 ? 1 : 2,
                advanced.ColumnDefinitions.Count);
            Assert.Equal(width < 520, viewModel.IsCompactLayout);
        }

        var visibleButtonLabels = view.GetVisualDescendants()
            .OfType<Button>()
            .Where(static button => button.IsVisible)
            .Select(static button => button.Content?.ToString())
            .Where(static content => content is not null)
            .ToArray();
        Assert.DoesNotContain("Search", visibleButtonLabels);
        Assert.DoesNotContain("Rebuild", visibleButtonLabels);
        Assert.DoesNotContain("Clear index", visibleButtonLabels);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Control>(), control =>
            control.GetType().Name.Contains("Semantic", StringComparison.OrdinalIgnoreCase));
        Assert.All(view.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("history-result")),
            button =>
            {
                Assert.NotNull(Avalonia.Automation.AutomationProperties.GetName(button));
                Assert.NotNull(Avalonia.Automation.AutomationProperties.GetHelpText(button));
            });
        var advancedToggle = view.FindControl<ToggleButton>("HistoryAdvancedToggle")!;
        Assert.True(advancedToggle.IsChecked);
        Assert.Equal("Advanced history filters", Avalonia.Automation.AutomationProperties.GetName(advancedToggle));
        Assert.Contains("Hide", Avalonia.Automation.AutomationProperties.GetHelpText(advancedToggle));
        window.Close();
    }

    [AvaloniaFact]
    public async Task HistorySearchView_OnlyNavigationFocusesQueryAndKeyboardCommandsAreImmediate()
    {
        using var scope = RegressionTestPackageScope.Create();
        var runtime = new HistoryRuntimeClient();
        var services = CreateHistoryAppServices(scope, runtime);
        await using var provider = services.BuildServiceProvider();
        using var view = ActivatorUtilities.CreateInstance<AgentHistorySearchView>(provider);
        var viewModel = Assert.IsType<AgentHistorySearchViewModel>(view.DataContext);
        var outside = new Button { Content = "Outside" };
        var host = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(outside, 0);
        Grid.SetRow(view, 1);
        host.Children.Add(view);
        host.Children.Add(outside);
        var window = new Window { Width = 520, Height = 600, Content = host };
        window.Show();
        outside.Focus();

        await view.WarmupAsync();
        var query = view.FindControl<TextBox>("HistoryQueryTextBox")!;
        Assert.False(query.IsKeyboardFocusWithin);

        await view.OnNavigatedToAsync(new PackageViewNavigationContext(
            "sunder.package.agent.history",
            new Dictionary<string, string?>()));
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Assert.True(query.IsKeyboardFocusWithin);

        viewModel.QueryText = "keyboard query";
        var searches = runtime.SearchCount;
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        await WaitUntilAsync(() => runtime.SearchCount == searches + 1);

        viewModel.IsAdvancedExpanded = true;
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, string.Empty);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, string.Empty);
        await WaitUntilAsync(() => viewModel.QueryText.Length == 0);
        Assert.True(viewModel.IsAdvancedExpanded);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, string.Empty);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, string.Empty);
        Assert.False(viewModel.IsAdvancedExpanded);

        viewModel.QueryText = "select this query";
        view.Focus();
        window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
        window.KeyRelease(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
        Assert.True(query.IsKeyboardFocusWithin);
        Assert.Equal(0, query.SelectionStart);
        Assert.Equal(query.Text?.Length ?? 0, query.SelectionEnd);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AgentChatView_TranscriptUsesFullWidthResponsiveGutters()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Layout profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Layout workspace");
        var session = services.SessionService.CreateSession(
            "Layout session",
            workspaceId: workspace.WorkspaceId);
        services.SessionService.AppendTextTurn(
            session.SessionId,
            AgentMessageRole.Assistant,
            "A transcript row that verifies the responsive content surface.");
        using var view = CreateAgentChatView(scope, services, profileService);
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        await view.OnNavigatedToAsync(new PackageViewNavigationContext(
            "sunder.package.agent.chat",
            new Dictionary<string, string?>()));
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        var scrollViewer = view.FindControl<ScrollViewer>("TranscriptScrollViewer")!;
        var content = view.FindControl<Border>("TranscriptScrollContent")!;
        var repeater = view.FindControl<ItemsRepeater>("TranscriptItemsControl")!;
        Assert.False(scrollViewer.BringIntoViewOnFocusChange);
        Assert.Equal(new Thickness(30, 30, 42, 30), content.Padding);
        Assert.DoesNotContain("compact", content.Classes);
        Assert.True(double.IsPositiveInfinity(content.MaxWidth));
        Assert.Equal(HorizontalAlignment.Stretch, content.HorizontalAlignment);
        Assert.Equal(HorizontalAlignment.Stretch, repeater.HorizontalAlignment);
        Assert.Equal(scrollViewer.Viewport.Width, content.Bounds.Width, precision: 3);
        Assert.True(scrollViewer.Extent.Width <= scrollViewer.Viewport.Width + 0.5);

        window.Width = 521;
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Assert.DoesNotContain("compact", content.Classes);

        window.Width = 519;
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        Assert.Contains("compact", content.Classes);
        Assert.Equal(new Thickness(16, 30, 24, 30), content.Padding);
        Assert.Equal(scrollViewer.Viewport.Width, content.Bounds.Width, precision: 3);
        Assert.True(scrollViewer.Extent.Width <= scrollViewer.Viewport.Width + 0.5);
        window.Close();
    }

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
        using var view = ActivatorUtilities.CreateInstance<AgentChatView>(provider);
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
                "sunder.package.agent.chat",
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
                "sunder.package.agent.chat",
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
        using var view = ActivatorUtilities.CreateInstance<AgentChatView>(provider);
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
                    "sunder.package.agent.chat",
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
    public async Task AgentChatView_TranscriptRealizesOnlyViewportRows()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Virtualized profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Virtualized workspace");
        var session = services.SessionService.CreateSession(
            "Virtualized session",
            workspaceId: workspace.WorkspaceId);
        for (var index = 0; index < 90; index++)
        {
            if (index % 3 == 0)
            {
                var callId = $"call-{index}";
                services.SessionService.AppendToolCallTurn(
                    session.SessionId,
                    AgentMessageRole.Assistant,
                    callId,
                    "test_tool",
                    "{}");
                services.SessionService.AppendToolResultTurn(
                    session.SessionId,
                    callId,
                    "test_tool",
                    "{}",
                    $"Output {index}",
                    "Completed.",
                    null,
                    null,
                    false,
                    false,
                    null,
                    null);
            }
            else
            {
                services.SessionService.AppendTextTurn(
                    session.SessionId,
                    AgentMessageRole.Assistant,
                    $"Response {index}: {new string('x', 400)}");
            }
        }

        using var view = new AgentChatView(
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

        await Task.Run(async () => await view.OnNavigatedToAsync(
            new PackageViewNavigationContext(
                "sunder.package.agent.chat",
                new Dictionary<string, string?>())));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);

        var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
        var repeater = view.FindControl<ItemsRepeater>("TranscriptItemsControl")!;
        var transcript = GetTranscriptScrollViewer(view);
        var realizedRows = Enumerable.Range(0, viewModel.Messages.Count)
            .Count(index => repeater.TryGetElement(index) is not null);
        Assert.Equal(viewModel.Messages.Count + 2, viewModel.TranscriptItems.Count);
        Assert.Same(viewModel.RunActivityRow, viewModel.TranscriptItems[^2]);
        Assert.IsType<AgentTranscriptTailSentinelRowViewModel>(viewModel.TranscriptItems[^1]);
        Assert.Contains(viewModel.Messages, row => row is AgentTextTranscriptRowViewModel);
        Assert.Contains(viewModel.Messages, row => row is AgentToolInvocationRowViewModel);
        Assert.InRange(realizedRows, 1, 16);
        Assert.Null(repeater.TryGetElement(0));
        Assert.NotNull(repeater.TryGetElement(viewModel.Messages.Count - 1));
        var activity = Assert.IsAssignableFrom<Control>(
            repeater.TryGetElement(viewModel.TranscriptItems.Count - 2));
        var tail = Assert.IsAssignableFrom<Control>(
            repeater.TryGetElement(viewModel.TranscriptItems.Count - 1));
        Assert.True(activity.IsVisible);
        Assert.False(activity.IsHitTestVisible);
        Assert.False(viewModel.RunActivityRow.IsLayoutVisible);
        Assert.Equal(1, activity.Bounds.Height, precision: 3);
        Assert.Equal(1, tail.Bounds.Height, precision: 3);
        AssertTranscriptTailGeometry(view, viewModel);
        Assert.Null(GetRepeaterMadeAnchor(repeater));
        await WaitForVisibleMarkdownToSettleAsync(view);

        window.MouseMove(new Point(450, 250), RawInputModifiers.None);
        window.MouseWheel(new Point(450, 250), new Vector(0, 200), RawInputModifiers.None);
        await WaitForPendingCoordinatorOperationsAsync(view, TimeSpan.FromSeconds(10));
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
        var detachedAnchor = Assert.IsAssignableFrom<Control>(transcript.CurrentAnchor);
        var detachedRow = Assert.Single(
            viewModel.Messages,
            row => ReferenceEquals(row, detachedAnchor.DataContext));
        var detachedAnchorKey = detachedRow.AnchorKey;
        var detachedAnchorIndex = viewModel.Messages
            .Select((row, index) => (row.AnchorKey, Index: index))
            .Single(item => Equals(item.AnchorKey, detachedAnchorKey))
            .Index;
        Assert.Same(detachedAnchor, repeater.TryGetElement(detachedAnchorIndex));
        Assert.Null(GetRepeaterMadeAnchor(repeater));

        GetTranscriptBehavior(view).GetType()
            .GetMethod(nameof(TranscriptViewBehavior.FollowLatestFromExplicitIntent))!
            .Invoke(GetTranscriptBehavior(view), null);
        await WaitForPendingCoordinatorOperationsAsync(view);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);

        AssertTranscriptTailGeometry(view, viewModel);
        Assert.NotSame(detachedAnchor, transcript.CurrentAnchor);
        Assert.Null(GetRepeaterMadeAnchor(repeater));
        window.Close();
    }

    [AvaloniaFact]
    public async Task AgentChatView_CollapsedToolDetailsAreAbsentAndDestroyedOnCollapse()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Tool details profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Tool details workspace");
        var session = services.SessionService.CreateSession(
            "Tool details session",
            workspaceId: workspace.WorkspaceId);
        services.SessionService.AppendToolCallTurn(
            session.SessionId,
            AgentMessageRole.Assistant,
            "details-call",
            "test_tool",
            "{}");
        services.SessionService.AppendToolResultTurn(
            session.SessionId,
            "details-call",
            "test_tool",
            "{}",
            "Deferred output",
            "Completed.",
            null,
            null,
            false,
            false,
            null,
            null);
        using var view = CreateAgentChatView(scope, services, profileService);
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        await view.OnNavigatedToAsync(new PackageViewNavigationContext(
            "sunder.package.agent.chat",
            new Dictionary<string, string?>()));
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
        var toolRow = Assert.Single(viewModel.Messages.OfType<AgentToolInvocationRowViewModel>());
        Assert.False(toolRow.IsExpanded);
        Assert.False(toolRow.HasMaterializedDetails);
        Assert.Empty(FindToolDetailCards(view));
        var portal = Assert.IsAssignableFrom<Panel>(
            view.FindControl<Control>("ToolDetailPreparationPortal"));
        Assert.Empty(portal.GetVisualDescendants().OfType<StreamingMarkdownPresenter>());

        var toolHeader = Assert.Single(
            view.GetVisualDescendants().OfType<Button>(),
            button => button.Classes.Contains("tool-step-header")
                      && ReferenceEquals(button.DataContext, toolRow));
        toolHeader.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => toolRow.IsExpanded);
        await WaitForPendingCoordinatorOperationsAsync(view);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        var detailCard = Assert.Single(FindToolDetailCards(view));
        Assert.True(toolRow.HasMaterializedDetails);

        toolHeader.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => !toolRow.IsExpanded);
        await WaitForPendingCoordinatorOperationsAsync(view);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        Assert.False(toolRow.HasMaterializedDetails);
        Assert.Empty(FindToolDetailCards(view));
        Assert.DoesNotContain(
            view.GetVisualDescendants().OfType<StreamingMarkdownPresenter>(),
            markdown => ReferenceEquals(markdown.DataContext, toolRow));

        toolHeader.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => toolRow.IsExpanded);
        await WaitForPendingCoordinatorOperationsAsync(view);
        Assert.NotSame(detailCard, Assert.Single(FindToolDetailCards(view)));
        window.Close();
    }

    [AvaloniaFact]
    public void TranscriptRowPresenter_ItemsControlHostKeepsManualAnchoring()
    {
        var presenter = new TranscriptRowPresenter
        {
            AnchorKey = "subsession-row",
            Content = new Border { Height = 80 },
        };
        var itemsControl = new ItemsControl();
        itemsControl.Items.Add(presenter);
        var scrollViewer = new ScrollViewer { Content = itemsControl };
        var window = new Window { Width = 300, Height = 200, Content = scrollViewer };

        window.Show();

        Assert.True(GetPrivateBoolean(presenter, "_isManuallyRegistered"));
        window.Close();
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

        using var view = new AgentChatView(
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
            "sunder.package.agent.chat",
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
        using var view = new AgentChatView(
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
                "sunder.package.agent.chat",
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
            Assert.Equal(authorityAfterInteraction, GetCoordinatorAuthorityRevision(coordinator));
            Assert.Equal(writesAfterInteraction, GetCoordinatorProgrammaticOffsetWrites(coordinator));
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
        using var view = new AgentChatView(
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
                "sunder.package.agent.chat",
                new Dictionary<string, string?>()));
            await GetSettledScrollOperation(view).WaitAsync(TimeSpan.FromSeconds(3));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
            var transcript = GetTranscriptScrollViewer(view);
            var coordinator = GetTranscriptScrollCoordinator(view);
            var toolRow = Assert.Single(viewModel.Messages.OfType<AgentToolInvocationRowViewModel>());
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
            Assert.True(GetAnchorHostSuspended(view));

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
        using var view = new AgentChatView(
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
                "sunder.package.agent.chat",
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
            Assert.True(GetAnchorHostSuspended(view));

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

    [AvaloniaFact]
    public void PresentationViews_ConstructWithCompiledXaml()
    {
        Control[] views =
        [
            new SkillSettingsView(),
            new AgentMcpSettingsView(),
            new BuilderView(),
            new AgentChatView(),
            new AgentProfilesView(),
            new AgentWorkspacesView(),
            new SubagentsView(),
            new SubsessionsView(),
            new LocalExecutionSettingsView(),
            new DockerExecutionSettingsView(),
            new MemoryInspectorView(),
            new OpenAiSettingsView(),
            new AnthropicSettingsView(),
            new GeminiSettingsView(),
            new LMStudioSettingsView(),
        ];

        foreach (var view in views.OfType<IDisposable>())
        {
            view.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task RuntimeBackedViews_ConstructWithoutWaitingForBlockedRuntime()
    {
        using var scope = RegressionTestPackageScope.Create();
        var runtime = new BlockingRuntimeClient();
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(scope.Context);
        services.AddSingleton<IPackageRuntimeClient>(runtime);
        services.AddSingleton<AgentRpcCatalog>(new RegressionTestExtensionCatalog());
        services.AddSingleton<IPackageShellViewService, NoOpPackageShellViewService>();
        services.AddSingleton<IPackageNotificationService>(NullPackageNotificationService.Instance);
        services.AddSingleton<IBackgroundProcessQueue, NoOpBackgroundProcessQueue>();
        new Sunder.Package.Agent.AppPackageModule().ConfigureAppServices(services, scope.Context);
        new Sunder.Package.Agent.Memory.Semantic.AppPackageModule().ConfigureAppServices(services, scope.Context);
        new Sunder.Package.Agent.Subagents.AppPackageModule().ConfigureAppServices(services, scope.Context);
        new Sunder.Package.Agent.Builder.AppPackageModule().ConfigureAppServices(services, scope.Context);
        await using var provider = services.BuildServiceProvider();

        var chat = ActivatorUtilities.CreateInstance<AgentChatView>(provider);
        var profiles = ActivatorUtilities.CreateInstance<AgentProfilesView>(provider);
        var workspaces = ActivatorUtilities.CreateInstance<AgentWorkspacesView>(provider);
        var subagents = ActivatorUtilities.CreateInstance<SubagentsView>(provider);
        var subsessions = ActivatorUtilities.CreateInstance<SubsessionsView>(provider);
        var memory = ActivatorUtilities.CreateInstance<MemoryInspectorView>(provider);
        var builder = ActivatorUtilities.CreateInstance<BuilderView>(provider);
        var builderViewModel = Assert.IsType<BuilderViewModel>(builder.DataContext);

        Assert.Equal(0, runtime.InvocationCount);
        Assert.Equal(0, runtime.SubscriptionCount);
        chat.Dispose();
        profiles.Dispose();
        workspaces.Dispose();
        subagents.Dispose();
        subsessions.Dispose();
        memory.Dispose();
        builder.Dispose();
        var replacementBuilder = ActivatorUtilities.CreateInstance<BuilderView>(provider);
        Assert.Same(builderViewModel, replacementBuilder.DataContext);
        Assert.False(GetPrivateBoolean(builderViewModel, "_disposed"));
        replacementBuilder.Dispose();
    }

    [AvaloniaFact]
    public async Task DockerSettingsView_PreparationHydratesBeforePresentation()
    {
        using var scope = RegressionTestPackageScope.Create();
        var runtime = new BlockingDockerSettingsRuntimeClient();
        var services = new ServiceCollection();
        services.AddSingleton<IPackageRuntimeClient>(runtime);
        services.AddSingleton<IBackgroundProcessQueue, NoOpBackgroundProcessQueue>();
        new Sunder.Package.Agent.Execution.Docker.AppPackageModule()
            .ConfigureAppServices(services, scope.Context);
        await using var provider = services.BuildServiceProvider();
        using var view = ActivatorUtilities.CreateInstance<DockerExecutionSettingsView>(provider);
        var viewModel = Assert.IsType<DockerExecutionSettingsViewModel>(view.DataContext);
        var source = new Border();
        var candidateHost = new ContentControl
        {
            Content = view,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = new Grid { Children = { source, candidateHost } },
        };
        window.Show();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var lifecycle = Assert.IsAssignableFrom<IPackageViewNavigationPreparationTarget>(viewModel);
            var context = new PackageViewNavigationContext(
                "sunder.package.agent.execution.docker.settings",
                new Dictionary<string, string?>());

            var preparation = lifecycle.PrepareNavigationAsync(context).AsTask();
            await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(preparation.IsCompleted);
            Assert.True(viewModel.IsLoading);
            Assert.False(viewModel.CanMutate);
            Assert.Empty(viewModel.Images);
            Assert.Equal(0, candidateHost.Opacity);
            Assert.False(candidateHost.IsHitTestVisible);

            runtime.Complete(
                "450",
                "/usr/local/bin/docker",
                [
                    new DockerImageDefinition(
                        "ubuntu:24.04",
                        DockerImageStatus.Ready,
                        DateTimeOffset.UtcNow,
                        null),
                ],
                7);

            Assert.True(await preparation.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(viewModel.IsReady);
            Assert.True(viewModel.CanMutate);
            Assert.Equal("450", viewModel.TimeoutSeconds);
            Assert.Equal("/usr/local/bin/docker", viewModel.DockerCliPath);
            Assert.Equal("ubuntu:24.04", Assert.Single(viewModel.Images).ImageReference);
            Assert.Equal(0, candidateHost.Opacity);

            candidateHost.Opacity = 1;
            candidateHost.IsHitTestVisible = true;
            await lifecycle.OnNavigationPresentedAsync(context);

            Assert.Equal(1, candidateHost.Opacity);
            Assert.True(candidateHost.IsHitTestVisible);
            Assert.Equal(1, runtime.InvocationCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ParameterFreeViews_WarmupAndRepeatNavigationApplyOneSnapshot()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Warmup profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Warmup workspace");
        var subagentService = new SubagentService(new SubagentStore(scope.Context));
        subagentService.CreateSubagent("Warmup subagent");
        var parent = services.SessionService.CreateSession(
            "Warmup parent",
            workspaceId: workspace.WorkspaceId);
        var firstChild = services.SessionService.CreateSession("First child", parentSessionId: parent.SessionId);
        var secondChild = services.SessionService.CreateSession("Second child", parentSessionId: parent.SessionId);
        services.ExtensionCatalog.AddProvider(
            AgentRpcServices.RuntimeCatalogs,
            new AgentRuntimeCatalog(services.SessionService, profileService, services.WorkspaceService));

        using var profiles = new AgentProfilesView(profileService);
        using var workspaces = new AgentWorkspacesView(
            services.WorkspaceService,
            services.TargetService,
            services.ExtensionCatalog,
            new AgentExecutionTargetWarmupService(services.WorkspaceService, services.TargetService));
        using var subagents = new SubagentsView(new SubagentsViewModel(subagentService, services.ExtensionCatalog));
        using var subsessions = new SubsessionsView(new SubsessionsViewModel(services.ExtensionCatalog));
        var inspector = CreateMemoryInspector(scope.Context, services.ExtensionCatalog);
        var memoryViewModel = (MemoryInspectorViewModel)Activator.CreateInstance(
            typeof(MemoryInspectorViewModel),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            [inspector],
            culture: null)!;
        using var memory = new MemoryInspectorView(memoryViewModel);

        Assert.Empty(Assert.IsType<AgentProfilesViewModel>(profiles.DataContext).Profiles);
        Assert.Empty(Assert.IsType<AgentWorkspacesViewModel>(workspaces.DataContext).Workspaces);
        Assert.Empty(Assert.IsType<SubagentsViewModel>(subagents.DataContext).Subagents);
        Assert.Empty(Assert.IsType<SubsessionsViewModel>(subsessions.DataContext).Subsessions);
        Assert.Empty(memoryViewModel.Sessions);

        await Assert.IsAssignableFrom<IPackageViewWarmupTarget>(profiles).WarmupAsync();
        await Assert.IsAssignableFrom<IPackageViewWarmupTarget>(workspaces).WarmupAsync();
        await Assert.IsAssignableFrom<IPackageViewWarmupTarget>(subagents).WarmupAsync();
        await Assert.IsAssignableFrom<IPackageViewWarmupTarget>(subsessions).WarmupAsync();
        await Assert.IsAssignableFrom<IPackageViewWarmupTarget>(memory).WarmupAsync();

        var profilesViewModel = Assert.IsType<AgentProfilesViewModel>(profiles.DataContext);
        var workspacesViewModel = Assert.IsType<AgentWorkspacesViewModel>(workspaces.DataContext);
        var subagentsViewModel = Assert.IsType<SubagentsViewModel>(subagents.DataContext);
        var subsessionViewModel = Assert.IsType<SubsessionsViewModel>(subsessions.DataContext);
        var profileSnapshot = profilesViewModel.Profiles.ToArray();
        var workspaceSnapshot = workspacesViewModel.Workspaces.ToArray();
        var subagentSnapshot = subagentsViewModel.Subagents.ToArray();
        var subsessionSnapshot = subsessionViewModel.Subsessions.ToArray();
        var memorySnapshot = memoryViewModel.Sessions.ToArray();
        var profileChanges = 0;
        var workspaceChanges = 0;
        var subagentChanges = 0;
        var subsessionChanges = 0;
        var memoryChanges = 0;
        profilesViewModel.Profiles.CollectionChanged += (_, _) => profileChanges++;
        workspacesViewModel.Workspaces.CollectionChanged += (_, _) => workspaceChanges++;
        subagentsViewModel.Subagents.CollectionChanged += (_, _) => subagentChanges++;
        subsessionViewModel.Subsessions.CollectionChanged += (_, _) => subsessionChanges++;
        memoryViewModel.Sessions.CollectionChanged += (_, _) => memoryChanges++;
        var emptyContext = new PackageViewNavigationContext("test", new Dictionary<string, string?>());

        await profiles.OnNavigatedToAsync(emptyContext);
        await profiles.OnNavigatedToAsync(emptyContext);
        await workspaces.OnNavigatedToAsync(emptyContext);
        await workspaces.OnNavigatedToAsync(emptyContext);
        await subagents.OnNavigatedToAsync(emptyContext);
        await subagents.OnNavigatedToAsync(emptyContext);
        await memory.OnNavigatedToAsync(emptyContext);
        await memory.OnNavigatedToAsync(emptyContext);

        Assert.Equal(profileSnapshot, profilesViewModel.Profiles);
        Assert.Equal(workspaceSnapshot, workspacesViewModel.Workspaces);
        Assert.Equal(subagentSnapshot, subagentsViewModel.Subagents);
        Assert.Equal(memorySnapshot, memoryViewModel.Sessions);
        Assert.Equal(0, profileChanges);
        Assert.Equal(0, workspaceChanges);
        Assert.Equal(0, subagentChanges);
        Assert.Equal(0, memoryChanges);

        var targetContext = new PackageViewNavigationContext(
            SubagentConstants.SubsessionsViewId,
            new Dictionary<string, string?>
            {
                [SubagentConstants.SubsessionNavigationSessionIdKey] = secondChild.SessionId.ToString("D"),
            });
        await subsessions.OnNavigatedToAsync(targetContext);
        var selectedSubsession = subsessionViewModel.SelectedSubsession;
        await subsessions.OnNavigatedToAsync(targetContext);

        Assert.Equal(subsessionSnapshot, subsessionViewModel.Subsessions);
        Assert.Contains(subsessionSnapshot, item => item.SessionId == firstChild.SessionId);
        Assert.Contains(subsessionSnapshot, item => item.SessionId == secondChild.SessionId);
        Assert.Equal(secondChild.SessionId, selectedSubsession?.SessionId);
        Assert.Same(selectedSubsession, subsessionViewModel.SelectedSubsession);
        Assert.Equal(0, subsessionChanges);
    }

    [AvaloniaFact]
    public async Task BuilderView_ConstructionIsColdAndWarmupNavigationShareOneLoad()
    {
        var store = new BlockingBuilderProjectStore();
        var pathService = new BuilderPathService();
        await using var viewModel = new BuilderViewModel(
            new BuilderProjectApplicationService(
                new BuilderSetupService(),
                new BuilderWorkspaceExecutionService(),
                store,
                pathService),
            new BuilderOperationQueue(new NoOpBackgroundProcessQueue()),
            new BuilderProjectPersistence(store),
            pathService,
            new AvaloniaBuilderUiDispatcher());
        using var view = new BuilderView(viewModel);
        var context = new PackageViewNavigationContext("sunder.package.agent.builder", new Dictionary<string, string?>());

        Assert.Equal(0, store.LoadCount);
        var warmup = view.WarmupAsync().AsTask();
        await store.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var navigation = view.OnNavigatedToAsync(context).AsTask();
        using var cancellation = new CancellationTokenSource();
        var canceledNavigation = view.OnNavigatedToAsync(context, cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledNavigation);
        Assert.Equal(1, store.LoadCount);
        store.ReleaseLoad.TrySetResult();
        await Task.WhenAll(warmup, navigation);
        await view.OnNavigatedToAsync(context);

        Assert.Equal(1, store.LoadCount);
    }

    [AvaloniaFact]
    public async Task ParameterFreeWorkspaceView_RetriesTransientWarmupFailure()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        services.WorkspaceService.CreateWorkspace("Retry workspace");
        var workspaceGateway = new FailOnceWorkspaceGateway(services.WorkspaceService);
        using var view = new AgentWorkspacesView(
            workspaceGateway,
            new AgentExecutionTargetWarmupService(
                services.WorkspaceService,
                services.TargetService),
            services.ExtensionCatalog);

        var firstWarmup = view.WarmupAsync().AsTask();
        await workspaceGateway.FirstInitializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        workspaceGateway.FailFirstInitialization.TrySetResult();
        await firstWarmup;
        await view.WarmupAsync();

        Assert.Equal(2, workspaceGateway.InitializeCount);
        var viewModel = Assert.IsType<AgentWorkspacesViewModel>(view.DataContext);
        Assert.Single(viewModel.Workspaces);
        Assert.DoesNotContain("unavailable", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task BuilderView_RetriesTransientWarmupFailure()
    {
        var store = new FailOnceBuilderProjectStore();
        var pathService = new BuilderPathService();
        await using var viewModel = new BuilderViewModel(
            new BuilderProjectApplicationService(
                new BuilderSetupService(),
                new BuilderWorkspaceExecutionService(),
                store,
                pathService),
            new BuilderOperationQueue(new NoOpBackgroundProcessQueue()),
            new BuilderProjectPersistence(store),
            pathService,
            new AvaloniaBuilderUiDispatcher());
        using var view = new BuilderView(viewModel);

        await view.WarmupAsync();
        await view.WarmupAsync();

        Assert.Equal(2, store.LoadCount);
        Assert.DoesNotContain(
            "initialization failed",
            viewModel.StatusText,
            StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task BuilderViewModel_DisposalDrainsBlockedInitializationBeforePersistenceDisposal()
    {
        var store = new BlockingBuilderProjectStore();
        var pathService = new BuilderPathService();
        var viewModel = new BuilderViewModel(
            new BuilderProjectApplicationService(
                new BuilderSetupService(),
                new BuilderWorkspaceExecutionService(),
                store,
                pathService),
            new BuilderOperationQueue(new NoOpBackgroundProcessQueue()),
            new BuilderProjectPersistence(store),
            pathService,
            new AvaloniaBuilderUiDispatcher());
        var initialization = viewModel.InitializeAsync();
        await store.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disposal = viewModel.DisposeAsync().AsTask();
        await store.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(disposal.IsCompleted);

        store.ReleaseCancellationCleanup.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        await initialization.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [AvaloniaFact]
    public async Task AgentPermissionsViewModel_EmptySuccessfulRetryClearsUnavailableStatus()
    {
        var gateway = new TogglePermissionGateway();
        using var viewModel = new AgentPermissionsViewModel(gateway);
        await viewModel.PrepareNavigationAsync(new PackageViewNavigationContext(
            "settings:sunder.package.agent.permissions",
            new Dictionary<string, string?>()));
        Assert.Contains("unavailable", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);

        gateway.IsAvailable = true;
        var reload = Assert.IsAssignableFrom<Task<bool>>(typeof(AgentPermissionsViewModel)
            .GetMethod(
                "TryReloadAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(viewModel, [CancellationToken.None]));
        var reloaded = await reload;

        Assert.True(reloaded);
        Assert.Empty(viewModel.Rows);
        Assert.Equal(string.Empty, viewModel.StatusText);
    }

    [AvaloniaFact]
    public void DisposableView_ActivatesAndReleasesOwnedSubscriptions()
    {
        using var view = new SkillSettingsView();
        var window = new Window { Content = view };

        window.Show();
        window.Close();
    }

    [AvaloniaFact]
    public void TranscriptViews_DisposeIdempotentlyAfterVisualAttachment()
    {
        var chat = new AgentChatView();
        var subsessions = new SubsessionsView();
        var window = new Window
        {
            Content = new StackPanel { Children = { chat, subsessions } },
        };

        window.Show();
        window.Close();
        chat.Dispose();
        chat.Dispose();
        subsessions.Dispose();
        subsessions.Dispose();
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
        using var view = new AgentChatView(
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
            "sunder.package.agent.chat",
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
        using var view = new AgentChatView(
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
            "sunder.package.agent.chat",
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

        using var view = new AgentChatView(
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
            "sunder.package.agent.chat",
            new Dictionary<string, string?>());

        var navigation = Task.Run(async () => await view.OnNavigatedToAsync(context, cancellation.Token));
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
        await WaitForTranscriptGeometrySettledAsync(window, transcript, coordinator);
        Assert.Equal(writesAfterCancellation, GetCoordinatorProgrammaticOffsetWrites(coordinator));
        Assert.Equal(authorityAfterCancellation, GetCoordinatorAuthorityRevision(coordinator));
        RecordDiagnostic(
            lifecycleTrace,
            DescribeAgentTranscriptState(view, transcript, coordinator, "geometry-established"));
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
            authorityAfterCancellation == GetCoordinatorAuthorityRevision(coordinator),
            $"Viewport authority changed after navigation cancellation.{Environment.NewLine}{string.Join(Environment.NewLine, lifecycleTrace)}");
        Assert.False(GetPrivateBoolean(coordinator, "_bottomPlacementLockActive"));
        Assert.False(GetAnchorHostSuspended(view));
        Assert.True(
            GetInitialPlacementCancellationCallbacks(view) > 0,
            $"Cancellation never claimed initial-placement authority.{Environment.NewLine}{string.Join(Environment.NewLine, lifecycleTrace)}");
        Assert.Equal(1d, transcript.Opacity);
        transcript.LayoutUpdated -= layoutHandler;
        await Task.Run(async () => await view.OnNavigatedToAsync(context));
        Assert.Equal(1d, transcript.Opacity);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AgentChatViewModel_QueuedAuthoritativeAddProjectsBeforeComposerClearsWithoutReplacement()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Send profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Send workspace");
        var session = services.SessionService.CreateSession(
            "Send session",
            workspaceId: workspace.WorkspaceId);
        var sessionGateway = new CountingSessionGateway(services.SessionService);
        using var viewModel = new AgentChatViewModel(
            profileService,
            services.WorkspaceService,
            sessionGateway,
            NoOpPermissionGateway.Instance,
            NoOpRunGateway.Instance);
        await viewModel.InitializeAsync();
        viewModel.SelectedSession = viewModel.Sessions.Single(item => item.SessionId == session.SessionId);
        const string message = "queued authoritative message";
        viewModel.DraftMessage = message;
        var composer = typeof(AgentChatViewModel)
            .GetField(
                "_composer",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(viewModel)!;
        var beginSubmission = composer.GetType()
            .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Single(method => method.Name == "TryBeginSubmission"
                              && method.GetParameters().Length == 2);
        var submission = beginSubmission.Invoke(
            composer,
            [session.SessionId, new HashSet<Guid>()]);
        Assert.NotNull(submission);
        var recentReadsBeforeCompletion = sessionGateway.RecentTranscriptReadCount;
        var turn = services.SessionService.AppendTextTurn(
            session.SessionId,
            AgentMessageRole.User,
            message);
        submission.GetType()
            .GetField(
                "<UserTurnId>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(submission, turn.TurnId);
        var rowWasPresentWhenComposerCleared = false;
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(AgentChatViewModel.DraftMessage)
                && string.IsNullOrEmpty(viewModel.DraftMessage))
            {
                rowWasPresentWhenComposerCleared = viewModel.Messages.Any(row => row.RowId == turn.TurnId);
            }
        };
        var completeSubmission = typeof(AgentChatViewModel).GetMethod(
            "CompleteOrRestoreComposerSubmissionAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        var completed = await Assert.IsAssignableFrom<Task<bool>>(completeSubmission.Invoke(
            viewModel,
            [viewModel.SelectedSession!, submission, true]));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.True(completed);
        Assert.True(rowWasPresentWhenComposerCleared);
        Assert.Empty(viewModel.DraftMessage);
        Assert.Single(viewModel.Messages, row => row.RowId == turn.TurnId);
        Assert.Equal(recentReadsBeforeCompletion, sessionGateway.RecentTranscriptReadCount);
        Assert.False(viewModel.IsTranscriptLoading);
    }

    [AvaloniaFact]
    public async Task AgentChatView_AssistantArrivalReplacesActivityWithoutExposingOlderPosition()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Live row profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Live row workspace");
        var session = services.SessionService.CreateSession(
            "Live row session",
            workspaceId: workspace.WorkspaceId);
        for (var index = 0; index < 8; index++)
        {
            services.SessionService.AppendTextTurn(
                session.SessionId,
                AgentMessageRole.Assistant,
                $"Existing response {index}: {new string('x', 100)}");
        }
        var runRevision = services.SessionService.GetNextRunRevision(session.SessionId);
        services.SessionService.SaveCheckpoint(
            session.SessionId,
            runRevision,
            AgentRunStatus.Running,
            "Thinking.");

        using var view = CreateAgentChatView(scope, services, profileService);
        var window = new Window { Width = 800, Height = 360, Content = view };
        window.Show();
        try
        {
            await view.OnNavigatedToAsync(new PackageViewNavigationContext(
                    "sunder.package.agent.chat",
                    new Dictionary<string, string?>()))
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
            var transcript = GetTranscriptScrollViewer(view);
            var repeater = Assert.IsType<ItemsRepeater>(view.FindControl<ItemsRepeater>("TranscriptItemsControl"));
            var jumpButton = Assert.IsType<Button>(view.FindControl<Button>("JumpToLatestTranscriptButton"));
            var completedLayoutDistances = new List<double>();
            var opacityWasHidden = false;
            var jumpBecameVisible = false;
            var collectionRemovals = 0;
            var observeLayouts = false;
            transcript.PropertyChanged += (_, change) =>
            {
                if (change.Property == Visual.OpacityProperty && transcript.Opacity == 0)
                {
                    opacityWasHidden = true;
                }
            };
            transcript.LayoutUpdated += (_, _) =>
            {
                if (observeLayouts && transcript.Viewport.Height > 0)
                {
                    completedLayoutDistances.Add(
                        transcript.Extent.Height - transcript.Viewport.Height - transcript.Offset.Y);
                }
            };
            viewModel.Messages.CollectionChanged += (_, eventArgs) =>
            {
                if (eventArgs.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
                {
                    collectionRemovals++;
                }
            };
            jumpButton.PropertyChanged += (_, change) =>
            {
                if (change.Property == Visual.IsVisibleProperty && jumpButton.IsVisible)
                {
                    jumpBecameVisible = true;
                }
            };
            const string markdown = "## New assistant response";
            Assert.True(viewModel.RunActivityRow.IsVisible);
            AssertTranscriptTailGeometry(view, viewModel);

            observeLayouts = true;
            services.SessionService.AppendTextTurn(
                session.SessionId,
                AgentMessageRole.Assistant,
                markdown);
            await WaitUntilAsync(() => viewModel.Messages
                .OfType<AgentTextTranscriptRowViewModel>()
                .Any(row => row.Content == markdown));
            await WaitForPendingCoordinatorOperationsAsync(view);
            await WaitUntilAsync(() => repeater.TryGetElement(viewModel.Messages.Count - 1) is Control row
                                       && row.GetVisualDescendants()
                                           .OfType<MarkdownRenderer>()
                                           .Any(renderer => renderer.Opacity == 1));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            observeLayouts = false;

            Assert.False(opacityWasHidden);
            Assert.False(jumpBecameVisible);
            Assert.False(viewModel.RunActivityRow.IsVisible);
            Assert.Equal(0, collectionRemovals);
            Assert.NotEmpty(completedLayoutDistances);
            Assert.All(
                completedLayoutDistances,
                distance => Assert.InRange(distance, -1, 1));
            Assert.Equal(
                transcript.Extent.Height - transcript.Viewport.Height,
                transcript.Offset.Y,
                precision: 3);
            AssertTranscriptTailGeometry(view, viewModel);

        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AgentChatView_ToolCallAndResultRemainPinnedWithoutCollectionRemoval()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Tool tail profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Tool tail workspace");
        var session = services.SessionService.CreateSession(
            "Tool tail session",
            workspaceId: workspace.WorkspaceId);
        for (var index = 0; index < 8; index++)
        {
            services.SessionService.AppendTextTurn(
                session.SessionId,
                AgentMessageRole.Assistant,
                $"Existing response {index}: {new string('x', 100)}");
        }
        var runRevision = services.SessionService.GetNextRunRevision(session.SessionId);
        services.SessionService.SaveCheckpoint(
            session.SessionId,
            runRevision,
            AgentRunStatus.Running,
            "Thinking.");

        using var view = CreateAgentChatView(scope, services, profileService);
        var window = new Window { Width = 800, Height = 360, Content = view };
        window.Show();
        try
        {
            await view.OnNavigatedToAsync(new PackageViewNavigationContext(
                "sunder.package.agent.chat",
                new Dictionary<string, string?>()));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
            var transcript = GetTranscriptScrollViewer(view);
            var repeater = Assert.IsType<ItemsRepeater>(
                view.FindControl<ItemsRepeater>("TranscriptItemsControl"));
            var completedLayoutDistances = new List<double>();
            var collectionRemovals = 0;
            var observeLayouts = false;
            transcript.LayoutUpdated += (_, _) =>
            {
                if (observeLayouts && transcript.Viewport.Height > 0)
                {
                    completedLayoutDistances.Add(
                        transcript.Extent.Height - transcript.Viewport.Height - transcript.Offset.Y);
                }
            };
            viewModel.Messages.CollectionChanged += (_, eventArgs) =>
            {
                if (eventArgs.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
                {
                    collectionRemovals++;
                }
            };
            Assert.True(viewModel.RunActivityRow.IsVisible);
            AssertTranscriptTailGeometry(view, viewModel);

            services.SessionService.ReportRunActivity(
                session.SessionId,
                runRevision,
                AgentRunActivityKind.Reasoning,
                "Inspecting a longer reasoning activity update that changes the persistent row height.");
            await WaitUntilAsync(() => viewModel.RunActivityRow.ThinkingText.Contains(
                "Inspecting",
                StringComparison.Ordinal));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            AssertTranscriptTailGeometry(view, viewModel);

            var largeDetailArguments = JsonSerializer.Serialize(new
            {
                detail = string.Join('\n', Enumerable.Range(0, 80).Select(index => $"Detail line {index}")),
            });
            var largeOutput = string.Join(
                '\n',
                Enumerable.Range(0, 100).Select(index => $"Output line {index}"));
            observeLayouts = true;
            services.SessionService.AppendToolCallTurn(
                session.SessionId,
                AgentMessageRole.Assistant,
                "tail-call",
                "test_tool",
                largeDetailArguments);
            await WaitUntilAsync(() => viewModel.Messages.OfType<AgentToolInvocationRowViewModel>().Any());
            Assert.False(viewModel.RunActivityRow.IsVisible);
            await WaitForPendingCoordinatorOperationsAsync(view);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            observeLayouts = false;

            Assert.Equal(0, collectionRemovals);
            Assert.NotEmpty(completedLayoutDistances);
            Assert.All(completedLayoutDistances, distance => Assert.InRange(distance, -1, 1));
            AssertTranscriptTailGeometry(view, viewModel);

            services.SessionService.ReportRunActivity(
                session.SessionId,
                runRevision,
                AgentRunActivityKind.Tool,
                "Running test tool");
            await WaitUntilAsync(() => viewModel.RunActivityRow.IsVisible);
            completedLayoutDistances.Clear();
            observeLayouts = true;
            services.SessionService.AppendToolResultTurn(
                session.SessionId,
                "tail-call",
                "test_tool",
                largeDetailArguments,
                largeOutput,
                "Failed.",
                null,
                null,
                false,
                true,
                "test_failure",
                "test");
            await WaitUntilAsync(() => viewModel.Messages
                .OfType<AgentToolInvocationRowViewModel>()
                .Any(row => row.IsFailed && !row.IsExpanded));
            Assert.False(viewModel.RunActivityRow.IsVisible);
            await WaitForPendingCoordinatorOperationsAsync(view);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            observeLayouts = false;

            var failedTool = Assert.Single(
                viewModel.Messages.OfType<AgentToolInvocationRowViewModel>(),
                row => row.IsFailed);
            Assert.True(failedTool.HasDetails);
            Assert.False(failedTool.HasMaterializedDetails);
            Assert.Equal(0, collectionRemovals);
            Assert.NotEmpty(completedLayoutDistances);
            Assert.All(completedLayoutDistances, distance => Assert.InRange(distance, -1, 1));
            Assert.Equal(
                transcript.Extent.Height - transcript.Viewport.Height,
                transcript.Offset.Y,
                precision: 3);
            AssertTranscriptTailGeometry(view, viewModel);

            services.SessionService.ReportRunActivity(
                session.SessionId,
                runRevision,
                AgentRunActivityKind.Tool,
                "Running detached failure tool");
            await WaitUntilAsync(() => viewModel.RunActivityRow.IsVisible);
            services.SessionService.AppendToolCallTurn(
                session.SessionId,
                AgentMessageRole.Assistant,
                "detached-failure-call",
                "test_tool",
                largeDetailArguments);
            await WaitUntilAsync(() => viewModel.Messages
                .OfType<AgentToolInvocationRowViewModel>()
                .Count() == 2);
            await WaitForPendingCoordinatorOperationsAsync(view);
            services.SessionService.ReportRunActivity(
                session.SessionId,
                runRevision,
                AgentRunActivityKind.Tool,
                "Collecting detached failure output");
            await WaitUntilAsync(() => viewModel.RunActivityRow.IsVisible);

            window.MouseMove(new Point(400, 180), RawInputModifiers.None);
            window.MouseWheel(new Point(400, 180), new Vector(0, 3), RawInputModifiers.None);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var detachedAnchor = Assert.IsAssignableFrom<Control>(transcript.CurrentAnchor);
            Assert.IsNotType<AgentTranscriptTailSentinelRowViewModel>(detachedAnchor.DataContext);
            var detachedAnchorKey = Assert.IsAssignableFrom<AgentTranscriptRowViewModel>(
                detachedAnchor.DataContext).AnchorKey;
            var detachedAnchorTop = Assert.IsType<Point>(
                detachedAnchor.TranslatePoint(default, transcript)).Y;

            services.SessionService.AppendToolResultTurn(
                session.SessionId,
                "detached-failure-call",
                "test_tool",
                largeDetailArguments,
                largeOutput,
                "Failed while detached.",
                null,
                null,
                false,
                true,
                "detached_failure",
                "test");
            await WaitUntilAsync(() => viewModel.Messages
                .OfType<AgentToolInvocationRowViewModel>()
                .Count(row => row.ResultTurnId is not null
                              && row.IsFailed
                              && !row.IsExpanded
                              && !row.HasMaterializedDetails) == 2);
            await WaitForPendingCoordinatorOperationsAsync(view);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

            var restoredAnchorIndex = viewModel.Messages
                .Select((row, index) => (row.AnchorKey, Index: index))
                .Single(item => Equals(item.AnchorKey, detachedAnchorKey))
                .Index;
            var restoredAnchor = Assert.IsAssignableFrom<Control>(repeater.TryGetElement(restoredAnchorIndex));
            Assert.InRange(
                Math.Abs(Assert.IsType<Point>(restoredAnchor.TranslatePoint(default, transcript)).Y
                         - detachedAnchorTop),
                0,
                1);
            Assert.False(viewModel.RunActivityRow.IsVisible);
            Assert.False(GetAnchorHostSuspended(view));
        }
        finally
        {
            window.Close();
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
                    "sunder.package.agent.chat",
                    new Dictionary<string, string?>())));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
            var transcript = GetTranscriptScrollViewer(view);
            var repeater = Assert.IsType<ItemsRepeater>(
                view.FindControl<ItemsRepeater>("TranscriptItemsControl"));
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
        var catalog = new RegressionTestExtensionCatalog();
        catalog.AddProvider(AgentRpcServices.RuntimeCatalogs, runtime, "test.runtime");
        var viewModel = new SubsessionsViewModel(catalog);
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
            viewModel.Messages.CollectionChanged += (_, change) => messageChanges.Add(change.Action);
            ((INotifyCollectionChanged)viewModel.TranscriptItems).CollectionChanged +=
                (_, change) => itemChanges.Add(change.Action);
            var context = CreateSubsessionNavigation(
                child.SessionId,
                parallelTurn.TurnId,
                targetItem.ItemId,
                "Activity",
                parallelTurn.CreatedAtUtc,
                targetItem.CallId);

            Assert.True(await view.PrepareNavigationAsync(context));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await GetPendingCoordinatorOperations(view);

            var targetAnchorKey = TranscriptRowAnchorKey.Tool(parallelTurn, targetItem).ToString();
            Assert.True(source.IsEffectivelyVisible);
            Assert.Equal(0, candidateHost.Opacity);
            Assert.False(candidateHost.IsHitTestVisible);
            Assert.Equal(0, runtime.RecentTranscriptReadCount);
            Assert.Equal([NotifyCollectionChangedAction.Reset], messageChanges);
            Assert.Equal([NotifyCollectionChangedAction.Reset], itemChanges);
            Assert.Equal(
                writesBeforePreparation + 1,
                GetCoordinatorProgrammaticOffsetWrites(coordinator));
            Assert.Equal(parallelTurn.Items.Count, viewModel.Messages.Count);
            Assert.All(parallelTurn.Items, item => Assert.Contains(
                viewModel.Messages,
                row => string.Equals(
                    row.AnchorKey.ToString(),
                    TranscriptRowAnchorKey.Tool(parallelTurn, item).ToString(),
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
            var targetPresenter = Assert.IsAssignableFrom<Control>(
                repeater.TryGetElement(targetIndex));
            Assert.Equal(nameof(TranscriptRowPresenter), targetPresenter.GetType().Name);
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

            await Task.Delay(TimeSpan.FromMilliseconds(2100));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

            Assert.True(latestRow.IsNavigationTargetHighlighted);
            Assert.True(latestRow.IsNavigationTargetFading);
            Assert.Equal("Search result target", latestRow.NavigationHighlightHelpText);
            Assert.Equal(
                "Search result target",
                Avalonia.Automation.AutomationProperties.GetHelpText(latestPresenter));
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
        var catalog = new RegressionTestExtensionCatalog();
        catalog.AddProvider(AgentRpcServices.RuntimeCatalogs, runtime, "test.runtime");
        var viewModel = new SubsessionsViewModel(catalog);
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
        services.ExtensionCatalog.AddProvider(
            AgentRpcServices.RuntimeCatalogs,
            new AgentRuntimeCatalog(services.SessionService, profileService, services.WorkspaceService));

        var viewModel = new SubsessionsViewModel(services.ExtensionCatalog);
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
                return new ToolExpansionFrame(
                    Assert.IsType<Point>(header.TranslatePoint(default, transcript)).Y,
                    CaptureFollowingRowPositions(
                        viewModel.TranscriptItems,
                        repeater,
                        transcript,
                        toolIndex,
                        static row => row.AnchorKey));
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
            Assert.True(viewModel.HasNewerTranscriptRows);
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
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            AssertSubsessionFollowingParity(coordinator, viewModel, expected: true);
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

    [AvaloniaFact]
    public async Task AgentChatView_NavigationPresentsRetryableStartupErrorWithoutHostFault()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        var workspaceGateway = new FailOnceWorkspaceGateway(services.WorkspaceService);
        using var view = new AgentChatView(
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
            "sunder.package.agent.chat",
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

    [AvaloniaFact]
    public async Task ProfilesView_DisposeStopsOwnedViewModelFromReloading()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Initial profile");
        var view = new AgentProfilesView(profileService);
        var viewModel = Assert.IsType<AgentProfilesViewModel>(view.DataContext);
        await view.OnNavigatedToAsync(new PackageViewNavigationContext(
            "sunder.package.agent.profiles",
            new Dictionary<string, string?>()));
        await WaitUntilAsync(() => viewModel.Profiles.Count > 0 && !viewModel.IsBusy);
        var profileCount = viewModel.Profiles.Count;

        view.Dispose();
        await profileService.CreateProfileAsync("Created after disposal");
        await Task.Delay(50);

        Assert.Null(view.DataContext);
        Assert.Equal(profileCount, viewModel.Profiles.Count);
    }

    [AvaloniaFact]
    public async Task WorkspacesView_DisposeStopsOwnedViewModelFromReloading()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        services.WorkspaceService.CreateWorkspace("Initial workspace");
        var warmup = new AgentExecutionTargetWarmupService(services.WorkspaceService, services.TargetService);
        var view = new AgentWorkspacesView(
            services.WorkspaceService,
            services.TargetService,
            services.ExtensionCatalog,
            warmup);
        var viewModel = Assert.IsType<AgentWorkspacesViewModel>(view.DataContext);
        await view.OnNavigatedToAsync(new PackageViewNavigationContext(
            "sunder.package.agent.workspaces",
            new Dictionary<string, string?>()));
        var workspaceCount = viewModel.Workspaces.Count;

        view.Dispose();
        services.WorkspaceService.CreateWorkspace("Created after disposal");

        Assert.Null(view.DataContext);
        Assert.Equal(workspaceCount, viewModel.Workspaces.Count);
    }

    [AvaloniaFact]
    public async Task MemoryInspectorView_DisposeCancelsOwnedViewModelLoad()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        var profile = await profileService.CreateProfileAsync("Memory profile");
        profileService.SaveProfile(
            profile.ProfileId,
            profile.DisplayName,
            profile.Description,
            profile.Instructions,
            profile.ChatProviderId,
            profile.ChatModelId,
            "blocking-embeddings",
            "semantic-v1");
        var workspace = services.WorkspaceService.CreateWorkspace("Memory workspace");
        services.SessionService.CreateSession(
            "Memory session",
            profileId: profile.ProfileId,
            workspaceId: workspace.WorkspaceId);
        services.ExtensionCatalog.AddProvider(
            AgentRpcServices.RuntimeCatalogs,
            new AgentRuntimeCatalog(services.SessionService, profileService, services.WorkspaceService));
        var embeddingProvider = new BlockingEmbeddingProvider();
        services.ExtensionCatalog.AddProvider(AgentRpcServices.EmbeddingProviders, embeddingProvider);
        var inspector = CreateMemoryInspector(scope.Context, services.ExtensionCatalog);
        var viewModel = (MemoryInspectorViewModel)Activator.CreateInstance(
            typeof(MemoryInspectorViewModel),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            [inspector],
            culture: null)!;
        var view = new MemoryInspectorView(viewModel);
        var navigation = view.OnNavigatedToAsync(new PackageViewNavigationContext(
            "sunder.package.agent.memory.semantic.inspector",
            new Dictionary<string, string?>())).AsTask();
        await embeddingProvider.ReadinessStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        view.Dispose();
        await embeddingProvider.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => navigation);

        Assert.Null(view.DataContext);
        Assert.Equal("Loading semantic status...", viewModel.SemanticStatusText);
    }

    [AvaloniaFact]
    public async Task McpView_DisposeStopsOwnedViewModelFromReloading()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new McpServerCatalogService(scope.Context);
        await using var connections = new McpClientConnectionManager(NullLoggerFactory.Instance);
        var viewModel = new AgentMcpSettingsViewModel(
            new McpSettingsEditorService(catalog),
            new McpConfigurationCoordinator(new McpEcosystemConfigurationImporter(catalog), syncService: null),
            new McpServerConnectionService(catalog, connections, oauthService: null),
            new McpOAuthCoordinator(oauthService: null, connections));
        Assert.Same(viewModel.InitializeAsync(), viewModel.InitializeAsync());
        await viewModel.InitializeAsync();
        var view = new AgentMcpSettingsView(viewModel);

        view.Dispose();
        view.Dispose();
        await catalog.SaveServerAsync(CreateMcpServer("created-after-disposal"), EmptyValues, EmptyValues);
        await Task.Delay(50);

        Assert.Null(view.DataContext);
        Assert.Empty(viewModel.Servers);
    }

    [AvaloniaFact]
    public async Task SkillsView_DisposeStopsOwnedViewModelFromReloading()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new SkillStore(scope.Context);
        var importer = new SkillImportService(store, new NoOpGitHubSkillClient(), scope.Context);
        var viewModel = new SkillSettingsViewModel(store, importer);
        Assert.Same(viewModel.InitializeAsync(), viewModel.InitializeAsync());
        await viewModel.InitializeAsync();
        var view = new SkillSettingsView(viewModel);

        view.Dispose();
        view.Dispose();
        store.SaveSkill(CreateSkill("created-after-disposal"));

        Assert.Null(view.DataContext);
        Assert.Empty(viewModel.Skills);
    }

    [AvaloniaFact]
    public async Task SubagentsView_DisposeStopsOwnedViewModelFromReloading()
    {
        using var scope = RegressionTestPackageScope.Create();
        var service = new SubagentService(new SubagentStore(scope.Context));
        service.CreateSubagent("Initial subagent");
        var viewModel = new SubagentsViewModel(service, new RegressionTestExtensionCatalog());
        await viewModel.InitializeAsync();
        var view = new SubagentsView(viewModel);
        var subagentCount = viewModel.Subagents.Count;

        view.Dispose();
        view.Dispose();
        service.CreateSubagent("Created after disposal");

        Assert.Null(view.DataContext);
        Assert.Equal(subagentCount, viewModel.Subagents.Count);
    }

    [AvaloniaFact]
    public async Task SubsessionsView_DisposeStopsOwnedViewModelFromReloading()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        var workspace = services.WorkspaceService.CreateWorkspace("Subsession workspace");
        var parent = services.SessionService.CreateSession("Parent", workspaceId: workspace.WorkspaceId);
        services.SessionService.CreateSession("Initial child", parentSessionId: parent.SessionId);
        services.ExtensionCatalog.AddProvider(
            AgentRpcServices.RuntimeCatalogs,
            new AgentRuntimeCatalog(services.SessionService, profileService, services.WorkspaceService));
        var viewModel = new SubsessionsViewModel(services.ExtensionCatalog);
        await viewModel.InitializeAsync();
        var view = new SubsessionsView(viewModel);
        var subsessionCount = viewModel.Subsessions.Count;

        view.Dispose();
        view.Dispose();
        services.SessionService.CreateSession("Created after disposal", parentSessionId: parent.SessionId);

        Assert.Null(view.DataContext);
        Assert.Equal(subsessionCount, viewModel.Subsessions.Count);
    }

    [AvaloniaFact]
    public async Task SkillsInitialization_MalformedIndexIsPresented()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new SkillStore(scope.Context);
        await File.WriteAllTextAsync(
            scope.Context.Storage.RoleLocalWorkspace.GetLocalPath("skills/skills.json"),
            "{ malformed");
        var viewModel = new SkillSettingsViewModel(
            store,
            new SkillImportService(store, new NoOpGitHubSkillClient(), scope.Context));

        await viewModel.InitializeAsync();

        Assert.Equal(SkillStatusKind.Error, viewModel.StatusKind);
        Assert.Contains("malformed", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        viewModel.Dispose();
    }

    [AvaloniaFact]
    public async Task SubagentInitialization_MalformedIndexIsPresented()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new SubagentStore(scope.Context);
        await File.WriteAllTextAsync(
            scope.Context.Storage.RoleLocalWorkspace.GetLocalPath("subagents/subagents.json"),
            "{ malformed");
        var viewModel = new SubagentsViewModel(
            new SubagentService(store),
            new RegressionTestExtensionCatalog());

        await viewModel.InitializeAsync();

        Assert.Equal(SubagentStatusKind.Error, viewModel.StatusKind);
        Assert.Contains("subagent", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        viewModel.Dispose();
    }

    [AvaloniaFact]
    public async Task SubsessionInitialization_CatalogFailureIsPresented()
    {
        using var catalog = new AgentRpcCatalog(new ThrowingRpcClient());
        var viewModel = new SubsessionsViewModel(catalog);

        await viewModel.InitializeAsync();

        Assert.Contains("Injected catalog failure", viewModel.StatusText, StringComparison.Ordinal);
        viewModel.Dispose();
    }

    [AvaloniaFact]
    public async Task SubsessionsView_OrdinaryInitializationFailureOpensWithVisibleRetry()
    {
        using var catalog = new AgentRpcCatalog(new ThrowingRpcClient());
        var viewModel = new SubsessionsViewModel(catalog);
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
        using var catalog = new AgentRpcCatalog(new ThrowingRpcClient());
        var viewModel = new SubsessionsViewModel(catalog);
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
        services.ExtensionCatalog.AddProvider(
            AgentRpcServices.RuntimeCatalogs,
            new AgentRuntimeCatalog(services.SessionService, profileService, services.WorkspaceService));
        var viewModel = new SubsessionsViewModel(services.ExtensionCatalog);
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

    [AvaloniaFact]
    public void SecretFieldStyle_MasksProviderApiKeyInput()
    {
        var view = new OpenAiSettingsView();
        var secretField = new TextBox();
        secretField.Classes.Add("secret-field");
        view.Content = secretField;
        var window = new Window { Content = view };

        window.Show();
        Assert.Equal('*', secretField.PasswordChar);
        window.Close();
    }

    [AvaloniaFact]
    public void OpenAiSettingsView_HasBoundAuthorizeButton()
    {
        using var scope = RegressionTestPackageScope.Create();
        var viewModel = new OpenAiSettingsViewModel(scope.Context, NullPackageRuntimeClient.Instance);
        using var view = new OpenAiSettingsView(viewModel);

        var button = view.FindControl<Button>("AuthorizeButton");

        Assert.NotNull(button);
        Assert.Same(viewModel.AuthorizeCommand, button.Command);
        Assert.Equal("Authorize with ChatGPT Plus/Pro", button.Content);
    }

    [AvaloniaFact]
    public async Task OpenAiSettingsView_InitializesPersistedRuntimeAuthStatus()
    {
        using var scope = RegressionTestPackageScope.Create();
        var runtime = new ConnectedOpenAiRuntimeClient();
        var viewModel = new OpenAiSettingsViewModel(scope.Context, runtime);
        using var view = new OpenAiSettingsView(viewModel);

        Assert.Equal(0, runtime.InvocationCount);
        var lifecycle = Assert.IsAssignableFrom<IPackageViewNavigationPreparationTarget>(view.DataContext);
        Assert.True(await lifecycle.PrepareNavigationAsync(new PackageViewNavigationContext(
            "settings:sunder.package.agent.provider.openai",
            new Dictionary<string, string?>())));

        Assert.True(runtime.InvocationCount > 0);
        Assert.Equal("Connected, active", viewModel.CodexStatusLabel);
        Assert.True(viewModel.CanDisconnectAction);
        Assert.Equal("Reauthorize with ChatGPT Plus/Pro", viewModel.AuthorizationButtonLabel);
    }

    private static AgentServices CreateAgentServices(RegressionTestPackageScope scope)
    {
        var extensionCatalog = new RegressionTestExtensionCatalog();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store, extensionCatalog);
        var workspaceService = new AgentWorkspaceService(store, extensionCatalog, sessionService);
        var targetService = new AgentExecutionTargetService(extensionCatalog);
        var toolService = new AgentToolService(
            sessionService,
            workspaceService,
            targetService,
            extensionCatalog);
        return new AgentServices(
            extensionCatalog,
            sessionService,
            workspaceService,
            targetService,
            new AgentProfileService(
                store,
                toolService,
                extensionCatalog,
                extensionCatalog.BehaviorLoops));
    }

    private static ServiceCollection CreateHistoryAppServices(
        RegressionTestPackageScope scope,
        IPackageRuntimeClient runtime)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(scope.Context);
        services.AddSingleton(runtime);
        services.AddSingleton<IPackageRuntimeClient>(runtime);
        services.AddSingleton<AgentRpcCatalog>(new RegressionTestExtensionCatalog());
        services.AddSingleton<IPackageShellViewService, NoOpPackageShellViewService>();
        services.AddSingleton<IPackageNotificationService>(NullPackageNotificationService.Instance);
        services.AddSingleton<IBackgroundProcessQueue, NoOpBackgroundProcessQueue>();
        new Sunder.Package.Agent.AppPackageModule().ConfigureAppServices(services, scope.Context);
        return services;
    }

    private static AgentChatView CreateAgentChatView(
        RegressionTestPackageScope scope,
        AgentServices services,
        AgentProfileService profileService)
        => new(
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

    private static IEnumerable<Border> FindToolDetailCards(Control root)
        => root.GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.Classes.Contains("tool-output-card"));

    private static readonly IReadOnlyDictionary<string, string> EmptyValues =
        new Dictionary<string, string>();

    private static ConfiguredMcpServerRecord CreateMcpServer(string id) => new()
    {
        ServerId = id,
        Name = id,
        DisplayName = id,
        IsEnabled = true,
        TransportType = ConfiguredMcpTransportType.HttpSse,
        EndpointUrl = $"https://{id}.example/mcp",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static InstalledSkillRecord CreateSkill(string id)
    {
        var now = DateTimeOffset.UtcNow;
        return new InstalledSkillRecord(
            id,
            $"skills/{id}",
            id,
            null,
            null,
            null,
            "local",
            null,
            null,
            null,
            id,
            now,
            now,
            new Dictionary<string, string>(),
            []);
    }

    private static MemoryInspectorService CreateMemoryInspector(
        IPackageContext context,
        AgentRpcCatalog extensionCatalog)
    {
        var store = new MemoryLocalStore(context);
        var settings = new MemorySemanticSettingsService(context);
        var metrics = new SemanticMemoryMetricsService();
        var resolver = new SemanticModelRuntimeResolver(extensionCatalog, settings);
        var retrieval = new SemanticMemoryRetrievalBackend(store, resolver, settings);
        var worker = new SemanticMemoryIndexingBackgroundService(store, resolver, settings, retrieval, metrics);
        return new MemoryInspectorService(store, retrieval, worker, resolver, metrics);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        Func<string>? timeoutDiagnostic = null)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= timeout)
            {
                var diagnostic = timeoutDiagnostic?.Invoke();
                throw new TimeoutException(
                    string.IsNullOrWhiteSpace(diagnostic)
                        ? "Timed out waiting for view-model state."
                        : $"Timed out waiting for view-model state. {diagnostic}");
            }

            await Task.Delay(10);
        }
    }

    private static async Task WaitForVisibleMarkdownToSettleAsync(Control root)
    {
        for (var pass = 0; pass < 100; pass++)
        {
            await Dispatcher.UIThread.InvokeAsync(
                () => (TopLevel.GetTopLevel(root) as Window)?.UpdateLayout(),
                DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            var pending = root.GetVisualDescendants()
                .OfType<Control>()
                .Where(control => control.GetType().Name == nameof(StreamingMarkdownPresenter)
                                  && control.IsEffectivelyVisible)
                .Any(control => Assert.IsType<bool>(control.GetType().GetProperty(
                        "IsRenderPending",
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic)!.GetValue(control)));
            if (!pending)
            {
                return;
            }
        }

        Assert.Fail("Visible Markdown did not settle before the tool expansion trace began.");
    }

    private sealed record AgentServices(
        RegressionTestExtensionCatalog ExtensionCatalog,
        AgentSessionService SessionService,
        AgentWorkspaceService WorkspaceService,
        AgentExecutionTargetService TargetService,
        AgentProfileService ProfileService);

    private sealed class ConnectedOpenAiRuntimeClient : IPackageRuntimeClient
    {
        private readonly DateTimeOffset _expiresAtUtc = DateTimeOffset.UtcNow.AddHours(1);

        public bool IsAvailable => true;

        public int InvocationCount { get; private set; }

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            var response = operation.OperationId switch
            {
                "provider.credential.query.v1" => Activator.CreateInstance(
                    typeof(TResponse),
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic,
                    binder: null,
                    args: [true],
                    culture: null),
                "openai.auth.v1" => Activator.CreateInstance(
                    typeof(TResponse),
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic,
                    binder: null,
                    args: [true, true, _expiresAtUtc, null],
                    culture: null),
                _ => throw new InvalidOperationException($"Unexpected runtime operation '{operation.OperationId}'."),
            };
            return ValueTask.FromResult(Assert.IsType<TResponse>(response));
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FailOnceWorkspaceGateway(IAgentWorkspaceGateway inner)
        : IAgentWorkspaceGateway
    {
        private int _initializeCount;

        public int InitializeCount => Volatile.Read(ref _initializeCount);
        public TaskCompletionSource FirstInitializationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FailFirstInitialization { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action? WorkspacesChanged
        {
            add => inner.WorkspacesChanged += value;
            remove => inner.WorkspacesChanged -= value;
        }
        public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => inner.ListWorkspaces();
        public AgentWorkspaceRecord? GetWorkspace(string workspaceId) => inner.GetWorkspace(workspaceId);
        public AgentWorkspaceRecord CreateWorkspace(string displayName) => inner.CreateWorkspace(displayName);
        public void SaveWorkspace(string workspaceId, string displayName, string? description)
            => inner.SaveWorkspace(workspaceId, displayName, description);
        public void SaveWorkspaceAggregate(
            string workspaceId,
            string displayName,
            string? description,
            IReadOnlyList<AgentWorkspacePathRecord> paths,
            IReadOnlyList<AgentWorkspaceDocumentRecord> documents,
            string? executionTargetId)
            => inner.SaveWorkspaceAggregate(
                workspaceId,
                displayName,
                description,
                paths,
                documents,
                executionTargetId);
        public void DeleteWorkspace(string workspaceId) => inner.DeleteWorkspace(workspaceId);
        public IReadOnlyList<AgentWorkspaceBindingRecord> ListBindings(string workspaceId)
            => inner.ListBindings(workspaceId);
        public AgentWorkspaceBindingRecord SavePrimaryExecutionBinding(
            string workspaceId,
            string contributionId,
            string displayRole = AgentWorkspaceBindingRoles.PrimaryExecutionTarget)
            => inner.SavePrimaryExecutionBinding(workspaceId, contributionId, displayRole);
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _initializeCount) == 1)
            {
                FirstInitializationStarted.TrySetResult();
                await FailFirstInitialization.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("Injected startup failure.");
            }

            await inner.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private class CountingSessionGateway(IAgentSessionGateway inner)
        : IAgentSessionGateway,
            IAgentTranscriptToolDetailGateway
    {
        public int RecentTranscriptReadCount { get; private set; }

        public event Action<Guid>? SessionChanged
        {
            add => inner.SessionChanged += value;
            remove => inner.SessionChanged -= value;
        }

        public event Action<Guid, AgentTurnRecord>? TurnChanged
        {
            add => inner.TurnChanged += value;
            remove => inner.TurnChanged -= value;
        }

        public event Action<Guid>? TranscriptReset
        {
            add => inner.TranscriptReset += value;
            remove => inner.TranscriptReset -= value;
        }

        public event Action<Guid, AgentRunActivityUpdate>? RunActivityChanged
        {
            add => inner.RunActivityChanged += value;
            remove => inner.RunActivityChanged -= value;
        }

        public IReadOnlyList<AgentSessionRecord> ListSessions() => inner.ListSessions();
        public IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(string workspaceId)
            => inner.ListSessionsForWorkspace(workspaceId);
        public AgentSessionRecord CreateSession(string title, Guid? parentSessionId = null, Guid? rootSessionId = null,
            Guid? parentRunId = null, long? parentRunRevision = null, string? parentToolCallId = null,
            string? taskId = null, string? profileId = null, string? behaviorLoopId = null,
            string? agentKind = null, string? workspaceId = null)
            => inner.CreateSession(title, parentSessionId, rootSessionId, parentRunId, parentRunRevision,
                parentToolCallId, taskId, profileId, behaviorLoopId, agentKind, workspaceId);
        public AgentSessionRecord? GetSession(Guid sessionId) => inner.GetSession(sessionId);
        public void UpdateSession(AgentSessionRecord session) => inner.UpdateSession(session);
        public void DeleteSession(Guid sessionId) => inner.DeleteSession(sessionId);
        public IReadOnlyList<AgentTurnRecord> ListTurns(Guid sessionId) => inner.ListTurns(sessionId);
        public virtual IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit)
        {
            RecentTranscriptReadCount++;
            return inner.ListRecentTurns(sessionId, limit);
        }
        public virtual IReadOnlyList<AgentTurnRecord> ListTurnsBefore(
            Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit)
            => inner.ListTurnsBefore(sessionId, beforeCreatedAtUtc, beforeTurnId, limit);
        public virtual IReadOnlyList<AgentTurnRecord> ListTurnsAfter(
            Guid sessionId, DateTimeOffset afterCreatedAtUtc, Guid afterTurnId, int limit)
            => inner.ListTurnsAfter(sessionId, afterCreatedAtUtc, afterTurnId, limit);
        public AgentTurnRecord? GetTurn(Guid turnId) => inner.GetTurn(turnId);
        public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => inner.GetLatestCheckpoint(sessionId);
        public Task<AgentTranscriptToolDetailRecord?> LoadToolDetailAsync(
            AgentTranscriptToolDetailRequest request,
            CancellationToken cancellationToken = default)
            => inner is IAgentTranscriptToolDetailGateway detailGateway
                ? detailGateway.LoadToolDetailAsync(request, cancellationToken)
                : Task.FromResult<AgentTranscriptToolDetailRecord?>(null);
    }

    private sealed class BlockingHistoryNavigationRuntimeClient(
        AgentProfileRecord profile,
        AgentWorkspaceRecord workspace,
        AgentSessionRecord session,
        IReadOnlyList<AgentTurnRecord> turns) : IPackageRuntimeClient
    {
        private int _aroundLoadCount;
        private int _chatSnapshotLoadCount;
        private int _transcriptPageLoadCount;

        public bool IsAvailable => true;

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

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            return operation.OperationId switch
            {
                "agent.chat.snapshot.v1" => new ValueTask<TResponse>(
                    CreateChatSnapshot<TRequest, TResponse>(request)),
                "agent.transcript.around.v1" => new ValueTask<TResponse>(
                    LoadAroundTurnAsync<TRequest, TResponse>(request, cancellationToken)),
                "agent.transcript.page.v1" => new ValueTask<TResponse>(
                    CreateTranscriptPage<TResponse>()),
                "agent.workspaces.command.v1" => new ValueTask<TResponse>(CreateResponse<TResponse>(new
                {
                    Revision = 1L,
                    Workspace = (object?)null,
                    Warmup = AgentExecutionTargetWarmupResult.Skipped(
                        "No execution target is required for this test."),
                })),
                "agent.dashboard.v1" => new ValueTask<TResponse>(CreateResponse<TResponse>(new
                {
                    Revision = 1L,
                    Profiles = new[] { profile },
                    Workspaces = new[] { workspace },
                    WorkspaceBindings = Array.Empty<AgentWorkspaceBindingRecord>(),
                })),
                _ => throw new NotSupportedException(operation.OperationId),
            };
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
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

    private sealed class BlockingRuntimeClient : IPackageRuntimeClient
    {
        private int _invocationCount;
        private int _subscriptionCount;

        public bool IsAvailable => true;
        public int InvocationCount => Volatile.Read(ref _invocationCount);
        public int SubscriptionCount => Volatile.Read(ref _subscriptionCount);

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            Interlocked.Increment(ref _invocationCount);
            return new ValueTask<TResponse>(WaitForCancellationAsync<TResponse>(cancellationToken));
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            Interlocked.Increment(ref _subscriptionCount);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        private static async Task<T> WaitForCancellationAsync<T>(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Cancellation was expected.");
        }
    }


    private static void AssertTranscriptTailGeometry(
        AgentChatView view,
        AgentChatViewModel viewModel)
    {
        var repeater = Assert.IsType<ItemsRepeater>(
            view.FindControl<ItemsRepeater>("TranscriptItemsControl"));
        var transcript = GetTranscriptScrollViewer(view);
        var content = Assert.IsType<Border>(view.FindControl<Border>("TranscriptScrollContent"));
        var activityIndex = viewModel.TranscriptItems.Count - 2;
        var tailIndex = viewModel.TranscriptItems.Count - 1;
        var activity = Assert.IsAssignableFrom<Control>(repeater.TryGetElement(activityIndex));
        var tail = Assert.IsAssignableFrom<Control>(repeater.TryGetElement(tailIndex));
        Assert.Same(viewModel.RunActivityRow, activity.DataContext);
        Assert.Same(viewModel.TranscriptItems[^1], tail.DataContext);
        Assert.False(tail.IsHitTestVisible);
        Assert.Equal(1, tail.Bounds.Height, precision: 3);
        Assert.Same(tail, transcript.CurrentAnchor);

        var signedDistance = transcript.Extent.Height - transcript.Viewport.Height - transcript.Offset.Y;
        Assert.InRange(signedDistance, -1, 1);
        var tailTop = Assert.IsType<Point>(tail.TranslatePoint(default, transcript)).Y;
        Assert.InRange(
            Math.Abs(tailTop + tail.Bounds.Height + content.Padding.Bottom - transcript.Viewport.Height),
            0,
            1);
        var activityTop = Assert.IsType<Point>(activity.TranslatePoint(default, repeater)).Y;
        var tailRepeaterTop = Assert.IsType<Point>(tail.TranslatePoint(default, repeater)).Y;
        Assert.InRange(Math.Abs(tailRepeaterTop + tail.Bounds.Height - repeater.Bounds.Height), 0, 1);

        if (viewModel.Messages.Count > 0)
        {
            var finalTransient = Assert.IsAssignableFrom<Control>(
                repeater.TryGetElement(viewModel.Messages.Count - 1));
            var finalTransientTop = Assert.IsType<Point>(
                finalTransient.TranslatePoint(default, repeater)).Y;
            var finalTransientBottom = finalTransientTop + finalTransient.Bounds.Height;
            if (activity.IsVisible)
            {
                Assert.InRange(Math.Abs(finalTransientBottom - activityTop), 0, 1);
                Assert.InRange(Math.Abs(activityTop + activity.Bounds.Height - tailRepeaterTop), 0, 1);
            }
            else
            {
                Assert.InRange(Math.Abs(finalTransientBottom - tailRepeaterTop), 0, 1);
            }
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

    private static void AssertFollowingRowsMoveMonotonically(
        IReadOnlyList<IReadOnlyDictionary<object, double>> frames,
        bool expanding,
        IReadOnlyList<string>? details)
    {
        var comparedFollowingRows = 0;
        foreach (var (previous, current) in frames.Zip(frames.Skip(1)))
        {
            foreach (var (key, previousY) in previous)
            {
                if (!current.TryGetValue(key, out var currentY))
                {
                    continue;
                }

                comparedFollowingRows++;
                var delta = currentY - previousY;
                Assert.True(
                    expanding ? delta >= -0.1 : delta <= 0.1,
                    $"A realized row below the tool moved in the wrong direction ({key}: {previousY:F3} -> {currentY:F3}).{Environment.NewLine}{string.Join(Environment.NewLine, details ?? [])}");
            }
        }
        Assert.True(
            comparedFollowingRows > 0,
            $"No continuously realized row below the tool was observed.{Environment.NewLine}{string.Join(Environment.NewLine, details ?? [])}");
    }

    private static IReadOnlyDictionary<object, double> CaptureFollowingRowPositions<TRow>(
        IReadOnlyList<TRow> transcriptItems,
        ItemsRepeater repeater,
        ScrollViewer transcript,
        int anchorIndex,
        Func<TRow, object> getAnchorKey)
    {
        var positions = new Dictionary<object, double>();
        for (var index = anchorIndex + 1; index < transcriptItems.Count; index++)
        {
            if (repeater.TryGetElement(index) is not Control row
                || row.TranslatePoint(default, transcript) is not { } point)
            {
                continue;
            }

            positions[getAnchorKey(transcriptItems[index])] = point.Y;
        }

        return positions;
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

    private sealed record ToolExpansionFrame(
        double HeaderY,
        IReadOnlyDictionary<object, double> FollowingRowPositions);

    private static bool GetAnchorHostSuspended(AgentChatView view)
    {
        var host = Assert.IsAssignableFrom<Control>(view.FindControl<Control>("TranscriptAnchorHost"));
        return Assert.IsType<bool>(host.GetType()
            .GetProperty(
                "IsAnchoringSuspended",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(host));
    }

    private static bool GetAnchorHostSuspended(SubsessionsView view)
    {
        var host = Assert.IsAssignableFrom<Control>(view.FindControl<Control>("TranscriptAnchorHost"));
        return Assert.IsType<bool>(host.GetType()
            .GetProperty(
                "IsAnchoringSuspended",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(host));
    }

    private static object? GetRepeaterMadeAnchor(ItemsRepeater repeater)
    {
        var viewportManager = typeof(ItemsRepeater)
            .GetField(
                "_viewportManager",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(repeater)!;
        return viewportManager.GetType()
            .GetField(
                "_makeAnchorElement",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(viewportManager);
    }

    private static Task GetSettledScrollOperation(AgentChatView view)
    {
        var coordinator = GetTranscriptScrollCoordinator(view);
        return Assert.IsAssignableFrom<Task>(coordinator.GetType()
            .GetField("_settledScrollOperation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator));
    }

    private static Task GetPendingCoordinatorOperations(AgentChatView view)
    {
        var coordinator = GetTranscriptScrollCoordinator(view);
        return Assert.IsAssignableFrom<Task>(coordinator.GetType()
            .GetProperty(
                "PendingPagingOperations",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator));
    }

    private static Task GetViewportMutationCompletionOperation(object coordinator)
        => Assert.IsAssignableFrom<Task>(coordinator.GetType()
            .GetField(
                "_viewportMutationCompletionOperation",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator));

    private static bool InvokeQueueLoadOlderRows(object coordinator)
        => Assert.IsType<bool>(coordinator.GetType()
            .GetMethod(
                "QueueLoadOlderRows",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(coordinator, null));

    private static bool InvokeQueueLoadNewerRows(object coordinator)
        => Assert.IsType<bool>(coordinator.GetType()
            .GetMethod(
                "QueueLoadNewerRows",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(coordinator, [false]));

    private static Task GetPendingCoordinatorOperations(SubsessionsView view)
    {
        var coordinator = GetTranscriptScrollCoordinator(view);
        return Assert.IsAssignableFrom<Task>(coordinator.GetType()
            .GetProperty(
                "PendingPagingOperations",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator));
    }

    private static async Task WaitForPendingCoordinatorOperationsAsync(
        AgentChatView view,
        TimeSpan? timeout = null)
    {
        try
        {
            await GetPendingCoordinatorOperations(view).WaitAsync(
                timeout ?? TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException exception)
        {
            var coordinator = GetTranscriptScrollCoordinator(view);
            var snapshot = coordinator.GetType()
                .GetProperty(
                    "DiagnosticSnapshot",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(coordinator);
            throw new TimeoutException($"Transcript coordinator did not settle: {snapshot}", exception);
        }
    }

    private static async Task WaitForPendingCoordinatorOperationsAsync(SubsessionsView view)
    {
        try
        {
            await GetPendingCoordinatorOperations(view).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException exception)
        {
            var coordinator = GetTranscriptScrollCoordinator(view);
            var snapshot = coordinator.GetType()
                .GetProperty(
                    "DiagnosticSnapshot",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(coordinator);
            throw new TimeoutException($"Subsession transcript coordinator did not settle: {snapshot}", exception);
        }
    }

    private static PackageViewNavigationContext CreateSubsessionNavigation(
        Guid sessionId,
        Guid turnId,
        Guid itemId,
        string anchorKind,
        DateTimeOffset? createdAtUtc = null,
        string? callId = null)
        => new(
            SubagentConstants.SubsessionsViewId,
            new Dictionary<string, string?>
            {
                ["sessionId"] = sessionId.ToString("D"),
                ["turnId"] = turnId.ToString("D"),
                ["itemId"] = itemId.ToString("D"),
                ["callId"] = callId,
                ["anchorKind"] = anchorKind,
                ["createdAtUtc"] = (createdAtUtc ?? DateTimeOffset.UtcNow).ToString("O"),
            });

    private static (
        SubsessionsViewModel ViewModel,
        SubsessionsView View,
        Window Window,
        AgentSessionRecord Child,
        AgentTurnRecord[] Turns) CreateSubsessionHistoryHighlightHarness()
    {
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
        catalog.AddProvider(AgentRpcServices.RuntimeCatalogs, runtime, "test.runtime");
        var viewModel = new SubsessionsViewModel(catalog);
        var view = new SubsessionsView(viewModel);
        var window = new Window { Width = 900, Height = 500, Content = view };
        window.Show();
        return (viewModel, view, window, child, turns);
    }

    private static object GetTranscriptScrollCoordinator(AgentChatView view)
    {
        var behavior = GetTranscriptBehavior(view);
        return behavior.GetType()
            .GetField("_scrollCoordinator", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(behavior)!;
    }

    private static object GetTranscriptScrollCoordinator(SubsessionsView view)
    {
        var behavior = GetTranscriptBehavior(view);
        return behavior.GetType()
            .GetField("_scrollCoordinator", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(behavior)!;
    }

    private static object GetTranscriptBehavior(AgentChatView view)
        => typeof(AgentChatView)
            .GetField("_transcriptBehavior", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(view)!;

    private static object GetTranscriptBehavior(SubsessionsView view)
        => typeof(SubsessionsView)
            .GetField("_transcriptBehavior", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(view)!;

    private static bool GetPrivateBoolean(object instance, string fieldName)
        => Assert.IsType<bool>(instance.GetType()
            .GetField(
                fieldName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(instance));

    private static Task GetPrivateTask(object instance, string fieldName)
        => Assert.IsAssignableFrom<Task>(instance.GetType()
            .GetField(
                fieldName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(instance));

    private static object? GetPrivateField(object instance, string fieldName)
        => instance.GetType()
            .GetField(
                fieldName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(instance);

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

    private static async Task WaitForTranscriptGeometrySettledAsync(
        Window window,
        ScrollViewer transcript,
        object coordinator)
    {
        const int maximumPasses = 100;
        const int requiredStablePasses = 2;
        (double Offset, double Extent, double Viewport)? previous = null;
        var stablePasses = 0;
        for (var pass = 0; pass < maximumPasses; pass++)
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            var current = (
                Offset: transcript.Offset.Y,
                Extent: transcript.Extent.Height,
                Viewport: transcript.Viewport.Height);
            var renderedContentPending = GetCoordinatorRenderedContentPending(coordinator);
            if (!renderedContentPending
                && previous is { } prior
                && Math.Abs(current.Offset - prior.Offset) <= 0.01
                && Math.Abs(current.Extent - prior.Extent) <= 0.01
                && Math.Abs(current.Viewport - prior.Viewport) <= 0.01)
            {
                stablePasses++;
                if (stablePasses >= requiredStablePasses)
                {
                    return;
                }
            }
            else
            {
                stablePasses = 0;
            }

            previous = current;
            await Task.Delay(1);
        }

        throw new TimeoutException(
            $"Transcript geometry did not settle: {GetCoordinatorDiagnosticSnapshot(coordinator)}");
    }

    private static bool GetCoordinatorRenderedContentPending(object coordinator)
    {
        var snapshot = GetCoordinatorDiagnosticSnapshot(coordinator);
        return Assert.IsType<bool>(snapshot.GetType()
            .GetProperty("RenderedContentPending")!
            .GetValue(snapshot));
    }

    private static object GetCoordinatorDiagnosticSnapshot(object coordinator)
        => coordinator.GetType()
            .GetProperty(
                "DiagnosticSnapshot",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator)!;

    private static void RecordDiagnostic(List<string> trace, string entry)
    {
        const int maximumEntries = 128;
        if (trace.Count == maximumEntries)
        {
            trace.RemoveAt(0);
        }
        trace.Add(entry);
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

    private static string DescribeSubsessionTranscriptState(
        SubsessionsView view,
        ScrollViewer transcript,
        object coordinator,
        string stage)
        => DescribeTranscriptState(
            stage,
            transcript,
            coordinator,
            GetTranscriptBehavior(view),
            GetPrivateField(view, "_navigationGeneration"));

    private static string DescribeTranscriptState(
        string stage,
        ScrollViewer transcript,
        object coordinator,
        object behavior,
        object? navigationGeneration)
    {
        var coordinatorSnapshot = GetCoordinatorDiagnosticSnapshot(coordinator);
        var behaviorSnapshot = behavior.GetType()
            .GetProperty(
                "DiagnosticSnapshot",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(behavior);
        return FormattableString.Invariant(
            $"{stage}: offset={transcript.Offset.Y:F3}, extent={transcript.Extent.Height:F3}, viewport={transcript.Viewport.Height:F3}, currentAnchor={transcript.CurrentAnchor?.DataContext?.GetType().Name ?? "null"}, navigationGeneration={navigationGeneration}, programmatic={GetPrivateBoolean(coordinator, "_isProgrammaticScroll")}, coordinator={coordinatorSnapshot}, placement={behaviorSnapshot}");
    }

    private static object? CaptureCurrentScrollAnchorKey(object coordinator)
        => coordinator.GetType()
            .GetMethod(
                "CaptureCurrentScrollAnchorKey",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(coordinator, null);

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

    private static long GetCoordinatorProgrammaticOffsetWrites(object coordinator)
    {
        var snapshot = coordinator.GetType()
            .GetProperty(
                "DiagnosticSnapshot",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator)!;
        return Assert.IsType<long>(snapshot.GetType()
            .GetProperty("ProgrammaticOffsetWrites")!
            .GetValue(snapshot));
    }

    private static long GetCoordinatorAuthorityRevision(object coordinator)
    {
        var snapshot = coordinator.GetType()
            .GetProperty(
                "DiagnosticSnapshot",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator)!;
        return Assert.IsType<long>(snapshot.GetType()
            .GetProperty("AuthorityRevision")!
            .GetValue(snapshot));
    }

    private static string? GetCoordinatorMutationStatus(object coordinator)
    {
        var snapshot = coordinator.GetType()
            .GetProperty(
                "DiagnosticSnapshot",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator)!;
        var mutation = snapshot.GetType().GetProperty("Mutation")!.GetValue(snapshot);
        return mutation?.GetType().GetProperty("Status")?.GetValue(mutation)?.ToString();
    }

    private static string? GetCoordinatorMutationMode(object coordinator)
    {
        var snapshot = GetCoordinatorDiagnosticSnapshot(coordinator);
        var mutation = snapshot.GetType().GetProperty("Mutation")!.GetValue(snapshot);
        return mutation?.GetType().GetProperty("Mode")?.GetValue(mutation)?.ToString();
    }

    private static object GetCoordinatorMutationDiagnostic(object coordinator)
    {
        var snapshot = coordinator.GetType()
            .GetProperty(
                "DiagnosticSnapshot",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator)!;
        var mutation = snapshot.GetType().GetProperty("Mutation")!.GetValue(snapshot);
        Assert.NotNull(mutation);
        return mutation;
    }

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

    private static int GetCoordinatorMutationCollapseClamps(object coordinator)
    {
        var mutation = GetCoordinatorMutationDiagnostic(coordinator);
        return Assert.IsType<int>(mutation.GetType().GetProperty("CollapseClamps")!.GetValue(mutation));
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

    private static bool GetTranscriptFollowingLatest(object viewModel)
        => Assert.IsType<bool>(viewModel.GetType()
            .GetProperty(
                "IsTranscriptFollowingLatest",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(viewModel));

    private static ScrollViewer GetTranscriptScrollViewer(AgentChatView view)
        => Assert.IsType<ScrollViewer>(view.FindControl<ScrollViewer>("TranscriptScrollViewer"));

    private static AgentTurnRecord CreateTextTurn(
        Guid sessionId,
        DateTimeOffset createdAtUtc,
        string content)
    {
        var turnId = Guid.NewGuid();
        return new AgentTurnRecord(
            turnId,
            sessionId,
            AgentMessageRole.Assistant,
            AgentTurnKind.Message,
            [new AgentTurnItemRecord(
                Guid.NewGuid(),
                turnId,
                0,
                AgentTurnItemKind.Text,
                content,
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                false,
                null,
                null)],
            createdAtUtc,
            createdAtUtc);
    }

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

        public TaskCompletionSource BlockingReadStarted { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseBlockingRead { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int BlockingReadThreadId { get; private set; }

        public int RecentTranscriptReadCount => Volatile.Read(ref _recentTranscriptReadCount);

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

        public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => [];

        public AgentWorkspaceRecord? GetWorkspace(string workspaceId) => null;

        public AgentProfileRecord? GetSessionProfile(Guid sessionId)
            => GetSession(sessionId) is { ProfileId: { } profileId }
                ? GetProfile(profileId)
                : null;

        public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId) => null;

        public AgentSessionContextCheckpointRecord? GetLatestSessionContextCheckpoint(Guid sessionId) => null;

        public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => null;

        public IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit)
        {
            Interlocked.Increment(ref _recentTranscriptReadCount);
            return ForSession(sessionId).TakeLast(Math.Max(0, limit)).ToArray();
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

            return ForSession(sessionId)
                .Where(turn => ComparePosition(turn, beforeCreatedAtUtc, beforeTurnId) < 0)
                .TakeLast(Math.Max(0, limit))
                .ToArray();
        }

        public IReadOnlyList<AgentTurnRecord> ListTurnsAfter(
            Guid sessionId,
            DateTimeOffset afterCreatedAtUtc,
            Guid afterTurnId,
            int limit)
            => ForSession(sessionId)
                .Where(turn => ComparePosition(turn, afterCreatedAtUtc, afterTurnId) > 0)
                .Take(Math.Max(0, limit))
                .ToArray();

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

    private sealed class NoOpPermissionGateway : IAgentPermissionGateway
    {
        public static NoOpPermissionGateway Instance { get; } = new();
        public AgentSessionPermissionState GetSessionState(Guid sessionId) => new(sessionId, false);
        public void SetSessionUnrestrictedMode(Guid sessionId, bool isEnabled) { }
        public IReadOnlyList<AgentPermissionActionDescriptor> ListActions() => [];
        public IReadOnlyList<AgentPermissionOverride> ListOverrides() => [];
        public void SaveOverride(string actionId, string boundaryId, AgentPermissionDecision decision) { }
        public void DeleteOverride(string actionId, string boundaryId) { }
        public IReadOnlyList<AgentPendingPermissionRequestRecord> ListPendingRequestsForSessionTree(Guid sessionId) => [];
    }

    private sealed class NoOpRunGateway : IAgentRunGateway
    {
        public static NoOpRunGateway Instance { get; } = new();
        public Task<AgentRunCheckpointRecord> QueueUserMessageAsync(Guid sessionId, string profileId,
            string userMessage, string workspaceId, IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentRunCheckpointRecord> RollbackAndQueueUserMessageAsync(Guid sessionId,
            Guid rollbackAnchorTurnId, string profileId, string userMessage, string workspaceId,
            IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentRunCheckpointRecord?> StopAsync(Guid sessionId,
            CancellationToken cancellationToken = default) => Task.FromResult<AgentRunCheckpointRecord?>(null);
        public Task<AgentRunCheckpointRecord?> ApprovePendingPermissionAsync(Guid sessionId, string requestId,
            CancellationToken cancellationToken = default) => Task.FromResult<AgentRunCheckpointRecord?>(null);
        public Task<AgentRunCheckpointRecord?> DenyPendingPermissionAsync(Guid sessionId, string requestId,
            CancellationToken cancellationToken = default) => Task.FromResult<AgentRunCheckpointRecord?>(null);
    }

    private sealed class BlockingEmbeddingProvider : IAgentEmbeddingProvider
    {
        public AgentEmbeddingProviderDescriptor Descriptor { get; } = new(
            "blocking-embeddings",
            "Blocking Embeddings",
            []);

        public TaskCompletionSource ReadinessStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> GetAvailableModelsAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentEmbeddingModelDescriptor>>([]);

        public async ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(
            CancellationToken cancellationToken = default)
        {
            ReadinessStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }

            return new AgentEmbeddingProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.Ready,
                "Ready.");
        }

        public ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(
            string modelId,
            string text,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentEmbeddingGenerationResult?>(null);

        public ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(
            string modelId,
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentEmbeddingGenerationResult?>>([]);
    }

    private sealed class BlockingBuilderProjectStore : IBuilderProjectStore
    {
        private int _loadCount;

        public int LoadCount => Volatile.Read(ref _loadCount);
        public TaskCompletionSource LoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLoad { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCancellationCleanup { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<BuilderProjectRecord>> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _loadCount);
            LoadStarted.TrySetResult();
            try
            {
                await ReleaseLoad.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                await ReleaseCancellationCleanup.Task;
                throw;
            }
            return [];
        }

        public Task SaveAsync(
            IReadOnlyList<BuilderProjectRecord> projects,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class TogglePermissionGateway : IAgentPermissionGateway
    {
        public bool IsAvailable { get; set; }

        public AgentSessionPermissionState GetSessionState(Guid sessionId)
            => new(sessionId, false);

        public void SetSessionUnrestrictedMode(Guid sessionId, bool isEnabled)
        {
        }

        public IReadOnlyList<AgentPermissionActionDescriptor> ListActions()
        {
            ThrowIfUnavailable();
            return [];
        }

        public IReadOnlyList<AgentPermissionOverride> ListOverrides()
        {
            ThrowIfUnavailable();
            return [];
        }

        public void SaveOverride(
            string actionId,
            string boundaryId,
            AgentPermissionDecision decision)
        {
        }

        public void DeleteOverride(string actionId, string boundaryId)
        {
        }

        public IReadOnlyList<AgentPendingPermissionRequestRecord> ListPendingRequestsForSessionTree(
            Guid sessionId) => [];

        private void ThrowIfUnavailable()
        {
            if (!IsAvailable)
            {
                throw new InvalidOperationException("Runtime unavailable.");
            }
        }
    }

    private sealed class FailOnceBuilderProjectStore : IBuilderProjectStore
    {
        private int _loadCount;

        public int LoadCount => Volatile.Read(ref _loadCount);

        public Task<IReadOnlyList<BuilderProjectRecord>> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Interlocked.Increment(ref _loadCount) == 1
                ? Task.FromException<IReadOnlyList<BuilderProjectRecord>>(
                    new InvalidOperationException("Injected Builder load failure."))
                : Task.FromResult<IReadOnlyList<BuilderProjectRecord>>([]);
        }

        public Task SaveAsync(
            IReadOnlyList<BuilderProjectRecord> projects,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class BlockingDockerSettingsRuntimeClient : IPackageRuntimeClient
    {
        private int _invocationCount;
        private readonly TaskCompletionSource _responseReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _timeoutSeconds;
        private string? _dockerCliPath;
        private IReadOnlyList<DockerImageDefinition>? _images;
        private long _catalogRevision;

        public bool IsAvailable => true;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(
            string timeoutSeconds,
            string dockerCliPath,
            IReadOnlyList<DockerImageDefinition> images,
            long catalogRevision)
        {
            _timeoutSeconds = timeoutSeconds;
            _dockerCliPath = dockerCliPath;
            _images = images;
            _catalogRevision = catalogRevision;
            _responseReady.TrySetResult();
        }

        public async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            Assert.Equal("docker-execution.presentation.v1", operation.OperationId);
            Assert.Equal(
                "GetSettings",
                request.GetType().GetProperty("Kind")?.GetValue(request)?.ToString());
            Interlocked.Increment(ref _invocationCount);
            Started.TrySetResult();
            await _responseReady.Task.WaitAsync(cancellationToken);
            var constructor = Assert.Single(
                typeof(TResponse).GetConstructors(
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic),
                candidate => candidate.GetParameters().Length == 7);
            return Assert.IsType<TResponse>(constructor.Invoke(
            [
                _timeoutSeconds,
                _dockerCliPath,
                _images,
                true,
                null,
                _catalogRevision,
                null,
            ]));
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class NoOpBackgroundProcessQueue : IBackgroundProcessQueue
    {
        public event EventHandler<BackgroundProcessChangedEventArgs>? ProcessChanged
        {
            add { }
            remove { }
        }

        public BackgroundProcessSnapshot Enqueue(BackgroundProcessRequest request)
            => new(
                Guid.NewGuid(),
                request.Title,
                request.GroupKey,
                request.Indicator,
                request.ConcurrencyMode,
                BackgroundProcessState.Queued,
                string.Empty,
                ProgressPercent: null,
                request.CanCancel,
                request.Metadata ?? new Dictionary<string, string>(),
                ErrorMessage: null,
                DateTimeOffset.UtcNow,
                StartedAtUtc: null,
                CompletedAtUtc: null);

        public IReadOnlyList<BackgroundProcessSnapshot> ListProcesses(string? groupKey = null) => [];

        public bool Cancel(Guid processId) => false;
    }

    private sealed class HistoryRuntimeClient : IPackageRuntimeClient
    {
        private int _searchCount;

        public bool IsAvailable => true;
        internal int SearchCount => Volatile.Read(ref _searchCount);

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            return operation.OperationId switch
            {
                "agent.history.search.v1" => new ValueTask<TResponse>(CreateSearchResponse<TResponse>()),
                "agent.history.state.v1" => new ValueTask<TResponse>(CreateStateResponse<TRequest, TResponse>(request)),
                "agent.history.command.v1" => new ValueTask<TResponse>(CreateResponse<TResponse>(new
                {
                    Status = CreateStatus(),
                })),
                _ => throw new NotSupportedException(operation.OperationId),
            };
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        private TResponse CreateSearchResponse<TResponse>() where TResponse : class
        {
            Interlocked.Increment(ref _searchCount);
            return CreateResponse<TResponse>(new
            {
                Results = new[]
                {
                    new
                    {
                        DocumentId = Guid.NewGuid().ToString("N"),
                        WorkspaceId = "workspace",
                        WorkspaceName = "Workspace with a readable context name",
                        SessionId = Guid.NewGuid(),
                        SessionTitle = "A populated history session",
                        IsChildSession = false,
                        RootSessionId = (Guid?)null,
                        ProfileId = "profile",
                        TimestampUtc = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero),
                        Role = AgentMessageRole.Assistant,
                        Activity = 4,
                        Snippet = "A populated local result verifies wrapping, focus, and accessible transcript navigation.",
                        Paths = new[]
                        {
                            "src/a/very/long/path/that/must/wrap/without/creating/horizontal/overflow/HistorySearch.cs",
                        },
                        Symbols = new[] { "AgentHistorySearchViewModel.CreateRequest" },
                        MatchReasons = new[] { "Path match" },
                        TurnId = Guid.NewGuid(),
                        ItemId = Guid.NewGuid(),
                        CallId = (string?)null,
                        AnchorKind = 0,
                    },
                },
                Continuation = (string?)null,
                IsPartial = false,
                Status = CreateStatus(),
                Restarted = false,
            });
        }

        private static TResponse CreateStateResponse<TRequest, TResponse>(TRequest request)
            where TRequest : class
            where TResponse : class
        {
            var includeAdvanced = request.GetType().GetProperty("IncludeAdvancedFilters")?.GetValue(request) as bool?
                                  ?? true;
            return CreateResponse<TResponse>(new
            {
                Status = CreateStatus(),
                EmbeddingProviders = Array.Empty<object>(),
                EmbeddingModels = Array.Empty<object>(),
                EmbeddingReadiness = (object?)null,
                Workspaces = new[] { new { Id = "workspace", DisplayName = "Workspace", ParentId = (string?)null } },
                Sessions = includeAdvanced
                    ? new[] { new { Id = Guid.NewGuid().ToString("D"), DisplayName = "Session", ParentId = "workspace" } }
                    : Array.Empty<object>(),
                Profiles = includeAdvanced
                    ? new[] { new { Id = "profile", DisplayName = "Profile", ParentId = (string?)null } }
                    : Array.Empty<object>(),
                Continuation = (string?)null,
            });
        }

        private static object CreateStatus()
            => new
            {
                Revision = 1,
                Availability = 1,
                LexicalEnabled = true,
                SemanticEnabled = false,
                SemanticReady = false,
                EmbeddingProviderPackageId = (string?)null,
                EmbeddingProviderId = (string?)null,
                EmbeddingModelId = (string?)null,
                SemanticConfigurationRevision = 0,
                TextGeneration = (long?)1,
                EmbeddingGeneration = (long?)null,
                IndexedDocuments = 1,
                EmbeddedDocuments = 0,
                PendingChanges = 0,
                ProgressCompleted = 0,
                ProgressTotal = (int?)null,
                LastReconciledAtUtc = (DateTimeOffset?)DateTimeOffset.UtcNow,
                FailureCode = (string?)null,
                FailureMessage = (string?)null,
                RuntimeInstanceId = "presentation-runtime",
            };

        private static TResponse CreateResponse<TResponse>(object value) where TResponse : class
            => JsonSerializer.Deserialize<TResponse>(JsonSerializer.Serialize(value), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? throw new InvalidOperationException("Could not create the history Runtime response.");
    }

    private sealed class NoOpPackageShellViewService : IPackageShellViewService
    {
        public IReadOnlyList<PackageHotbarView> ListHotbarViews() => [];

        public bool IsViewInHotbar(string viewId) => false;

        public ValueTask<bool> AddViewToDefaultHotbarAsync(
            string viewId,
            bool openPanel = false,
            IReadOnlyDictionary<string, string?>? parameters = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<bool> AddViewToHotbarAsync(
            string viewId,
            PackageViewPlacement placement,
            int? index = null,
            bool openPanel = false,
            IReadOnlyDictionary<string, string?>? parameters = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<bool> RemoveViewFromHotbarAsync(
            string viewId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<bool> OpenViewPanelAsync(
            string viewId,
            IReadOnlyDictionary<string, string?>? parameters = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<bool> CloseViewPanelAsync(
            string viewId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }

    private sealed class NoOpGitHubSkillClient : IGitHubSkillClient
    {
        public Task<string?> TryGetDefaultBranchAsync(
            string owner,
            string repo,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<GitHubSkillFolder?> TryGetFolderAsync(
            GitHubSkillFolderRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<GitHubSkillFolder?>(null);

        public Task<GitHubSkillFolder?> TryGetSkillFolderAsync(
            GitHubSkillFolderRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<GitHubSkillFolder?>(null);

        public Task<IReadOnlyList<GitHubSkillFile>> ListFilesAsync(
            GitHubSkillFolder folder,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GitHubSkillFile>>([]);

        public Task<byte[]> ReadFileAsync(
            GitHubSkillFolder folder,
            GitHubSkillFile file,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<byte>());
    }

    private sealed class ThrowingRpcClient : ISunderRpcClient
    {
        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
            SunderRpcEndpointReference endpoint,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SunderRpcProviderSnapshot?>(Failure());

        public ValueTask<bool> TryReportInvariantViolationAsync(
            SunderRpcEndpointReference endpoint,
            Exception exception,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<bool>(Failure());

        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
            string contractId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SunderRpcCatalogSnapshot>(Failure());

        public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
            long afterRevision,
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask<JsonElement> InvokeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<JsonElement>(Failure());

        public async IAsyncEnumerable<JsonElement> SubscribeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (!cancellationToken.IsCancellationRequested)
            {
                throw Failure();
            }
            yield break;
        }

        private static InvalidOperationException Failure()
            => new("Injected catalog failure.");
    }
}
