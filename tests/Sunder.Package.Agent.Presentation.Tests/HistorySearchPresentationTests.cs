using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Runtime;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class HistorySearchPresentationTests
{
    private const double ExpectedMaximumContentWidth = 1040;

    [AvaloniaFact]
    public async Task ResultsStretchAtEveryBreakpointAndUseRoleSurfacesWithoutRecentTag()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var provider = CreateServices(scope, new PresentationHistoryRuntimeClient());
        using var view = ActivatorUtilities.CreateInstance<AgentHistorySearchView>(provider);
        var viewModel = Assert.IsType<AgentHistorySearchViewModel>(view.DataContext);
        var window = new Window { Width = 180, Height = 1000, Content = view };
        window.Show();
        await view.WarmupAsync();

        foreach (var width in new[] { 180d, 360d, 519d, 520d, 819d, 820d })
        {
            window.Width = width;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

            var scroll = view.FindControl<ScrollViewer>("HistoryScrollViewer")!;
            var layout = view.FindControl<Border>("HistoryLayoutRoot")!;
            var content = view.FindControl<Border>("HistoryContent")!;
            var repeater = view.FindControl<ItemsRepeater>("HistoryResultsRepeater")!;
            Assert.Equal(width < 240, layout.Classes.Contains("micro"));
            Assert.Equal(width < 520, layout.Classes.Contains("compact"));
            Assert.Equal(width is >= 520 and < 820, layout.Classes.Contains("intermediate"));
            Assert.Equal(width >= 820, layout.Classes.Contains("wide"));
            Assert.Equal(Math.Min(scroll.Viewport.Width, ExpectedMaximumContentWidth),
                content.Bounds.Width,
                precision: 3);
            Assert.Equal(HorizontalAlignment.Stretch, repeater.HorizontalAlignment);
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 0.5,
                $"History content overflowed at {width}px: {scroll.Extent.Width} > {scroll.Viewport.Width}.");

            for (var index = 0; index < viewModel.Results.Count; index++)
            {
                var button = Assert.IsType<Button>(repeater.TryGetElement(index));
                var surface = Assert.IsType<Border>(button.Content);
                Assert.Equal(HorizontalAlignment.Stretch, button.HorizontalAlignment);
                Assert.Equal(HorizontalAlignment.Stretch, surface.HorizontalAlignment);
                Assert.Equal(repeater.Bounds.Width, button.Bounds.Width, precision: 3);
                Assert.Equal(button.Bounds.Width, surface.Bounds.Width, precision: 3);
                Assert.Equal(new Thickness(1), surface.BorderThickness);
                Assert.NotEqual(default, surface.CornerRadius);
                Assert.NotNull(AutomationProperties.GetName(button));
                Assert.NotNull(AutomationProperties.GetHelpText(button));
                Assert.Empty(button.GetVisualDescendants().OfType<Image>());
                Assert.DoesNotContain(
                    button.GetVisualDescendants().OfType<TextBlock>(),
                    static text => string.Equals(text.Text, "Recent history", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(
                    button.GetVisualDescendants().OfType<TextBlock>(),
                    static text => text.Text?.Contains("Symbol", StringComparison.OrdinalIgnoreCase) == true);
            }
        }

        var resultsRepeater = view.FindControl<ItemsRepeater>("HistoryResultsRepeater")!;
        var userSurface = GetSurface(resultsRepeater, 0);
        var assistantSurface = GetSurface(resultsRepeater, 1);
        var activitySurface = GetSurface(resultsRepeater, 2);
        AssertResourceBrush("Sunder.Brush.Info.Soft", userSurface.Background);
        AssertResourceBrush("Sunder.Brush.Accent", userSurface.BorderBrush);
        AssertResourceBrush("Sunder.Brush.Surface.Base", assistantSurface.Background);
        AssertResourceBrush("Sunder.Brush.Border.Subtle", assistantSurface.BorderBrush);
        AssertResourceBrush("Sunder.Brush.Surface.Code", activitySurface.Background);
        AssertResourceBrush("Sunder.Brush.Border.Subtle", activitySurface.BorderBrush);

        var assistantButton = Assert.IsType<Button>(resultsRepeater.TryGetElement(1));
        var pointer = Assert.IsType<Point>(assistantButton.TranslatePoint(
            new Point(assistantButton.Bounds.Width / 2, assistantButton.Bounds.Height / 2),
            window));
        window.MouseMove(pointer, RawInputModifiers.None);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Assert.True(assistantButton.IsPointerOver);
        AssertResourceBrush("Sunder.Brush.Border.Strong", assistantSurface.BorderBrush);
        Assert.Equal(new Thickness(1), assistantSurface.BorderThickness);
        Assert.Contains(view.Styles.OfType<Style>(), static style =>
            style.Selector?.ToString().Contains(
                "Button.history-result:pressed Border.history-result-surface",
                StringComparison.Ordinal) == true);

        assistantButton.Focus();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        AssertResourceBrush("Sunder.Brush.Accent", assistantSurface.BorderBrush);
        Assert.Equal(new Thickness(1), assistantSurface.BorderThickness);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ProgressRowIsTwoPixelsFlushReservedAndNeverChangesInputWidth()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var provider = CreateServices(scope, new PresentationHistoryRuntimeClient());
        using var view = ActivatorUtilities.CreateInstance<AgentHistorySearchView>(provider);
        var viewModel = Assert.IsType<AgentHistorySearchViewModel>(view.DataContext);
        var window = new Window { Width = 520, Height = 700, Content = view };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        var host = view.FindControl<Grid>("HistorySearchProgressHost")!;
        var searchHost = view.FindControl<Border>("HistorySearchHost")!;
        var progress = view.FindControl<ProgressBar>("HistorySearchProgress")!;
        var query = view.FindControl<TextBox>("HistoryQueryTextBox")!;
        var scopeContext = view.FindControl<TextBlock>("HistoryScopeContext")!;
        Assert.True(progress.IsVisible);
        Assert.Equal(2, progress.Bounds.Height, precision: 3);
        Assert.Equal(searchHost.Bounds.Bottom, progress.Bounds.Top, precision: 3);
        Assert.Equal(1, progress.Bounds.Left, precision: 3);
        Assert.Equal(host.Bounds.Width - 2, progress.Bounds.Width, precision: 3);
        var activeHostHeight = host.Bounds.Height;
        var activeQueryWidth = query.Bounds.Width;
        var activeScopeTop = Assert.IsType<Point>(scopeContext.TranslatePoint(default, view)).Y;

        viewModel.IsInitialLoading = false;
        viewModel.IsSearching = false;
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Assert.False(progress.IsVisible);
        Assert.Equal(activeHostHeight, host.Bounds.Height, precision: 3);
        Assert.Equal(activeQueryWidth, query.Bounds.Width, precision: 3);
        Assert.Equal(activeScopeTop, Assert.IsType<Point>(scopeContext.TranslatePoint(default, view)).Y, precision: 3);

        viewModel.IsSearching = true;
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Assert.True(progress.IsVisible);
        Assert.Equal(activeQueryWidth, query.Bounds.Width, precision: 3);
        Assert.Equal(activeHostHeight, host.Bounds.Height, precision: 3);

        viewModel.IsSearching = false;
        viewModel.IsAdvancedLoading = true;
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Assert.False(progress.IsVisible);
        Assert.Equal(activeQueryWidth, query.Bounds.Width, precision: 3);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ResultSemanticsAggregateMetadataAndExposeUsefulAccessibilityText()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var provider = CreateServices(scope, new PresentationHistoryRuntimeClient());
        using var view = ActivatorUtilities.CreateInstance<AgentHistorySearchView>(provider);
        var viewModel = Assert.IsType<AgentHistorySearchViewModel>(view.DataContext);
        viewModel.QueryText = "history query";
        await view.WarmupAsync();

        var result = viewModel.Results[0];
        Assert.True(result.IsUserResult);
        Assert.False(result.IsAssistantResult);
        Assert.False(result.IsActivityResult);
        Assert.Equal("User", result.SenderText);
        Assert.Contains("User", result.HeaderText, StringComparison.Ordinal);
        Assert.Equal("Paths", result.PathLabel);
        Assert.Equal(2, result.Paths.Count);
        Assert.Contains("src/History.cs", result.PathSummary, StringComparison.Ordinal);
        Assert.Contains("child session", result.ContextText, StringComparison.Ordinal);
        Assert.DoesNotContain("Recent history", result.DisplayReasons);
        Assert.Equal(["Phrase match", "Path exact match"], result.DisplayReasons);
        Assert.Contains("user", result.AutomationHelpText, StringComparison.Ordinal);
        Assert.Contains("Session", result.AutomationName, StringComparison.Ordinal);

        var activity = viewModel.Results[2];
        Assert.True(activity.IsActivityResult);
        Assert.True(activity.HasActivityLabel);
        Assert.Equal("Search activity", activity.ActivityLabel);
        Assert.All(viewModel.Results, static item => Assert.True(item.HasMatchReasons));
    }

    private static ServiceProvider CreateServices(
        RegressionTestPackageScope scope,
        IPackageRuntimeClient runtime)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(scope.Context);
        services.AddSingleton(runtime);
        services.AddSingleton<IPackageRuntimeClient>(runtime);
        services.AddSingleton<AgentRpcCatalog>(new RegressionTestExtensionCatalog());
        services.AddSingleton<IPackageShellViewService, PresentationShellViewService>();
        services.AddSingleton<IPackageNotificationService>(NullPackageNotificationService.Instance);
        services.AddSingleton<IBackgroundProcessQueue, PresentationBackgroundProcessQueue>();
        new AppPackageModule().ConfigureAppServices(services, scope.Context);
        return services.BuildServiceProvider();
    }

    private static Border GetSurface(ItemsRepeater repeater, int index)
        => Assert.IsType<Border>(Assert.IsType<Button>(repeater.TryGetElement(index)).Content);

    private static void AssertResourceBrush(string key, IBrush? actual)
    {
        var expected = Assert.IsAssignableFrom<IBrush>(Application.Current!.Resources[key]);
        Assert.Same(expected, actual);
    }

    private sealed class PresentationHistoryRuntimeClient : IPackageRuntimeClient
    {
        public bool IsAvailable => true;

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
                "agent.history.search.v1" => new ValueTask<TResponse>(CreateSearchResponse<TRequest, TResponse>(request)),
                "agent.history.state.v1" => new ValueTask<TResponse>(CreateStateResponse<TResponse>()),
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

        private static TResponse CreateSearchResponse<TRequest, TResponse>(TRequest request)
            where TRequest : class
            where TResponse : class
        {
            var query = request.GetType().GetProperty("Query")?.GetValue(request) as string;
            var reasons = string.IsNullOrWhiteSpace(query)
                ? new[] { "Recent history" }
                : ["Recent history", "Phrase match", "Path exact match"];
            return CreateResponse<TResponse>(new
            {
                Results = new[]
                {
                    CreateHit(
                        "user",
                        (AgentMessageRole?)AgentMessageRole.User,
                        activity: 0,
                        anchorKind: 0,
                        isChild: true,
                        reasons),
                    CreateHit(
                        "assistant",
                        (AgentMessageRole?)AgentMessageRole.Assistant,
                        activity: 0,
                        anchorKind: 0,
                        isChild: false,
                        reasons),
                    CreateHit(
                        "activity",
                        role: null,
                        activity: 2,
                        anchorKind: 1,
                        isChild: false,
                        reasons),
                },
                Continuation = (string?)null,
                IsPartial = false,
                Status = CreateStatus(),
                Restarted = false,
            });
        }

        private static object CreateHit(
            string snippet,
            AgentMessageRole? role,
            int activity,
            int anchorKind,
            bool isChild,
            IReadOnlyList<string> reasons)
            => new
            {
                DocumentId = Guid.NewGuid().ToString("N"),
                WorkspaceId = "workspace",
                WorkspaceName = "Workspace",
                SessionId = Guid.NewGuid(),
                SessionTitle = "Session",
                IsChildSession = isChild,
                RootSessionId = isChild ? Guid.NewGuid() : (Guid?)null,
                ProfileId = "profile",
                TimestampUtc = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero),
                Role = role,
                Activity = activity,
                Snippet = snippet,
                Paths = new[] { "src/History.cs", "tests/HistoryTests.cs", "src/History.cs" },
                Symbols = new[] { "HistorySearch.RunAsync", "HistorySearch.RunAsync" },
                MatchReasons = reasons,
                TurnId = Guid.NewGuid(),
                ItemId = Guid.NewGuid(),
                CallId = anchorKind == 1 ? "call-1" : null,
                AnchorKind = anchorKind,
            };

        private static TResponse CreateStateResponse<TResponse>() where TResponse : class
            => CreateResponse<TResponse>(new
            {
                Status = CreateStatus(),
                EmbeddingProviders = Array.Empty<object>(),
                EmbeddingModels = Array.Empty<object>(),
                EmbeddingReadiness = (object?)null,
                Workspaces = new[] { new { Id = "workspace", DisplayName = "Workspace", ParentId = (string?)null } },
                Sessions = Array.Empty<object>(),
                Profiles = Array.Empty<object>(),
                Continuation = (string?)null,
            });

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
                ProjectionRevision = 0,
                TextGeneration = (long?)1,
                EmbeddingGeneration = (long?)null,
                IndexedDocuments = 3,
                EmbeddedDocuments = 0,
                PendingChanges = 0,
                ProgressCompleted = 0,
                ProgressTotal = (int?)null,
                LastReconciledAtUtc = (DateTimeOffset?)DateTimeOffset.UtcNow,
                FailureCode = (string?)null,
                FailureMessage = (string?)null,
                RuntimeInstanceId = "presentation-history",
            };

        private static TResponse CreateResponse<TResponse>(object value) where TResponse : class
            => JsonSerializer.Deserialize<TResponse>(JsonSerializer.Serialize(value), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? throw new InvalidOperationException("Could not create the History Runtime response.");
    }

    private sealed class PresentationShellViewService : IPackageShellViewService
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
            => ValueTask.FromResult(true);

        public ValueTask<bool> CloseViewPanelAsync(
            string viewId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }

    private sealed class PresentationBackgroundProcessQueue : IBackgroundProcessQueue
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
}
