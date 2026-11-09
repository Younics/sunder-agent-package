extern alias AgentApp;

using AgentApp::Sunder.Package.Agent.PackageViews;
using AgentApp::Sunder.Package.Agent.Shared.PackageViews;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia;
using Sunder.Package.Agent.Contracts.Models;
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

public sealed class AgentChatLayoutTests
{
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
            "younics.ai.agent.chat",
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

        await Task.Run(async () => await view.OnNavigatedToAsync(
            new PackageViewNavigationContext(
                "younics.ai.agent.chat",
                new Dictionary<string, string?>())));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);

        var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
        await RevealCompactCallsAsync(view, viewModel.TranscriptItems.OfType<AgentActivityGroupRowViewModel>().ToArray());
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
        window.MouseWheel(new Point(450, 250), new Vector(0, 2), RawInputModifiers.None);
        await WaitForPendingCoordinatorOperationsAsync(view, TimeSpan.FromSeconds(10));
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
        var detachedAnchor = Assert.IsAssignableFrom<Control>(transcript.CurrentAnchor);
        var detachedRow = Assert.Single(
            viewModel.TranscriptItems,
            row => ReferenceEquals(row, detachedAnchor.DataContext));
        var detachedAnchorKey = detachedRow.AnchorKey;
        var detachedAnchorIndex = viewModel.TranscriptItems
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
            "younics.ai.agent.chat",
            new Dictionary<string, string?>()));
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
        await RevealCompactCallsAsync(view, viewModel.TranscriptItems.OfType<AgentActivityGroupRowViewModel>().ToArray());
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
}
