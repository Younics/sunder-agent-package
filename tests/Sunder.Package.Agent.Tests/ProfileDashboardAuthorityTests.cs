extern alias AgentCore;

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AgentCore::Sunder.Package.Agent.Contracts.Models;
using AgentCore::Sunder.Package.Agent.PackageViews;
using AgentCore::Sunder.Package.Agent.Runtime;
using Xunit;
using AgentPresentationDispatcher = AgentCore::Sunder.Package.Agent.Shared.Presentation.IPresentationDispatcher;
using static Sunder.Package.Agent.Tests.RuntimeSnapshotFixture;

namespace Sunder.Package.Agent.Tests;

[Collection("Runtime correctness")]
public sealed class ProfileDashboardAuthorityTests
{
    [Fact]
    public async Task AgentProfilesViewModel_ChatBeforeProfiles_LoadsAndPreservesAuthoritativeAssignments()
    {
        var fixture = CreateProfileDashboardFixture(revision: 31);
        var client = new ProfileDashboardRuntimeClient(
            fixture.Snapshot,
            fixture.Dashboard,
            fixture.Catalog);
        using var gateway = new AgentAppRuntimeGateway(client);
        var snapshot = await gateway.LoadChatSnapshotAsync(new AgentChatSnapshotRequest());
        Assert.Empty(snapshot.SelectedProfile?.SelectableCapabilityAssignments ?? []);
        gateway.CompleteChatSnapshot(snapshot, applied: true);
        using var viewModel = new AgentProfilesViewModel(client, gateway);

        await viewModel.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => !viewModel.IsBusy
                                   && viewModel.SelectedProfile?.ProfileId == fixture.Profile.ProfileId
                                   && viewModel.PackageCapabilities.Count == 1);

        Assert.Equal(1, client.ProfileQueryCount);
        Assert.Equal(fixture.Profile.Description, viewModel.Description);
        Assert.Equal(fixture.Profile.Instructions, viewModel.Instructions);
        Assert.True(Assert.Single(viewModel.PackageCapabilities).IsEnabled);
        Assert.Equal(fixture.Assignment, Assert.Single(viewModel.Capabilities.Assignments));
        Assert.Contains(client.CatalogRequests, request =>
            request.Profile?.SelectableCapabilityAssignments?.Contains(fixture.Assignment) == true);

        viewModel.DisplayName = "Updated authoritative profile";
        await viewModel.SaveProfileCommand.ExecuteAsync(null);

