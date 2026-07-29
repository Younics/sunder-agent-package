using System.Collections.Concurrent;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class HistorySearchViewModelTests
{
    [Fact]
    public async Task Initialization_SearchesRecentHistoryOnceInCurrentWorkspace()
    {
        using var scope = RegressionTestPackageScope.Create();
        var selection = new AgentChatSelectionStateService(scope.Context);
        await selection.SaveSelectedWorkspaceIdAsync("workspace-current");
        var gateway = new RecordingHistoryGateway
        {
            StateFactory = request => HistoryViewModelTestData.State(
                request.IncludeAdvancedFilters
                    ? []
                    : [new HistorySearchFilterOption("workspace-current", "Current workspace")]),
        };
        using var viewModel = new AgentHistorySearchViewModel(
            gateway,
            selection,
            new CapturingHistoryShellViewService());

        await viewModel.InitializeAsync();

        var request = Assert.Single(gateway.SearchRequests);
        Assert.Equal(string.Empty, request.Query);
        Assert.Equal("workspace-current", request.WorkspaceId);
        Assert.Null(request.SessionId);
        Assert.True(request.IncludeChildSessions);
        var stateRequest = Assert.Single(gateway.StateRequests);
        Assert.False(stateRequest.IncludeAdvancedFilters);
        Assert.Equal("workspace-current", stateRequest.WorkspaceId);
        Assert.False(viewModel.IsInitialLoading);
        Assert.False(viewModel.IsAdvancedExpanded);
        Assert.Equal("Current workspace: Current workspace", viewModel.ScopeContextText);
    }

    [Fact]
    public async Task RapidTyping_CoalescesToLatestAndSuppressesStaleResponse()
    {
        using var scope = RegressionTestPackageScope.Create();
        var calls = new ConcurrentDictionary<string, ControlledSearchCall>(StringComparer.Ordinal);
        var gateway = new RecordingHistoryGateway
        {
            SearchFactory = (request, cancellationToken) =>
            {
                if (request.Query.Length == 0)
                {
                    return Task.FromResult(HistoryViewModelTestData.Response());
                }
                var call = new ControlledSearchCall(cancellationToken);
                calls[request.Query] = call;
                return call.Completion.Task;
            },
        };
        using var viewModel = HistoryViewModelTestData.CreateViewModel(scope, gateway);
        await viewModel.InitializeAsync();

        viewModel.QueryText = "older";
        await gateway.WaitForSearchAsync(request => request.Query == "older");
        var older = calls["older"];
        viewModel.QueryText = "n";
        viewModel.QueryText = "ne";
        viewModel.QueryText = "newer";
        await gateway.WaitForSearchAsync(request => request.Query == "newer");
        var newer = calls["newer"];
        newer.Completion.TrySetResult(HistoryViewModelTestData.Response(
            HistoryViewModelTestData.Hit("new result")));
        await HistoryViewModelTestData.WaitUntilAsync(() => viewModel.Results.Count == 1);

        older.Completion.TrySetResult(HistoryViewModelTestData.Response(
            HistoryViewModelTestData.Hit("stale result")));
        await HistoryViewModelTestData.WaitUntilAsync(() => older.CancellationToken.IsCancellationRequested);
        await Task.Yield();

        Assert.DoesNotContain(gateway.SearchRequests, request => request.Query is "n" or "ne");
        Assert.Equal("new result", Assert.Single(viewModel.Results).Snippet);
        Assert.False(viewModel.IsSearching);
    }

    [Fact]
    public async Task Advanced_OpensLazilyAndOnlyThenLoadsEverySessionPage()
    {
        using var scope = RegressionTestPackageScope.Create();
        var gateway = new RecordingHistoryGateway
        {
            StateFactory = request => request switch
            {
                { IncludeAdvancedFilters: false } => HistoryViewModelTestData.State(),
                { Continuation: null } => HistoryViewModelTestData.State(
                    workspaces: [new("workspace-a", "Workspace A")],
                    sessions: Enumerable.Range(0, 100)
                        .Select(index => new HistorySearchFilterOption(
                            Guid.NewGuid().ToString("D"),
                            $"Session {index:D3}",
                            "workspace-a"))
                        .ToArray(),
                    profiles: [new("profile-a", "Profile A")],
                    continuation: "page-2"),
                _ => HistoryViewModelTestData.State(
                    sessions: Enumerable.Range(100, 30)
                        .Select(index => new HistorySearchFilterOption(
                            Guid.NewGuid().ToString("D"),
                            $"Session {index:D3}",
                            "workspace-a"))
                        .ToArray()),
            },
        };
        using var viewModel = HistoryViewModelTestData.CreateViewModel(scope, gateway);

        await viewModel.InitializeAsync();

        Assert.Single(gateway.StateRequests);
        Assert.Single(gateway.SearchRequests);
        Assert.Empty(viewModel.SessionOptions);
        viewModel.ToggleAdvancedCommand.Execute(null);
        await HistoryViewModelTestData.WaitUntilAsync(() => viewModel.SessionOptions.Count == 131);

        Assert.True(viewModel.IsAdvancedExpanded);
        Assert.Equal(3, gateway.StateRequests.Count);
        Assert.Equal([null, "page-2"], gateway.StateRequests
            .Where(static request => request.IncludeAdvancedFilters)
            .Select(static request => request.Continuation));
        Assert.Single(gateway.SearchRequests);
    }

    [Fact]
    public async Task Advanced_CollapseCancelsLoadAndQuarantinesLateState()
    {
        using var scope = RegressionTestPackageScope.Create();
        var advancedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdvanced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedCancellation = default;
        var gateway = new RecordingHistoryGateway
        {
            StateAsyncFactory = async (request, cancellationToken) =>
            {
                if (!request.IncludeAdvancedFilters)
                {
                    return HistoryViewModelTestData.State();
                }
                observedCancellation = cancellationToken;
                advancedStarted.TrySetResult();
                await releaseAdvanced.Task;
                return HistoryViewModelTestData.State(
                    workspaces: [new("stale-workspace", "Stale workspace")]);
            },
        };
        using var viewModel = HistoryViewModelTestData.CreateViewModel(scope, gateway);
        await viewModel.InitializeAsync();

        viewModel.IsAdvancedExpanded = true;
        await advancedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.IsAdvancedExpanded = false;
        await HistoryViewModelTestData.WaitUntilAsync(() => observedCancellation.IsCancellationRequested);
        releaseAdvanced.TrySetResult();
        await Task.Delay(20);

        Assert.Empty(viewModel.WorkspaceOptions);
        Assert.False(viewModel.IsAdvancedLoading);
    }

    [Fact]
    public async Task AdvancedPaging_IsCappedWhenGatewayAlwaysReturnsAnotherPage()
    {
        using var scope = RegressionTestPackageScope.Create();
        var gateway = new RecordingHistoryGateway
        {
            StateFactory = request =>
            {
                if (!request.IncludeAdvancedFilters)
                {
                    return HistoryViewModelTestData.State();
                }
                var page = request.Continuation is null
                    ? 0
                    : int.Parse(request.Continuation.AsSpan("page-".Length), System.Globalization.CultureInfo.InvariantCulture);
                var sessions = Enumerable.Range(
                        page * HistorySearchLimits.MaximumStateOptions,
                        HistorySearchLimits.MaximumStateOptions)
                    .Select(static index => new HistorySearchFilterOption(
                        $"{index:x8}-0000-0000-0000-000000000000",
                        $"Session {index:D4}",
                        "workspace"))
                    .ToArray();
                return HistoryViewModelTestData.State(
                    sessions: sessions,
                    continuation: $"page-{page + 1}");
            },
        };
        using var viewModel = HistoryViewModelTestData.CreateViewModel(scope, gateway);
        await viewModel.InitializeAsync();

        viewModel.IsAdvancedExpanded = true;
        await HistoryViewModelTestData.WaitUntilAsync(() => !viewModel.IsAdvancedLoading
                                                           && gateway.StateRequests.Count(static request => request.IncludeAdvancedFilters) > 0);

        Assert.Equal(
            HistorySearchLimits.MaximumAdvancedFilterPages,
            gateway.StateRequests.Count(static request => request.IncludeAdvancedFilters));
        Assert.Equal(HistorySearchLimits.MaximumAdvancedSessionOptions + 1, viewModel.SessionOptions.Count);
    }

    [Fact]
    public async Task RapidSearchAdvancedAndStateReplacement_CancelsWithoutCtsOwnershipFaults()
    {
        using var scope = RegressionTestPackageScope.Create();
        var selection = new AgentChatSelectionStateService(scope.Context);
        var canceledSearches = 0;
        var canceledStates = 0;
        var gateway = new RecordingHistoryGateway
        {
            SearchFactory = async (request, cancellationToken) =>
            {
                if (request.Query.Length == 0 || request.Query == "settled")
                {
                    return HistoryViewModelTestData.Response(
                        HistoryViewModelTestData.Hit(request.Query.Length == 0 ? "initial" : "settled"));
                }
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return HistoryViewModelTestData.Response();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref canceledSearches);
                    throw;
                }
            },
            StateAsyncFactory = async (request, cancellationToken) =>
            {
                if (request.IncludeAdvancedFilters)
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return HistoryViewModelTestData.State();
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref canceledStates);
                        throw;
                    }
                }
                if (request.WorkspaceId is null or "workspace-final")
                {
                    return HistoryViewModelTestData.State();
                }
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return HistoryViewModelTestData.State();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref canceledStates);
                    throw;
                }
            },
        };
        using var viewModel = new AgentHistorySearchViewModel(
            gateway,
            selection,
            new CapturingHistoryShellViewService());
        await viewModel.InitializeAsync();

        viewModel.QueryText = "race";
        await gateway.WaitForSearchAsync(static request => request.Query == "race");
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var count = gateway.SearchRequests.Count;
            viewModel.SelectedRole = viewModel.RoleOptions.Single(option => option.Id == (iteration % 2 == 0 ? "User" : "Assistant"));
            await HistoryViewModelTestData.WaitUntilAsync(() => gateway.SearchRequests.Count > count);
        }
        viewModel.QueryText = "settled";
        await gateway.WaitForSearchAsync(static request => request.Query == "settled");
        await HistoryViewModelTestData.WaitUntilAsync(() => viewModel.Results.Any(static result => result.Snippet == "settled"));

        for (var iteration = 0; iteration < 10; iteration++)
        {
            var stateCount = gateway.StateRequests.Count;
            viewModel.IsAdvancedExpanded = true;
            await HistoryViewModelTestData.WaitUntilAsync(() => gateway.StateRequests.Count > stateCount);
            viewModel.IsAdvancedExpanded = false;
            await HistoryViewModelTestData.WaitUntilAsync(() => !viewModel.IsAdvancedLoading);
        }

        for (var iteration = 0; iteration < 10; iteration++)
        {
            await selection.SaveSelectedWorkspaceIdAsync($"workspace-{iteration}");
            await gateway.WaitForStateAsync(request => request.WorkspaceId == $"workspace-{iteration}");
        }
        await selection.SaveSelectedWorkspaceIdAsync("workspace-final");
        await gateway.WaitForStateAsync(static request => request.WorkspaceId == "workspace-final");
        await HistoryViewModelTestData.WaitUntilAsync(() => !viewModel.IsSearching);

        Assert.True(canceledSearches > 0);
        Assert.True(canceledStates > 0);
        Assert.Empty(viewModel.SearchError);
        Assert.Empty(viewModel.RuntimeNotice);
    }

    [Fact]
    public async Task DisposeDuringCoalescedStatusRefresh_CancelsRefreshWithoutLifetimeTokenFaults()
    {
        using var scope = RegressionTestPackageScope.Create();
        var refreshStarted = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var searchCount = 0;
        var gateway = new RecordingHistoryGateway
        {
            SearchFactory = async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref searchCount) == 1)
                {
                    return HistoryViewModelTestData.Response();
                }
                refreshStarted.TrySetResult(cancellationToken);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return HistoryViewModelTestData.Response();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    refreshCancelled.TrySetResult();
                    throw;
                }
            },
        };
        var viewModel = HistoryViewModelTestData.CreateViewModel(scope, gateway);
        await viewModel.InitializeAsync();

        gateway.RaiseStatus(HistoryViewModelTestData.ReadyStatus(
            revision: 2,
            projectionRevision: 1));
        var refreshToken = await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.Dispose();
        await refreshCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(refreshToken.IsCancellationRequested);
        Assert.Empty(viewModel.SearchError);
    }

    [Fact]
    public async Task LoadMore_AppendsAndGenerationRestartReplacesResults()
    {
        using var scope = RegressionTestPackageScope.Create();
        var gateway = new RecordingHistoryGateway
        {
            SearchFactory = (request, _) => Task.FromResult(request.Continuation switch
            {
                null => HistoryViewModelTestData.Response(
                    HistoryViewModelTestData.Hit("first"),
                    continuation: "next"),
                "next" => HistoryViewModelTestData.Response(
                    HistoryViewModelTestData.Hit("second"),
                    continuation: "restart"),
                _ => HistoryViewModelTestData.Response(
                    HistoryViewModelTestData.Hit("replacement")) with
                { Restarted = true },
            }),
        };
        using var viewModel = HistoryViewModelTestData.CreateViewModel(scope, gateway);
        await viewModel.InitializeAsync();

        await viewModel.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(["first", "second"], viewModel.Results.Select(static result => result.Snippet));
        await viewModel.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal("replacement", Assert.Single(viewModel.Results).Snippet);
        Assert.Equal([null, "next", "restart"], gateway.SearchRequests.Select(static request => request.Continuation));
    }

    [Theory]
    [InlineData(false, "sunder.package.agent.chat")]
    [InlineData(true, "sunder.package.agent.subagents.sessions")]
    public async Task OpenResult_RoutesToOwningTranscriptWithExactAnchor(
        bool isChildSession,
        string expectedViewId)
    {
        using var scope = RegressionTestPackageScope.Create();
        var hit = HistoryViewModelTestData.Hit("navigate", isChildSession);
        var gateway = new RecordingHistoryGateway
        {
            SearchFactory = (_, _) => Task.FromResult(HistoryViewModelTestData.Response(hit)),
        };
        var shell = new CapturingHistoryShellViewService();
        using var viewModel = new AgentHistorySearchViewModel(
            gateway,
            new AgentChatSelectionStateService(scope.Context),
            shell);
        await viewModel.InitializeAsync();

        await viewModel.OpenResultCommand.ExecuteAsync(Assert.Single(viewModel.Results));

        Assert.Equal(expectedViewId, shell.OpenedViewId);
        Assert.NotNull(shell.Parameters);
        Assert.Equal(hit.WorkspaceId, shell.Parameters[HistorySearchNavigation.WorkspaceIdKey]);
        Assert.Equal(hit.SessionId.ToString("D"), shell.Parameters[HistorySearchNavigation.SessionIdKey]);
        Assert.Equal(hit.TurnId.ToString("D"), shell.Parameters[HistorySearchNavigation.TurnIdKey]);
        Assert.Equal(hit.ItemId.ToString("D"), shell.Parameters[HistorySearchNavigation.ItemIdKey]);
    }

    [Fact]
    public async Task SearchFailure_PreservesResultsAndUsesSeparateErrorState()
    {
        using var scope = RegressionTestPackageScope.Create();
        var gateway = new RecordingHistoryGateway
        {
            SearchFactory = (request, _) => request.Query.Length == 0
                ? Task.FromResult(HistoryViewModelTestData.Response(HistoryViewModelTestData.Hit("existing")))
                : Task.FromException<HistorySearchResponse>(new InvalidOperationException("expected")),
        };
        using var viewModel = HistoryViewModelTestData.CreateViewModel(scope, gateway);
        await viewModel.InitializeAsync();

        viewModel.QueryText = "failure";
        await HistoryViewModelTestData.WaitUntilAsync(() => viewModel.HasSearchError);

        Assert.Equal("existing", Assert.Single(viewModel.Results).Snippet);
        Assert.False(viewModel.IsSearching);
        Assert.Empty(viewModel.NavigationError);
        Assert.NotEmpty(viewModel.SearchError);
    }

    [Fact]
    public async Task FirstSearchFailure_DoesNotClaimThatPreviousResultsAreShown()
    {
        using var scope = RegressionTestPackageScope.Create();
        var gateway = new RecordingHistoryGateway
        {
            SearchFactory = (_, _) => Task.FromException<HistorySearchResponse>(
                new InvalidOperationException("expected")),
        };
        using var viewModel = HistoryViewModelTestData.CreateViewModel(scope, gateway);

        await viewModel.InitializeAsync();

        Assert.True(viewModel.HasSearchError);
        Assert.Equal("History search could not complete. Try again.", viewModel.SearchError);
        Assert.Empty(viewModel.Results);
    }

    private sealed class ControlledSearchCall(CancellationToken cancellationToken)
    {
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal TaskCompletionSource<HistorySearchResponse> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal sealed class RecordingHistoryGateway : IAgentHistorySearchGateway
{
    private readonly SemaphoreSlim _searchObserved = new(0);
    private readonly SemaphoreSlim _stateObserved = new(0);
    private HistorySearchStatus _status = HistoryViewModelTestData.ReadyStatus();

    internal Func<HistorySearchRequest, CancellationToken, Task<HistorySearchResponse>>? SearchFactory { get; init; }
    internal Func<HistorySearchStateRequest, CancellationToken, Task<HistorySearchState>>? StateAsyncFactory { get; init; }
    internal Func<HistorySearchStateRequest, HistorySearchState>? StateFactory { get; init; }
    internal List<HistorySearchRequest> SearchRequests { get; } = [];
    internal List<HistorySearchStateRequest> StateRequests { get; } = [];
    internal List<HistorySearchCommand> Commands { get; } = [];

    public event Action<HistorySearchStatus>? HistoryStatusChanged;
    public event Action? HistoryRuntimeChanged;

    public Task<HistorySearchResponse> SearchHistoryAsync(
        HistorySearchRequest request,
        CancellationToken cancellationToken = default)
    {
        lock (SearchRequests)
        {
            SearchRequests.Add(request);
        }
        _searchObserved.Release();
        return SearchFactory?.Invoke(request, cancellationToken)
               ?? Task.FromResult(HistoryViewModelTestData.Response(status: _status));
    }

    public Task<HistorySearchState> LoadHistoryStateAsync(
        HistorySearchStateRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (StateRequests)
        {
            StateRequests.Add(request);
        }
        _stateObserved.Release();
        if (StateAsyncFactory is not null)
        {
            return StateAsyncFactory(request, cancellationToken);
        }
        var state = StateFactory?.Invoke(request) ?? HistoryViewModelTestData.State(status: _status);
        _status = state.Status;
        return Task.FromResult(state);
    }

    public Task<HistorySearchCommandResult> ExecuteHistoryCommandAsync(
        HistorySearchCommand command,
        CancellationToken cancellationToken = default)
    {
        Commands.Add(command);
        return Task.FromResult(new HistorySearchCommandResult(_status));
    }

    public void StartObservingHistoryStatus() { }

    internal void RaiseStatus(HistorySearchStatus status)
    {
        _status = status;
        HistoryStatusChanged?.Invoke(status);
    }

    internal void RaiseRuntimeChanged() => HistoryRuntimeChanged?.Invoke();

    internal async Task<HistorySearchRequest> WaitForSearchAsync(Func<HistorySearchRequest, bool> predicate)
    {
        while (true)
        {
            lock (SearchRequests)
            {
                var match = SearchRequests.LastOrDefault(predicate);
                if (match is not null)
                {
                    return match;
                }
            }
            await _searchObserved.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    internal async Task<HistorySearchStateRequest> WaitForStateAsync(
        Func<HistorySearchStateRequest, bool> predicate)
    {
        while (true)
        {
            lock (StateRequests)
            {
                var match = StateRequests.LastOrDefault(predicate);
                if (match is not null)
                {
                    return match;
                }
            }
            await _stateObserved.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}

internal static class HistoryViewModelTestData
{
    internal static AgentHistorySearchViewModel CreateViewModel(
        RegressionTestPackageScope scope,
        RecordingHistoryGateway gateway,
        TimeProvider? timeProvider = null)
        => new(
            gateway,
            new AgentChatSelectionStateService(scope.Context),
            new CapturingHistoryShellViewService(),
            timeProvider);

    internal static HistorySearchState State(
        IReadOnlyList<HistorySearchFilterOption>? workspaces = null,
        IReadOnlyList<HistorySearchFilterOption>? sessions = null,
        IReadOnlyList<HistorySearchFilterOption>? profiles = null,
        string? continuation = null,
        HistorySearchStatus? status = null)
        => new(
            status ?? ReadyStatus(),
            [],
            [],
            null,
            workspaces ?? [],
            sessions ?? [],
            profiles ?? [],
            continuation);

    internal static HistorySearchResponse Response(
        HistorySearchHit? hit = null,
        string? continuation = null,
        HistorySearchStatus? status = null)
        => new(hit is null ? [] : [hit], continuation, false, status ?? ReadyStatus());

    internal static HistorySearchHit Hit(string snippet, bool isChildSession = false)
    {
        var rootId = Guid.NewGuid();
        return new HistorySearchHit(
            Guid.NewGuid().ToString("N"),
            "workspace",
            "Workspace",
            Guid.NewGuid(),
            "Session",
            isChildSession,
            isChildSession ? rootId : null,
            "profile",
            new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero),
            AgentMessageRole.Assistant,
            HistoryActivityKind.None,
            snippet,
            ["src/History.cs"],
            ["SearchAsync"],
            ["Text match"],
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            HistoryAnchorKind.Text);
    }

    internal static HistorySearchStatus ReadyStatus(
        long revision = 1,
        string runtimeInstanceId = "runtime-one",
        long? textGeneration = 1,
        int pendingChanges = 0,
        long projectionRevision = 0)
        => new(
            revision,
            HistorySearchAvailability.Ready,
            LexicalEnabled: true,
            SemanticEnabled: false,
            SemanticReady: false,
            EmbeddingProviderPackageId: null,
            EmbeddingProviderId: null,
            EmbeddingModelId: null,
            SemanticConfigurationRevision: 0,
            ProjectionRevision: projectionRevision,
            TextGeneration: textGeneration,
            EmbeddingGeneration: null,
            IndexedDocuments: 1,
            EmbeddedDocuments: 0,
            PendingChanges: pendingChanges,
            ProgressCompleted: 0,
            ProgressTotal: null,
            LastReconciledAtUtc: DateTimeOffset.UtcNow,
            FailureCode: null,
            FailureMessage: null,
            RuntimeInstanceId: runtimeInstanceId);

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for history ViewModel state.");
            }
            await Task.Delay(10);
        }
    }
}

internal sealed class CapturingHistoryShellViewService : IPackageShellViewService
{
    internal string? OpenedViewId { get; private set; }
    internal IReadOnlyDictionary<string, string?>? Parameters { get; private set; }
    public IReadOnlyList<PackageHotbarView> ListHotbarViews() => [];
    public bool IsViewInHotbar(string viewId) => false;
    public ValueTask<bool> AddViewToDefaultHotbarAsync(
        string viewId,
        bool openPanel = false,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    public ValueTask<bool> AddViewToHotbarAsync(
        string viewId,
        PackageViewPlacement placement,
        int? index = null,
        bool openPanel = false,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    public ValueTask<bool> RemoveViewFromHotbarAsync(
        string viewId,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    public ValueTask<bool> OpenViewPanelAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        OpenedViewId = viewId;
        Parameters = parameters;
        return ValueTask.FromResult(true);
    }
    public ValueTask<bool> CloseViewPanelAsync(
        string viewId,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
}
