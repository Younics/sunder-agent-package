extern alias AgentApp;

using System.Text.Json;
using AgentApp::Sunder.Package.Agent.HistorySearch;
using AgentApp::Sunder.Package.Agent.PackageViews;
using Avalonia.Controls.Primitives;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Xunit;
using static Sunder.Package.Agent.Presentation.Tests.RenderedViewComposition;
using static Sunder.Package.Agent.Presentation.Tests.RenderedViewWaits;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class HistorySearchRenderedViewTests
{
    [AvaloniaFact]
    public async Task HistorySearchView_UsesLockedBreakpointsWithoutHorizontalOverflow()
    {
        using var scope = RegressionTestPackageScope.Create();
        var runtime = new HistoryRuntimeClient();
        var services = CreateHistoryAppServices(scope, runtime);
        await using var provider = services.BuildServiceProvider();
        using var view = provider.GetRequiredService<AgentHistorySearchView>();
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
        using var view = provider.GetRequiredService<AgentHistorySearchView>();
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
            "younics.ai.agent.history",
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

    private sealed class HistoryRuntimeClient : OwnerServiceStub
    {
        private int _searchCount;

        internal int SearchCount => Volatile.Read(ref _searchCount);

        public override ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request is HistorySearchRequest)
            {
                return new ValueTask<TResponse>(CreateSearchResponse<TResponse>());
            }
            if (request is HistorySearchStateRequest)
            {
                return new ValueTask<TResponse>(CreateStateResponse<TRequest, TResponse>(request));
            }
            if (request is HistorySearchCommand)
            {
                return new ValueTask<TResponse>(CreateResponse<TResponse>(new
                {
                    Status = CreateStatus(),
                }));
            }
            throw new NotSupportedException(typeof(TRequest).Name);
        }

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
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
}