        var save = Assert.Single(client.ProfileCommands, command => command.Kind == AgentProfileCommandKind.Save);
        Assert.Equal(fixture.Profile.Description, save.Description);
        Assert.Equal(fixture.Profile.Instructions, save.Instructions);
        Assert.Equal(fixture.Assignment, Assert.Single(save.Assignments ?? []));
        Assert.Equal(2, client.ProfileQueryCount);
    }

    [Fact]
    public async Task Gateway_ProfilesBeforeChat_DoesNotReplaceAuthoritativeDashboard()
    {
        var fixture = CreateProfileDashboardFixture(revision: 31);
        var client = new ProfileDashboardRuntimeClient(
            fixture.Snapshot,
            fixture.Dashboard,
            fixture.Catalog);
        using var gateway = new AgentAppRuntimeGateway(client);

        var initialDashboard = await gateway.LoadDashboardAsync();
        var snapshot = await gateway.LoadChatSnapshotAsync(new AgentChatSnapshotRequest());
        gateway.CompleteChatSnapshot(snapshot, applied: true);
        var dashboardAfterChat = await gateway.LoadDashboardAsync();

        Assert.Equal(fixture.Assignment, Assert.Single(Assert.Single(initialDashboard.Profiles)
            .SelectableCapabilityAssignments ?? []));
        var profileAfterChat = Assert.Single(dashboardAfterChat.Profiles);
        Assert.Equal(fixture.Profile.Description, profileAfterChat.Description);
        Assert.Equal(fixture.Profile.Instructions, profileAfterChat.Instructions);
        Assert.Equal(fixture.Assignment, Assert.Single(profileAfterChat.SelectableCapabilityAssignments ?? []));
        Assert.Equal(2, client.DashboardInvocationCount);
    }

    [Fact]
    public async Task Gateway_DashboardRefreshDoesNotAdvanceActiveChatChangeCursor()
    {
        var fixture = CreateProfileDashboardFixture(revision: 31);
        var client = new ActiveChatDashboardCursorRuntimeClient(
            fixture.Snapshot,
            fixture.Dashboard with { Revision = 40 });
        using var gateway = new AgentAppRuntimeGateway(client);
        var profileChanged = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ProfileChanged += profileId => profileChanged.TrySetResult(profileId);
        var snapshot = await gateway.LoadChatSnapshotAsync(new AgentChatSnapshotRequest());
        gateway.CompleteChatSnapshot(snapshot, applied: true);
        await client.SubscriptionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        _ = await gateway.LoadDashboardAsync();
        client.ReleaseChange();
        var profileId = await profileChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(fixture.Profile.ProfileId, profileId);
        Assert.Equal(fixture.Snapshot.Revision, client.SubscriptionAfterRevision);
    }

    [Fact]
    public async Task Gateway_DashboardRefreshDoesNotAdvancePendingChatHandoffCursor()
    {
        var fixture = CreateProfileDashboardFixture(revision: 31);
        var client = new ActiveChatDashboardCursorRuntimeClient(
            fixture.Snapshot,
            fixture.Dashboard with { Revision = 40 },
            blockSnapshot: true);
        using var gateway = new AgentAppRuntimeGateway(client);
        var applyChange = typeof(AgentAppRuntimeGateway).GetMethod(
            "ApplyCurrentChange",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        _ = applyChange.Invoke(gateway, [new AgentRuntimeChange(
            30,
            AgentRuntimeChangeKind.Connected,
            RuntimeInstanceId: fixture.Snapshot.RuntimeInstanceId)]);
        var profileChanged = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ProfileChanged += profileId => profileChanged.TrySetResult(profileId);
        var snapshotLoad = gateway.LoadChatSnapshotAsync(new AgentChatSnapshotRequest());
        await client.SnapshotStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        _ = await gateway.LoadDashboardAsync();
        client.ReleaseSnapshot();
        var snapshot = await snapshotLoad;
        gateway.CompleteChatSnapshot(snapshot, applied: true);
        await client.SubscriptionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        client.ReleaseChange();
        var profileId = await profileChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(fixture.Profile.ProfileId, profileId);
        Assert.Equal(fixture.Snapshot.Revision, client.SubscriptionAfterRevision);
    }

    [Fact]
    public async Task AgentProfilesViewModel_NewerChatSnapshotRefreshesAuthoritativeProfile()
    {
        var original = CreateProfileDashboardFixture(
            revision: 31,
            displayName: "Original profile");
        var current = CreateProfileDashboardFixture(
            revision: 32,
            displayName: "Current profile");
        var client = new ProfileDashboardRuntimeClient(
            original.Snapshot,
            original.Dashboard,
            original.Catalog);
        using var gateway = new AgentAppRuntimeGateway(client);
        using var viewModel = new AgentProfilesViewModel(client, gateway);
        await viewModel.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => viewModel.DisplayName == original.Profile.DisplayName && !viewModel.IsBusy);
        var reenteredHydration = false;
        viewModel.PropertyChanged += (_, _) => reenteredHydration |= viewModel.IsHydrating;

        client.SetState(current.Snapshot, current.Dashboard, current.Catalog);
        var snapshot = await gateway.LoadChatSnapshotAsync(new AgentChatSnapshotRequest());
        gateway.CompleteChatSnapshot(snapshot, applied: true);
        await WaitUntilAsync(() => viewModel.DisplayName == current.Profile.DisplayName
                                   && !viewModel.IsBusy
                                   && client.ProfileQueryCount >= 2);

        Assert.Equal(current.Profile.Description, viewModel.Description);
        Assert.Equal(current.Profile.Instructions, viewModel.Instructions);
        Assert.True(Assert.Single(viewModel.PackageCapabilities).IsEnabled);
        Assert.Equal(current.Assignment, Assert.Single(viewModel.Capabilities.Assignments));
        Assert.False(reenteredHydration);
    }

    [Fact]
    public async Task AgentProfilesViewModel_CatalogOnlyChatRevisionRefreshesCapabilities()
    {
        var fixture = CreateProfileDashboardFixture(revision: 31);
        var updatedDescriptor = Assert.Single(fixture.Catalog.SelectableCapabilities) with
        {
            DisplayName = "Updated read file",
        };
        var updatedSnapshot = fixture.Snapshot with
        {
            Revision = 32,
            InitialTranscript = fixture.Snapshot.InitialTranscript with { Revision = 32 },
        };
        var updatedDashboard = fixture.Dashboard with { Revision = 32 };
        var updatedCatalog = fixture.Catalog with
        {
            Revision = 32,
            SelectableCapabilities = [updatedDescriptor],
        };
        var client = new ProfileDashboardRuntimeClient(
            fixture.Snapshot,
            fixture.Dashboard,
            fixture.Catalog);
        using var gateway = new AgentAppRuntimeGateway(client);
        var initialSnapshot = await gateway.LoadChatSnapshotAsync(new AgentChatSnapshotRequest());
        gateway.CompleteChatSnapshot(initialSnapshot, applied: true);
        using var viewModel = new AgentProfilesViewModel(client, gateway);
        await viewModel.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => viewModel.PackageCapabilities.Count == 1
                                    && viewModel.PackageCapabilities[0].DisplayName == "Read file");
        var retainedOption = Assert.Single(viewModel.PackageCapabilities);
        var retainedGroup = Assert.Single(viewModel.CapabilityGroups);
        var becameBusy = false;
        viewModel.PropertyChanged += (_, _) => becameBusy |= viewModel.IsBusy;

        client.SetState(updatedSnapshot, updatedDashboard, updatedCatalog);
        var snapshot = await gateway.LoadChatSnapshotAsync(new AgentChatSnapshotRequest());
        gateway.CompleteChatSnapshot(snapshot, applied: true);
        await WaitUntilAsync(() => viewModel.PackageCapabilities.Count == 1
                                   && viewModel.PackageCapabilities[0].DisplayName
                                   == updatedDescriptor.DisplayName);

        Assert.True(Assert.Single(viewModel.PackageCapabilities).IsEnabled);
        Assert.Equal(fixture.Assignment, Assert.Single(viewModel.Capabilities.Assignments));
        Assert.Same(retainedOption, Assert.Single(viewModel.PackageCapabilities));
        Assert.Same(retainedGroup, Assert.Single(viewModel.CapabilityGroups));
        Assert.False(becameBusy);
    }

    [Fact]
    public async Task AgentProfilesViewModel_RepeatedEquivalentRpcSnapshotsDoNotRehydrateTheEditor()
    {
        var fixture = CreateProfileDashboardFixture(revision: 31);
        var client = new ProfileDashboardRuntimeClient(fixture.Snapshot, fixture.Dashboard, fixture.Catalog);
        using var gateway = new AgentAppRuntimeGateway(client);
        using var viewModel = new AgentProfilesViewModel(client, gateway);
        await viewModel.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => !viewModel.IsBusy && viewModel.PackageCapabilities.Count == 1);
        var selected = viewModel.SelectedProfile;
        var option = Assert.Single(viewModel.PackageCapabilities);
        var group = Assert.Single(viewModel.CapabilityGroups);
        viewModel.Instructions = "Unsaved instructions";
        var becameBusy = false;
        viewModel.PropertyChanged += (_, _) => becameBusy |= viewModel.IsBusy;

        for (var revision = 32; revision <= 34; revision++)
        {
            var profile = fixture.Profile with
            {
                ModelBindings = [],
                SelectableCapabilityAssignments = [fixture.Assignment with { }],
            };
            var snapshot = fixture.Snapshot with { Revision = revision };
            client.SetState(snapshot, fixture.Dashboard with { Revision = revision, Profiles = [profile] },
                fixture.Catalog with { Revision = revision });
            var queries = client.ProfileQueryCount;
            gateway.CompleteChatSnapshot(await gateway.LoadChatSnapshotAsync(new AgentChatSnapshotRequest()), applied: true);
            await WaitUntilAsync(() => client.ProfileQueryCount > queries);
            await viewModel.CurrentRuntimeRefresh.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(selected, viewModel.SelectedProfile);
            Assert.Same(option, Assert.Single(viewModel.PackageCapabilities));
            Assert.Same(group, Assert.Single(viewModel.CapabilityGroups));
        }

        Assert.False(becameBusy);
        Assert.True(viewModel.IsDirty);
        Assert.Equal("Unsaved instructions", viewModel.Instructions);
    }

    [Fact]
    public async Task AgentProfilesViewModel_CatalogBurstCoalescesWithoutBlockingEditsOrRestoringOldSelections()
    {
        var fixture = CreateProfileDashboardFixture(revision: 31);
        var client = new ProfileDashboardRuntimeClient(fixture.Snapshot, fixture.Dashboard, fixture.Catalog);
        using var gateway = new AgentAppRuntimeGateway(client);
        using var model = new AgentProfilesViewModel(client, gateway, null, new InlineProfileDispatcher());
        await model.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await model.CurrentCapabilityRefresh.WaitAsync(TimeSpan.FromSeconds(5));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.BeforeCatalog = async (request, token) =>
        {
            if (request.Profile is null) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var before = client.CatalogRequests.Count;
        var applyChange = typeof(AgentAppRuntimeGateway).GetMethod("ApplyCurrentChange", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            _ = applyChange.Invoke(gateway, [new AgentRuntimeChange(32, AgentRuntimeChangeKind.Catalog)]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(model.IsBusy);
            Assert.True(model.IsEditorEnabled);
            var option = Assert.Single(model.PackageCapabilities);
            option.IsEnabled = false;
            for (var revision = 33; revision <= 42; revision++)
                _ = applyChange.Invoke(gateway, [new AgentRuntimeChange(revision, AgentRuntimeChangeKind.Catalog)]);
            release.TrySetResult();
            await model.CurrentCapabilityRefresh.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(4, client.CatalogRequests.Count - before); // One active refresh and one trailing refresh, two queries each.
            Assert.Same(option, Assert.Single(model.PackageCapabilities));
            Assert.False(option.IsEnabled);
            Assert.True(model.IsDirty);
            Assert.False(model.IsBusy);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gateway_DetachedDashboardLoadRetriesAfterNewerChatSnapshot(bool failDetachedLoad)
    {
        var stale = CreateProfileDashboardFixture(
            revision: 30,
            displayName: "Stale profile");
        var current = CreateProfileDashboardFixture(
            revision: 31,
            displayName: "Current profile");
        var client = new DetachedDashboardRuntimeClient(
            current.Snapshot,
            stale.Dashboard,
            current.Dashboard,
            failDetachedLoad);
        using var gateway = new AgentAppRuntimeGateway(client);
        var dashboardLoad = gateway.LoadDashboardAsync();
        await client.FirstDashboardStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var snapshot = await gateway.LoadChatSnapshotAsync(new AgentChatSnapshotRequest());
        gateway.CompleteChatSnapshot(snapshot, applied: true);
        client.ReleaseFirstDashboard();
        var dashboard = await dashboardLoad.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Current profile", Assert.Single(dashboard.Profiles).DisplayName);
        Assert.Equal(current.Assignment, Assert.Single(Assert.Single(dashboard.Profiles)
            .SelectableCapabilityAssignments ?? []));
        Assert.Equal(2, client.DashboardInvocationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gateway_DetachedCatalogLoadRetriesAfterRuntimeReplacement(bool failDetachedLoad)
    {
        var original = CreateProfileDashboardFixture(
            revision: 100,
            runtimeInstanceId: "runtime-1");
        var replacement = CreateProfileDashboardFixture(
            revision: 1,
            runtimeInstanceId: "runtime-2");
        var client = new DetachedCatalogRuntimeClient(
            original.Snapshot,
            replacement.Snapshot,
            original.Catalog,
            replacement.Catalog,
            failDetachedLoad);
        using var gateway = new AgentAppRuntimeGateway(client);
        var originalSnapshot = await gateway.LoadChatSnapshotAsync(new AgentChatSnapshotRequest());
        gateway.CompleteChatSnapshot(originalSnapshot, applied: true);
        var catalogLoad = gateway.LoadCatalogAsync(new AgentCatalogRequest());
        await client.FirstCatalogStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var replacementSnapshot = await gateway.LoadChatSnapshotAsync(new AgentChatSnapshotRequest());
        gateway.CompleteChatSnapshot(replacementSnapshot, applied: true);
        client.ReleaseFirstCatalog();
        var catalog = await catalogLoad.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(replacement.Catalog, catalog);
        Assert.Equal(2, client.CatalogInvocationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gateway_RuntimeReplacementInvalidatesDashboardAtLowerRevision(
        bool establishOriginalRuntimeIdentity)
    {
        var original = CreateProfileDashboardFixture(
            revision: 100,
            runtimeInstanceId: "runtime-1",
            displayName: "Original runtime profile");
        var replacement = CreateProfileDashboardFixture(
            revision: 1,
            runtimeInstanceId: "runtime-2",
            displayName: "Replacement runtime profile");
        var client = new ProfileDashboardRuntimeClient(
            original.Snapshot,
            original.Dashboard,
            original.Catalog);
        using var gateway = new AgentAppRuntimeGateway(client);
        if (establishOriginalRuntimeIdentity)
        {
            var originalSnapshot = await gateway.LoadChatSnapshotAsync(new AgentChatSnapshotRequest());
            gateway.CompleteChatSnapshot(originalSnapshot, applied: true);
        }
        _ = await gateway.LoadDashboardAsync();

        client.SetState(replacement.Snapshot, replacement.Dashboard, replacement.Catalog);
        var replacementSnapshot = await gateway.LoadChatSnapshotAsync(new AgentChatSnapshotRequest());
        gateway.CompleteChatSnapshot(replacementSnapshot, applied: true);
        var dashboard = await gateway.LoadDashboardAsync();

        Assert.Equal("Replacement runtime profile", Assert.Single(dashboard.Profiles).DisplayName);
        Assert.Equal(replacement.Assignment, Assert.Single(Assert.Single(dashboard.Profiles)
            .SelectableCapabilityAssignments ?? []));
        Assert.Equal(2, client.DashboardInvocationCount);
    }

    [Fact]
    public async Task Gateway_ResnapshotReusesActivatedChatRequestWithoutDashboardFallback()
    {
        var fixture = CreateProfileDashboardFixture(revision: 31);
        var initial = fixture.Snapshot;
        var refreshed = initial with
        {
            Revision = 32,
            InitialTranscript = initial.InitialTranscript with { Revision = 32 },
        };
        var dashboard = fixture.Dashboard with { Revision = refreshed.Revision };
        var client = new ResnapshotChatRuntimeClient(initial, refreshed, dashboard);
        using var gateway = new AgentAppRuntimeGateway(client);
        var request = new AgentChatSnapshotRequest(
            42,
            initial.SelectedProfile?.ProfileId,
            initial.SelectedWorkspace?.WorkspaceId,
            initial.SelectedSession?.Session.SessionId);
        var reloaded = new TaskCompletionSource<AgentChatSnapshotProjection>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ChatSnapshotReloaded += snapshot => reloaded.TrySetResult(snapshot);

        var snapshot = await gateway.LoadChatSnapshotAsync(request);
        gateway.CompleteChatSnapshot(snapshot, applied: true);
        var reconnectSnapshot = await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(refreshed.Revision, reconnectSnapshot.Revision);
        Assert.Equal(2, client.ChatSnapshotRequests.Count);
        Assert.All(client.ChatSnapshotRequests, actual => Assert.Equal(request, actual));
        Assert.Equal(0, client.DashboardInvocationCount);
        var reloadedDashboard = await gateway.LoadDashboardAsync();
        Assert.Equal(fixture.Assignment, Assert.Single(Assert.Single(reloadedDashboard.Profiles)
            .SelectableCapabilityAssignments ?? []));
        Assert.Equal(1, client.DashboardInvocationCount);
    }

    private sealed class ProfileDashboardRuntimeClient(
        AgentChatSnapshotProjection snapshot,
        AgentDashboardProjection dashboard,
        AgentCatalogProjection catalog)
        : OwnerServiceStub
    {
        private readonly object _sync = new();
        private AgentChatSnapshotProjection _snapshot = snapshot;
        private AgentDashboardProjection _dashboard = dashboard;
        private AgentCatalogProjection _catalog = catalog;
        private int _dashboardInvocationCount;
        private int _profileQueryCount;

        public bool IsAvailable => true;
        public int DashboardInvocationCount => Volatile.Read(ref _dashboardInvocationCount);
        public int ProfileQueryCount => Volatile.Read(ref _profileQueryCount);
        public ConcurrentQueue<AgentCatalogRequest> CatalogRequests { get; } = new();
        public ConcurrentQueue<AgentProfileCommand> ProfileCommands { get; } = new();
        public Func<AgentCatalogRequest, CancellationToken, Task>? BeforeCatalog { get; set; }

        public override Task<IReadOnlyList<AgentProfileRecord>> ListProfilesAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _profileQueryCount);
            lock (_sync) return Task.FromResult(_dashboard.Profiles);
        }

        public void SetState(
            AgentChatSnapshotProjection nextSnapshot,
            AgentDashboardProjection nextDashboard,
            AgentCatalogProjection nextCatalog)
        {
            lock (_sync)
            {
                _snapshot = nextSnapshot;
                _dashboard = nextDashboard;
                _catalog = nextCatalog;
            }
        }

        public override ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request is AgentChatSnapshotRequest)
            {
                lock (_sync)
                {
                    return ValueTask.FromResult((TResponse)(object)_snapshot);
                }
            }
            if (request is AgentDashboardRequest)
            {
                Interlocked.Increment(ref _dashboardInvocationCount);
                lock (_sync)
                {
                    return ValueTask.FromResult((TResponse)(object)_dashboard);
                }
            }
            if (request is AgentCatalogRequest)
            {
                CatalogRequests.Enqueue((AgentCatalogRequest)(object)request);
                if (BeforeCatalog is { } before)
                    return new ValueTask<TResponse>(ReadCatalogAsync((AgentCatalogRequest)(object)request, before, cancellationToken));
                lock (_sync)
                {
                    return ValueTask.FromResult((TResponse)(object)_catalog);
                }
            }
            if (request is AgentProfileCommand)
            {
                var command = (AgentProfileCommand)(object)request;
                ProfileCommands.Enqueue(command);
                if (command.Kind != AgentProfileCommandKind.Save)
                {
                    return ValueTask.FromException<TResponse>(new NotSupportedException(command.Kind.ToString()));
                }

                lock (_sync)
                {
                    var current = Assert.Single(_dashboard.Profiles);
                    var updated = current with
                    {
                        DisplayName = command.DisplayName ?? current.DisplayName,
                        Description = command.Description,
                        Instructions = command.Instructions,
                        SelectableCapabilityAssignments = command.Assignments,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                    };
                    _dashboard = _dashboard with
                    {
                        Revision = _dashboard.Revision + 1,
                        Profiles = [updated],
                    };
                    return ValueTask.FromResult((TResponse)(object)new AgentProfileCommandResult(
                        _dashboard.Revision,
                        updated));
                }
            }
            return ValueTask.FromException<TResponse>(new NotSupportedException(typeof(TRequest).Name));

            async Task<TResponse> ReadCatalogAsync(AgentCatalogRequest catalogRequest,
                Func<AgentCatalogRequest, CancellationToken, Task> before, CancellationToken token)
            {
                await before(catalogRequest, token);
                lock (_sync) return (TResponse)(object)_catalog;
            }
        }

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class InlineProfileDispatcher : AgentPresentationDispatcher
    {
        public bool CheckAccess() => true;
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    }

    private sealed class ActiveChatDashboardCursorRuntimeClient(
        AgentChatSnapshotProjection snapshot,
        AgentDashboardProjection dashboard,
        bool blockSnapshot = false)
        : OwnerServiceStub
    {
        private readonly TaskCompletionSource _releaseChange = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSnapshot = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAvailable => true;
        public long SubscriptionAfterRevision { get; private set; } = -1;
        public TaskCompletionSource SubscriptionStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SnapshotStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseChange() => _releaseChange.TrySetResult();
        public void ReleaseSnapshot() => _releaseSnapshot.TrySetResult();

        public override async ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request is AgentChatSnapshotRequest)
            {
                SnapshotStarted.TrySetResult();
                if (blockSnapshot)
                {
                    await _releaseSnapshot.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                return (TResponse)(object)snapshot;
            }
            if (request is AgentDashboardRequest)
            {
                return (TResponse)(object)dashboard;
            }
            throw new NotSupportedException(typeof(TRequest).Name);
        }

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            SubscriptionAfterRevision = ((AgentChangeSubscription)(object)request).AfterRevision;
            SubscriptionStarted.TrySetResult();
            await _releaseChange.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield return (TEvent)(object)new AgentRuntimeChange(
                snapshot.Revision + 1,
                AgentRuntimeChangeKind.Profile,
                ProfileId: snapshot.SelectedProfile?.ProfileId);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class DetachedDashboardRuntimeClient(
        AgentChatSnapshotProjection snapshot,
        AgentDashboardProjection staleDashboard,
        AgentDashboardProjection currentDashboard,
        bool failFirstDashboard)
        : OwnerServiceStub
    {
        private readonly TaskCompletionSource _releaseFirstDashboard = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _dashboardInvocationCount;

        public bool IsAvailable => true;
        public int DashboardInvocationCount => Volatile.Read(ref _dashboardInvocationCount);
        public TaskCompletionSource FirstDashboardStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseFirstDashboard() => _releaseFirstDashboard.TrySetResult();

        public override async ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request is AgentChatSnapshotRequest)
            {
                return (TResponse)(object)snapshot;
            }
            if (request is AgentDashboardRequest)
            {
                if (Interlocked.Increment(ref _dashboardInvocationCount) == 1)
                {
                    FirstDashboardStarted.TrySetResult();
                    await _releaseFirstDashboard.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (failFirstDashboard)
                    {
                        throw new InvalidOperationException("The detached dashboard failed.");
                    }
                    return (TResponse)(object)staleDashboard;
                }
                return (TResponse)(object)currentDashboard;
            }
            throw new NotSupportedException(typeof(TRequest).Name);
        }

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class DetachedCatalogRuntimeClient(
        AgentChatSnapshotProjection originalSnapshot,
        AgentChatSnapshotProjection replacementSnapshot,
        AgentCatalogProjection staleCatalog,
        AgentCatalogProjection currentCatalog,
        bool failFirstCatalog)
        : OwnerServiceStub
    {
        private readonly TaskCompletionSource _releaseFirstCatalog = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _catalogInvocationCount;
        private int _chatSnapshotInvocationCount;

        public bool IsAvailable => true;
        public int CatalogInvocationCount => Volatile.Read(ref _catalogInvocationCount);
        public TaskCompletionSource FirstCatalogStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseFirstCatalog() => _releaseFirstCatalog.TrySetResult();

        public override async ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request is AgentChatSnapshotRequest)
            {
                var invocation = Interlocked.Increment(ref _chatSnapshotInvocationCount);
                return (TResponse)(object)(invocation == 1
                    ? originalSnapshot
                    : replacementSnapshot);
            }
            if (request is AgentCatalogRequest)
            {
                if (Interlocked.Increment(ref _catalogInvocationCount) == 1)
                {
                    FirstCatalogStarted.TrySetResult();
                    await _releaseFirstCatalog.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (failFirstCatalog)
                    {
                        throw new InvalidOperationException("The detached catalog failed.");
                    }
                    return (TResponse)(object)staleCatalog;
                }
                return (TResponse)(object)currentCatalog;
            }
            throw new NotSupportedException(typeof(TRequest).Name);
        }

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class ResnapshotChatRuntimeClient(
        AgentChatSnapshotProjection initial,
        AgentChatSnapshotProjection refreshed,
        AgentDashboardProjection dashboard)
        : OwnerServiceStub
    {
        private int _chatSnapshotInvocationCount;
        private int _dashboardInvocationCount;
        private int _resetSent;

        public bool IsAvailable => true;
        public ConcurrentQueue<AgentChatSnapshotRequest> ChatSnapshotRequests { get; } = new();
        public int DashboardInvocationCount => Volatile.Read(ref _dashboardInvocationCount);

        public override ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request is AgentChatSnapshotRequest)
            {
                ChatSnapshotRequests.Enqueue((AgentChatSnapshotRequest)(object)request);
                var response = Interlocked.Increment(ref _chatSnapshotInvocationCount) == 1
                    ? initial
                    : refreshed;
                return ValueTask.FromResult((TResponse)(object)response);
            }
            if (request is AgentDashboardRequest)
            {
                Interlocked.Increment(ref _dashboardInvocationCount);
                return ValueTask.FromResult((TResponse)(object)dashboard);
            }
            return ValueTask.FromException<TResponse>(new NotSupportedException(typeof(TRequest).Name));
        }

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            var subscription = (AgentChangeSubscription)(object)request;
            if (subscription.AfterRevision == initial.Revision
                && Interlocked.Exchange(ref _resetSent, 1) == 0)
            {
                yield return (TEvent)(object)new AgentRuntimeChange(
                    refreshed.Revision,
                    AgentRuntimeChangeKind.ResnapshotRequired);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
