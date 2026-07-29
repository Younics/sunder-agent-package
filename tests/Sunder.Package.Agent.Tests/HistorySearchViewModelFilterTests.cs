using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class HistorySearchViewModelFilterTests
{
    [Fact]
    public async Task AdvancedFilters_CreateExactRequestAndResetOnce()
    {
        using var scope = RegressionTestPackageScope.Create();
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        var gateway = CreateFilterGateway(sessionA, sessionB);
        using var viewModel = HistoryViewModelTestData.CreateViewModel(
            scope,
            gateway,
            new FixedHistoryTimeProvider(new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero)));
        await viewModel.InitializeAsync();
        viewModel.ToggleAdvancedCommand.Execute(null);
        await HistoryViewModelTestData.WaitUntilAsync(() => viewModel.SessionOptions.Count > 0);

        viewModel.SelectedScope = viewModel.ScopeOptions.Single(option => option.Id == "specific");
        viewModel.SelectedWorkspace = viewModel.WorkspaceOptions.Single(option => option.Id == "workspace-b");
        Assert.DoesNotContain(viewModel.SessionOptions, option => option.Id == sessionA.ToString("D"));
        viewModel.SelectedSession = viewModel.SessionOptions.Single(option => option.Id == sessionB.ToString("D"));
        viewModel.SelectedProfile = viewModel.ProfileOptions.Single(option => option.Id == "profile-b");
        viewModel.SelectedRole = viewModel.RoleOptions.Single(option => option.Id == "Assistant");
        viewModel.SelectedActivity = viewModel.ActivityOptions.Single(option => option.Id == "Edit");
        viewModel.SelectedWhen = viewModel.WhenOptions.Single(option => option.Id == "custom");
        viewModel.FromDate = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        viewModel.ToDate = new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);
        viewModel.IncludeChildSessions = false;
        await gateway.WaitForSearchAsync(request => request.SessionId == sessionB
                                                       && request.Activity == HistoryActivityKind.Edit
                                                       && request.IncludeChildSessions == false);

        var request = gateway.SearchRequests.Last();
        Assert.Equal("workspace-b", request.WorkspaceId);
        Assert.Equal(sessionB, request.SessionId);
        Assert.Equal("profile-b", request.ProfileId);
        Assert.Equal(HistorySearchRoleFilter.Assistant, request.Role);
        Assert.Equal(HistoryActivityKind.Edit, request.Activity);
        Assert.Equal(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero), request.FromUtc);
        Assert.Equal(new DateTimeOffset(2026, 7, 20, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_999), request.ToUtc);
        Assert.True(viewModel.IsSpecificSessionSelected);
        Assert.True(viewModel.HasDateValidation);
        Assert.Contains(viewModel.ActiveFilters, filter => filter.Key == "children");

        var searchesBeforeReset = gateway.SearchRequests.Count;
        viewModel.ResetFiltersCommand.Execute(null);
        await HistoryViewModelTestData.WaitUntilAsync(() => gateway.SearchRequests.Count == searchesBeforeReset + 1);

        var reset = gateway.SearchRequests.Last();
        Assert.Null(reset.SessionId);
        Assert.Null(reset.ProfileId);
        Assert.Equal(HistorySearchRoleFilter.Any, reset.Role);
        Assert.Equal(HistoryActivityKind.None, reset.Activity);
        Assert.Null(reset.FromUtc);
        Assert.Null(reset.ToUtc);
        Assert.True(reset.IncludeChildSessions);
        Assert.Empty(viewModel.ActiveFilters);
    }

    [Fact]
    public async Task ChangingWorkspace_ResetsIncompatibleSessionAndChildToggle()
    {
        using var scope = RegressionTestPackageScope.Create();
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        var gateway = CreateFilterGateway(sessionA, sessionB);
        using var viewModel = HistoryViewModelTestData.CreateViewModel(scope, gateway);
        await viewModel.InitializeAsync();
        viewModel.ToggleAdvancedCommand.Execute(null);
        await HistoryViewModelTestData.WaitUntilAsync(() => viewModel.WorkspaceOptions.Count == 2);
        viewModel.SelectedScope = viewModel.ScopeOptions.Single(option => option.Id == "specific");
        viewModel.SelectedWorkspace = viewModel.WorkspaceOptions.Single(option => option.Id == "workspace-a");
        viewModel.SelectedSession = viewModel.SessionOptions.Single(option => option.Id == sessionA.ToString("D"));
        Assert.True(viewModel.IsSpecificSessionSelected);

        viewModel.SelectedWorkspace = viewModel.WorkspaceOptions.Single(option => option.Id == "workspace-b");

        Assert.Equal(string.Empty, viewModel.SelectedSession?.Id);
        Assert.False(viewModel.IsSpecificSessionSelected);
        Assert.Contains(viewModel.SessionOptions, option => option.Id == sessionB.ToString("D"));
        Assert.DoesNotContain(viewModel.SessionOptions, option => option.Id == sessionA.ToString("D"));
    }

    [Fact]
    public async Task DatePresets_UseDeterministicBoundaries()
    {
        using var scope = RegressionTestPackageScope.Create();
        var now = new DateTimeOffset(2026, 7, 25, 15, 30, 0, TimeSpan.Zero);
        var gateway = CreateFilterGateway(Guid.NewGuid(), Guid.NewGuid());
        using var viewModel = HistoryViewModelTestData.CreateViewModel(
            scope,
            gateway,
            new FixedHistoryTimeProvider(now));
        await viewModel.InitializeAsync();

        viewModel.SelectedWhen = viewModel.WhenOptions.Single(option => option.Id == "today");
        await gateway.WaitForSearchAsync(request => request.FromUtc == new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(now, gateway.SearchRequests.Last().ToUtc);

        viewModel.SelectedWhen = viewModel.WhenOptions.Single(option => option.Id == "past-7-days");
        await gateway.WaitForSearchAsync(request => request.FromUtc == now.AddDays(-7));
        Assert.Equal(now, gateway.SearchRequests.Last().ToUtc);

        viewModel.SelectedWhen = viewModel.WhenOptions.Single(option => option.Id == "past-30-days");
        await gateway.WaitForSearchAsync(request => request.FromUtc == now.AddDays(-30));

        viewModel.SelectedWhen = viewModel.WhenOptions.Single(option => option.Id == "past-year");
        await gateway.WaitForSearchAsync(request => request.FromUtc == now.AddYears(-1));
    }

    [Fact]
    public async Task RollingDatePreset_IsSnapshottedAcrossPagingAndRefreshesForANewQuery()
    {
        using var scope = RegressionTestPackageScope.Create();
        var initialNow = new DateTimeOffset(2026, 7, 25, 15, 30, 0, TimeSpan.Zero);
        var timeProvider = new MutableHistoryTimeProvider(initialNow);
        var gateway = new RecordingHistoryGateway
        {
            SearchFactory = (request, _) => Task.FromResult(request switch
            {
                { FromUtc: not null, Continuation: null } => HistoryViewModelTestData.Response(
                    HistoryViewModelTestData.Hit("first"),
                    continuation: "next"),
                { Continuation: "next" } => HistoryViewModelTestData.Response(
                    HistoryViewModelTestData.Hit("second")),
                _ => HistoryViewModelTestData.Response(),
            }),
        };
        using var viewModel = HistoryViewModelTestData.CreateViewModel(scope, gateway, timeProvider);
        await viewModel.InitializeAsync();

        viewModel.SelectedWhen = viewModel.WhenOptions.Single(option => option.Id == "past-7-days");
        var first = await gateway.WaitForSearchAsync(request => request.FromUtc == initialNow.AddDays(-7));
        await HistoryViewModelTestData.WaitUntilAsync(() => viewModel.CanLoadMore);
        timeProvider.Advance(TimeSpan.FromDays(2));

        await viewModel.LoadMoreCommand.ExecuteAsync(null);
        var second = await gateway.WaitForSearchAsync(request => request.Continuation == "next");

        Assert.Equal(first.FromUtc, second.FromUtc);
        Assert.Equal(first.ToUtc, second.ToUtc);

        timeProvider.Advance(TimeSpan.FromHours(3));
        viewModel.QueryText = "fresh range";
        var refreshed = await gateway.WaitForSearchAsync(request => request.Query == "fresh range");

        Assert.Equal(timeProvider.GetUtcNow(), refreshed.ToUtc);
        Assert.Equal(timeProvider.GetUtcNow().AddDays(-7), refreshed.FromUtc);
        Assert.NotEqual(first.ToUtc, refreshed.ToUtc);
    }

    [Fact]
    public void DateBoundaries_HandleInvalidAndAmbiguousMidnightTransitions()
    {
        var daylightStart = TimeZoneInfo.TransitionTime.CreateFixedDateRule(
            new DateTime(1, 1, 1, 0, 0, 0),
            month: 3,
            day: 10);
        var daylightEnd = TimeZoneInfo.TransitionTime.CreateFixedDateRule(
            new DateTime(1, 1, 1, 1, 0, 0),
            month: 11,
            day: 1);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1),
            new DateTime(2026, 12, 31),
            TimeSpan.FromHours(1),
            daylightStart,
            daylightEnd);
        var timeZone = TimeZoneInfo.CreateCustomTimeZone(
            "History-Midnight-Transitions",
            TimeSpan.FromHours(-5),
            "History test time",
            "History standard time",
            "History daylight time",
            [rule]);

        var invalidBoundary = Sunder.Package.Agent.PackageViews.AgentHistorySearchViewModel
            .ResolveLocalBoundaryUtc(new DateTime(2026, 3, 10), timeZone);
        var ambiguousBoundary = Sunder.Package.Agent.PackageViews.AgentHistorySearchViewModel
            .ResolveLocalBoundaryUtc(new DateTime(2026, 11, 1), timeZone);

        Assert.Equal(new DateTimeOffset(2026, 3, 10, 5, 0, 0, TimeSpan.Zero), invalidBoundary);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero), ambiguousBoundary);
    }

    [Fact]
    public async Task MeaningfulStatusTransitionsRefreshOnceAndProgressDoesNotStorm()
    {
        using var scope = RegressionTestPackageScope.Create();
        var gateway = new RecordingHistoryGateway();
        using var viewModel = HistoryViewModelTestData.CreateViewModel(scope, gateway);
        await viewModel.InitializeAsync();
        var searches = gateway.SearchRequests.Count;

        gateway.RaiseStatus(HistoryViewModelTestData.ReadyStatus(revision: 2, pendingChanges: 1));
        gateway.RaiseStatus(HistoryViewModelTestData.ReadyStatus(revision: 3, pendingChanges: 1) with
        {
            ProgressCompleted = 1,
        });
        gateway.RaiseStatus(HistoryViewModelTestData.ReadyStatus(revision: 4, pendingChanges: 1) with
        {
            ProgressCompleted = 2,
        });
        Assert.Equal(searches, gateway.SearchRequests.Count);

        gateway.RaiseStatus(HistoryViewModelTestData.ReadyStatus(revision: 5, pendingChanges: 0));
        await HistoryViewModelTestData.WaitUntilAsync(() => gateway.SearchRequests.Count == searches + 1);
        Assert.Equal("", viewModel.RuntimeNotice);

        searches = gateway.SearchRequests.Count;
        gateway.RaiseStatus(HistoryViewModelTestData.ReadyStatus(revision: 6) with
        {
            Availability = HistorySearchAvailability.Rebuilding,
            TextGeneration = null,
            ProgressCompleted = 0,
        });
        gateway.RaiseStatus(HistoryViewModelTestData.ReadyStatus(revision: 7) with
        {
            Availability = HistorySearchAvailability.Rebuilding,
            TextGeneration = null,
            ProgressCompleted = 20,
        });
        Assert.Equal(searches, gateway.SearchRequests.Count);
        gateway.RaiseStatus(HistoryViewModelTestData.ReadyStatus(revision: 8, textGeneration: 2));
        await HistoryViewModelTestData.WaitUntilAsync(() => gateway.SearchRequests.Count == searches + 1);

        searches = gateway.SearchRequests.Count;
        gateway.RaiseStatus(HistoryViewModelTestData.ReadyStatus(
            revision: 1,
            runtimeInstanceId: "runtime-two",
            textGeneration: 1));
        gateway.RaiseRuntimeChanged();
        await HistoryViewModelTestData.WaitUntilAsync(() => gateway.SearchRequests.Count == searches + 1);
        Assert.Equal(2, gateway.StateRequests.Count(request => !request.IncludeAdvancedFilters));
    }

    [Fact]
    public async Task ProjectionRevisionRefreshesOnceAndProgressWithSameProjectionDoesNotStorm()
    {
        using var scope = RegressionTestPackageScope.Create();
        var gateway = new RecordingHistoryGateway();
        using var viewModel = HistoryViewModelTestData.CreateViewModel(scope, gateway);
        await viewModel.InitializeAsync();
        var searches = gateway.SearchRequests.Count;

        gateway.RaiseStatus(HistoryViewModelTestData.ReadyStatus(
            revision: 2,
            projectionRevision: 1));
        await HistoryViewModelTestData.WaitUntilAsync(() => gateway.SearchRequests.Count == searches + 1);
        searches = gateway.SearchRequests.Count;
        gateway.RaiseStatus(HistoryViewModelTestData.ReadyStatus(
            revision: 3,
            projectionRevision: 1) with
        { ProgressCompleted = 1 });
        gateway.RaiseStatus(HistoryViewModelTestData.ReadyStatus(
            revision: 4,
            projectionRevision: 1) with
        { ProgressCompleted = 2 });
        await Task.Delay(20);

        Assert.Equal(searches, gateway.SearchRequests.Count);
    }

    [Fact]
    public async Task PersistedWorkspaceChangeRefreshesCachedCurrentWorkspaceScope()
    {
        using var scope = RegressionTestPackageScope.Create();
        var selection = new AgentChatSelectionStateService(scope.Context);
        await selection.SaveSelectedWorkspaceIdAsync("workspace-a");
        var gateway = new RecordingHistoryGateway
        {
            StateFactory = request => HistoryViewModelTestData.State(
                workspaces: request.WorkspaceId is null
                    ? []
                    : [new(request.WorkspaceId, request.WorkspaceId == "workspace-a" ? "Workspace A" : "Workspace B")]),
        };
        using var viewModel = new Sunder.Package.Agent.PackageViews.AgentHistorySearchViewModel(
            gateway,
            selection,
            new CapturingHistoryShellViewService());
        await viewModel.InitializeAsync();

        await selection.SaveSelectedWorkspaceIdAsync("workspace-b");
        await gateway.WaitForSearchAsync(request => request.WorkspaceId == "workspace-b");

        Assert.Contains(gateway.StateRequests, request => !request.IncludeAdvancedFilters
                                                          && request.WorkspaceId == "workspace-b");
        Assert.Equal("Current workspace: Workspace B", viewModel.ScopeContextText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpecificWorkspaceResnapshotPreservesExplicitWorkspaceWhileMetadataReloads(
        bool advancedRemainsOpen)
    {
        using var scope = RegressionTestPackageScope.Create();
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        var selection = new AgentChatSelectionStateService(scope.Context);
        await selection.SaveSelectedWorkspaceIdAsync("workspace-a");
        var gateway = CreateFilterGateway(sessionA, sessionB);
        using var viewModel = new Sunder.Package.Agent.PackageViews.AgentHistorySearchViewModel(
            gateway,
            selection,
            new CapturingHistoryShellViewService());
        await viewModel.InitializeAsync();
        viewModel.IsAdvancedExpanded = true;
        await HistoryViewModelTestData.WaitUntilAsync(() => viewModel.WorkspaceOptions.Count == 2);
        viewModel.SelectedScope = viewModel.ScopeOptions.Single(option => option.Id == "specific");
        viewModel.SelectedWorkspace = viewModel.WorkspaceOptions.Single(option => option.Id == "workspace-b");
        await gateway.WaitForSearchAsync(request => request.WorkspaceId == "workspace-b");
        await HistoryViewModelTestData.WaitUntilAsync(() => !viewModel.IsSearching);
        if (!advancedRemainsOpen)
        {
            viewModel.IsAdvancedExpanded = false;
        }
        await Task.Delay(20);
        var searches = gateway.SearchRequests.Count;

        await selection.SaveSelectedWorkspaceIdAsync("workspace-new-current");
        await HistoryViewModelTestData.WaitUntilAsync(() => gateway.SearchRequests.Count == searches + 1);
        if (advancedRemainsOpen)
        {
            await HistoryViewModelTestData.WaitUntilAsync(() => !viewModel.IsAdvancedLoading);
        }

        Assert.Equal("workspace-b", gateway.SearchRequests[searches].WorkspaceId);
        Assert.Equal("workspace-b", viewModel.SelectedWorkspace?.Id);
        await Task.Delay(20);
        Assert.Equal(searches + 1, gateway.SearchRequests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentWorkspaceNotificationClearsIncompatibleSessionBeforeSearch(
        bool advancedRemainsOpen)
    {
        using var scope = RegressionTestPackageScope.Create();
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        var selection = new AgentChatSelectionStateService(scope.Context);
        await selection.SaveSelectedWorkspaceIdAsync("workspace-a");
        var gateway = CreateFilterGateway(sessionA, sessionB);
        using var viewModel = new Sunder.Package.Agent.PackageViews.AgentHistorySearchViewModel(
            gateway,
            selection,
            new CapturingHistoryShellViewService());
        await viewModel.InitializeAsync();
        viewModel.IsAdvancedExpanded = true;
        await HistoryViewModelTestData.WaitUntilAsync(() => viewModel.SessionOptions.Any(option => option.Id == sessionA.ToString("D")));
        viewModel.SelectedSession = viewModel.SessionOptions.Single(option => option.Id == sessionA.ToString("D"));
        viewModel.IncludeChildSessions = false;
        await gateway.WaitForSearchAsync(request => request.SessionId == sessionA && !request.IncludeChildSessions);
        await HistoryViewModelTestData.WaitUntilAsync(() => !viewModel.IsSearching);
        if (!advancedRemainsOpen)
        {
            viewModel.IsAdvancedExpanded = false;
        }
        await Task.Delay(20);
        var searches = gateway.SearchRequests.Count;

        await selection.SaveSelectedWorkspaceIdAsync("workspace-b");
        await HistoryViewModelTestData.WaitUntilAsync(() => gateway.SearchRequests.Count == searches + 1);
        var request = gateway.SearchRequests[searches];

        Assert.Equal("workspace-b", request.WorkspaceId);
        Assert.Null(request.SessionId);
        Assert.True(request.IncludeChildSessions);
        Assert.Equal(string.Empty, viewModel.SelectedSession?.Id);
        Assert.True(viewModel.IncludeChildSessions);
        await Task.Delay(20);
        Assert.Equal(searches + 1, gateway.SearchRequests.Count);
    }

    [Fact]
    public async Task MissingSpecificWorkspaceResetsVisiblyAndResearchesOnceAfterMetadataLoad()
    {
        using var scope = RegressionTestPackageScope.Create();
        var includeWorkspaceB = true;
        var selection = new AgentChatSelectionStateService(scope.Context);
        await selection.SaveSelectedWorkspaceIdAsync("workspace-a");
        var gateway = new RecordingHistoryGateway
        {
            StateFactory = request => request.IncludeAdvancedFilters
                ? HistoryViewModelTestData.State(
                    workspaces: includeWorkspaceB
                        ? [new("workspace-a", "Workspace A"), new("workspace-b", "Workspace B")]
                        : [new("workspace-c", "Workspace C")])
                : HistoryViewModelTestData.State(
                    workspaces: request.WorkspaceId is null
                        ? []
                        : [new(request.WorkspaceId, request.WorkspaceId)]),
        };
        using var viewModel = new Sunder.Package.Agent.PackageViews.AgentHistorySearchViewModel(
            gateway,
            selection,
            new CapturingHistoryShellViewService());
        await viewModel.InitializeAsync();
        viewModel.IsAdvancedExpanded = true;
        await HistoryViewModelTestData.WaitUntilAsync(() => viewModel.WorkspaceOptions.Count == 2);
        viewModel.SelectedScope = viewModel.ScopeOptions.Single(option => option.Id == "specific");
        viewModel.SelectedWorkspace = viewModel.WorkspaceOptions.Single(option => option.Id == "workspace-b");
        await gateway.WaitForSearchAsync(request => request.WorkspaceId == "workspace-b");
        await HistoryViewModelTestData.WaitUntilAsync(() => !viewModel.IsSearching);
        await Task.Delay(20);
        var searches = gateway.SearchRequests.Count;
        includeWorkspaceB = false;

        await selection.SaveSelectedWorkspaceIdAsync("workspace-c");
        await HistoryViewModelTestData.WaitUntilAsync(() => gateway.SearchRequests.Count == searches + 2);

        Assert.Equal("workspace-b", gateway.SearchRequests[searches].WorkspaceId);
        Assert.Equal("workspace-c", gateway.SearchRequests[searches + 1].WorkspaceId);
        Assert.Equal("workspace-c", viewModel.SelectedWorkspace?.Id);
        Assert.Equal("Workspace: Workspace C", viewModel.ScopeContextText);
    }

    [Fact]
    public async Task SpecificWorkspaceScopeFailsClosedWhenAdvancedMetadataCannotLoad()
    {
        using var scope = RegressionTestPackageScope.Create();
        var selection = new AgentChatSelectionStateService(scope.Context);
        await selection.SaveSelectedWorkspaceIdAsync("workspace-current");
        var gateway = new RecordingHistoryGateway
        {
            StateAsyncFactory = (request, _) => request.IncludeAdvancedFilters
                ? Task.FromException<HistorySearchState>(new InvalidOperationException("expected"))
                : Task.FromResult(HistoryViewModelTestData.State()),
        };
        using var viewModel = new Sunder.Package.Agent.PackageViews.AgentHistorySearchViewModel(
            gateway,
            selection,
            new CapturingHistoryShellViewService());
        await viewModel.InitializeAsync();
        viewModel.IsAdvancedExpanded = true;
        await HistoryViewModelTestData.WaitUntilAsync(() => viewModel.HasRuntimeNotice);

        var searches = gateway.SearchRequests.Count;
        viewModel.SelectedScope = viewModel.ScopeOptions.Single(option => option.Id == "specific");
        await HistoryViewModelTestData.WaitUntilAsync(() => gateway.SearchRequests.Count > searches);
        var request = gateway.SearchRequests.Last();

        Assert.Equal("workspace-current", request.WorkspaceId);
    }

    private static RecordingHistoryGateway CreateFilterGateway(Guid sessionA, Guid sessionB)
        => new()
        {
            StateFactory = request => request.IncludeAdvancedFilters
                ? HistoryViewModelTestData.State(
                    workspaces:
                    [
                        new("workspace-a", "Workspace A"),
                        new("workspace-b", "Workspace B"),
                    ],
                    sessions:
                    [
                        new(sessionA.ToString("D"), "Session A", "workspace-a"),
                        new(sessionB.ToString("D"), "Session B", "workspace-b"),
                    ],
                    profiles:
                    [
                        new("profile-a", "Profile A"),
                        new("profile-b", "Profile B"),
                    ])
                : HistoryViewModelTestData.State(),
        };

    private sealed class FixedHistoryTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class MutableHistoryTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        internal void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
