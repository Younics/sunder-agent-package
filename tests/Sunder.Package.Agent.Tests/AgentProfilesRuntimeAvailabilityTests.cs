extern alias AgentCore;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Runtime;
using Xunit;
using AgentPresentationDispatcher = AgentCore::Sunder.Package.Agent.Shared.Presentation.IPresentationDispatcher;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentProfilesRuntimeAvailabilityTests
{
    [Fact]
    public async Task ReconnectWithinGraceDoesNotShowRuntimeNoticeAndReloadsProfiles()
    {
        var timeProvider = new ManualTimerTimeProvider();
        var gateway = new RuntimeAwareProfileGateway();
        using var viewModel = CreateViewModel(gateway, timeProvider);
        await viewModel.InitializeAsync();
        var initialLoads = gateway.ListProfilesCount;

        gateway.SetConnectionState(AgentRuntimeConnectionState.Reconnecting);
        gateway.SetConnectionState(AgentRuntimeConnectionState.Connected);
        timeProvider.FireAll();
        await WaitUntilAsync(() => gateway.ListProfilesCount > initialLoads);

        Assert.False(viewModel.HasRuntimeNotice);
        Assert.Empty(viewModel.RuntimeNoticeText);
    }

    [Fact]
    public async Task PersistentOutageShowsRuntimeNoticeAfterGrace()
    {
        var timeProvider = new ManualTimerTimeProvider();
        var gateway = new RuntimeAwareProfileGateway();
        using var viewModel = CreateViewModel(gateway, timeProvider);
        await viewModel.InitializeAsync();

        gateway.SetConnectionState(AgentRuntimeConnectionState.Unavailable);

        Assert.False(viewModel.HasRuntimeNotice);
        timeProvider.FireAll();
        await WaitUntilAsync(() => viewModel.HasRuntimeNotice);

        Assert.Equal("Agent Runtime is unavailable. Reconnecting...", viewModel.RuntimeNoticeText);
        Assert.False(viewModel.HasStatusText);
    }

    [Fact]
    public async Task MissingProfileSaveKeepsHealthyRuntimeConnectedWithoutNotice()
    {
        using var runtime = ProductionProfileRuntime.Create();
        var profile = await runtime.Profiles.CreateProfileAsync("Profile");
        using var gateway = new AgentAppRuntimeGateway(runtime.Client);
        await gateway.InitializeAsync();
        await WaitUntilAsync(() => gateway.ConnectionState == AgentRuntimeConnectionState.Connected);
        var timeProvider = new ManualTimerTimeProvider();
        using var viewModel = CreateViewModel(gateway, timeProvider);
        await viewModel.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => viewModel.SelectedProfile?.ProfileId == profile.ProfileId && !viewModel.IsBusy);
        runtime.Store.DeleteProfile(profile.ProfileId);
        viewModel.DisplayName = "Stale Profile";

        await viewModel.SaveProfileCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));
        timeProvider.FireAll();
        await Task.Yield();

        Assert.Contains("was not found", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Equal(AgentProfileStatusKind.Error, viewModel.StatusKind);
        Assert.Equal(AgentRuntimeConnectionState.Connected, gateway.ConnectionState);
        Assert.False(viewModel.HasRuntimeNotice);
    }

    [Fact]
    public async Task TransportFailureTransitionsHealthyRuntimeToUnavailable()
    {
        using var runtime = ProductionProfileRuntime.Create(
            new HttpRequestException("Injected Runtime transport failure."));
        using var gateway = new AgentAppRuntimeGateway(runtime.Client);
        await gateway.InitializeAsync();
        await WaitUntilAsync(() => gateway.ConnectionState == AgentRuntimeConnectionState.Connected);
        var timeProvider = new ManualTimerTimeProvider();
        using var viewModel = CreateViewModel(gateway, timeProvider);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => gateway.SaveProfileAsync(
            "profile",
            "Profile",
            description: null,
            instructions: null,
            chatProviderId: null,
            chatModelId: null,
            embeddingProviderId: null,
            embeddingModelId: null));
        timeProvider.FireAll();
        await WaitUntilAsync(() => viewModel.HasRuntimeNotice);

        Assert.Equal("Injected Runtime transport failure.", exception.Message);
        Assert.Equal(AgentRuntimeConnectionState.Unavailable, gateway.ConnectionState);
        Assert.Equal("Agent Runtime is unavailable. Reconnecting...", viewModel.RuntimeNoticeText);
    }

    [Theory]
    [InlineData(false, "Profile saved.", AgentProfileStatusKind.Success)]
    [InlineData(true, "Injected save failure.", AgentProfileStatusKind.Error)]
    public async Task ConnectedClearsOnlyRuntimeNotice(
        bool saveFails,
        string expectedStatus,
        AgentProfileStatusKind expectedKind)
    {
        var timeProvider = new ManualTimerTimeProvider();
        var gateway = new RuntimeAwareProfileGateway { ThrowOnSave = saveFails };
        using var viewModel = CreateViewModel(gateway, timeProvider);
        await viewModel.InitializeAsync();
        viewModel.DisplayName = "Updated Profile";
        await viewModel.SaveProfileCommand.ExecuteAsync(null);
        Assert.Equal(expectedStatus, viewModel.StatusText);

        gateway.SetConnectionState(AgentRuntimeConnectionState.Reconnecting);
        timeProvider.FireAll();
        await WaitUntilAsync(() => viewModel.HasRuntimeNotice);
        gateway.SetConnectionState(AgentRuntimeConnectionState.Connected);
        await WaitUntilAsync(() => !viewModel.HasRuntimeNotice);

        Assert.Equal(expectedStatus, viewModel.StatusText);
        Assert.Equal(expectedKind, viewModel.StatusKind);
    }

    [Fact]
    public async Task SaveSucceedingDuringHealthyHandoffNeverEndsWithRuntimeWarning()
    {
        var timeProvider = new ManualTimerTimeProvider();
        var gateway = new RuntimeAwareProfileGateway();
        using var viewModel = CreateViewModel(gateway, timeProvider);
        await viewModel.InitializeAsync();
        gateway.OnSave = () =>
        {
            gateway.SetConnectionState(AgentRuntimeConnectionState.Reconnecting);
            gateway.SetConnectionState(AgentRuntimeConnectionState.Connected);
        };
        viewModel.DisplayName = "Updated Profile";

        await viewModel.SaveProfileCommand.ExecuteAsync(null);
        timeProvider.FireAll();

        Assert.Equal("Profile saved.", viewModel.StatusText);
        Assert.Equal(AgentProfileStatusKind.Success, viewModel.StatusKind);
        Assert.False(viewModel.HasRuntimeNotice);
    }

    [Fact]
    public async Task BackgroundAvailabilityCallbacksUpdateRuntimeNoticeOnUiDispatcher()
    {
        var timeProvider = new ManualTimerTimeProvider();
        var gateway = new RuntimeAwareProfileGateway();
        using var dispatcher = new DedicatedTestDispatcher();
        using var viewModel = CreateViewModel(gateway, timeProvider, dispatcher);
        var changedThread = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AgentProfilesViewModel.RuntimeNoticeText)
                && viewModel.HasRuntimeNotice)
            {
                changedThread.TrySetResult(Environment.CurrentManagedThreadId);
            }
        };

        await Task.Run(() => gateway.SetConnectionState(AgentRuntimeConnectionState.Reconnecting));
        await dispatcher.WaitForIdleAsync();
        timeProvider.FireAll();

        Assert.Equal(
            dispatcher.ThreadId,
            await changedThread.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task QueuedStaleConnectedCallbackCannotClearNewerOutage()
    {
        var timeProvider = new ManualTimerTimeProvider();
        var gateway = new RuntimeAwareProfileGateway();
        using var dispatcher = new DedicatedTestDispatcher();
        using var viewModel = CreateViewModel(gateway, timeProvider, dispatcher);

        gateway.SetConnectionState(AgentRuntimeConnectionState.Unavailable);
        await dispatcher.WaitForIdleAsync();
        timeProvider.FireAll();
        await WaitUntilAsync(() => viewModel.HasRuntimeNotice);
        dispatcher.Pause();

        gateway.SetConnectionState(AgentRuntimeConnectionState.Connected);
        await dispatcher.WaitForEnqueuedAsync();
        gateway.SetConnectionState(AgentRuntimeConnectionState.Reconnecting);
        await dispatcher.WaitForEnqueuedAsync();
        dispatcher.Resume();
        await dispatcher.WaitForIdleAsync();

        Assert.True(viewModel.HasRuntimeNotice);
        Assert.Equal("Agent Runtime is unavailable. Reconnecting...", viewModel.RuntimeNoticeText);
    }

    [Fact]
    public async Task DisposeSuppressesDelayedRuntimeNotice()
    {
        var timeProvider = new ManualTimerTimeProvider();
        var gateway = new RuntimeAwareProfileGateway();
        var viewModel = CreateViewModel(gateway, timeProvider);

        gateway.SetConnectionState(AgentRuntimeConnectionState.Reconnecting);
        viewModel.Dispose();
        timeProvider.FireAll();
        await Task.Yield();

        Assert.False(viewModel.HasRuntimeNotice);
        Assert.Empty(viewModel.RuntimeNoticeText);
    }

    [Fact]
    public async Task ReusedProfileViewModelReconcilesAndClearsStaleRuntimeNotice()
    {
        var timeProvider = new ManualTimerTimeProvider();
        var gateway = new RuntimeAwareProfileGateway();
        using var viewModel = CreateViewModel(gateway, timeProvider);
        await viewModel.InitializeAsync();
        var selectedProfileId = viewModel.SelectedProfile?.ProfileId;
        gateway.SetConnectionState(AgentRuntimeConnectionState.Unavailable);
        timeProvider.FireAll();
        await WaitUntilAsync(() => viewModel.HasRuntimeNotice);

        gateway.SetConnectionState(AgentRuntimeConnectionState.Connected, notify: false);
        await viewModel.InitializeAsync();

        Assert.False(viewModel.HasRuntimeNotice);
        Assert.Empty(viewModel.RuntimeNoticeText);
        Assert.Equal(selectedProfileId, viewModel.SelectedProfile?.ProfileId);
    }

    [Fact]
    public async Task BackDuringCreate_PreventsLateCompletionFromReopeningProfile()
    {
        var timeProvider = new ManualTimerTimeProvider();
        var gateway = new RuntimeAwareProfileGateway { BlockCreate = true };
        using var viewModel = CreateViewModel(gateway, timeProvider);
        await viewModel.InitializeAsync();

        var create = viewModel.CreateProfileCommand.ExecuteAsync(null);
        await gateway.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.BackToProfileListCommand.Execute(null);
        gateway.ReleaseCreate.TrySetResult();
        await create;

        Assert.Null(viewModel.SelectedProfile);
        Assert.True(viewModel.IsListActive);
        Assert.Equal("Profile", Assert.Single(viewModel.Profiles).DisplayName);
    }

    [Fact]
    public async Task BackDuringCreatedProfileHydration_RetiresNonCooperativeLoad()
    {
        var timeProvider = new ManualTimerTimeProvider();
        var gateway = new RuntimeAwareProfileGateway();
        using var viewModel = CreateViewModel(gateway, timeProvider);
        await viewModel.InitializeAsync();
        gateway.BlockNextLocalTools = true;

        var create = viewModel.CreateProfileCommand.ExecuteAsync(null);
        await gateway.LocalToolsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.BackToProfileListCommand.Execute(null);

        await create.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(viewModel.SelectedProfile);
        Assert.False(viewModel.IsHydrating);
        Assert.False(viewModel.IsBusy);
        gateway.ReleaseLocalTools.TrySetResult();
    }

    [Fact]
    public async Task Initialization_ReplaysProfileChangeRaisedWhileInitialLoadIsPending()
    {
        var timeProvider = new ManualTimerTimeProvider();
        var gateway = new RuntimeAwareProfileGateway { BlockNextLocalTools = true };
        using var viewModel = CreateViewModel(gateway, timeProvider);

        var initialization = viewModel.InitializeAsync();
        await gateway.LocalToolsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        gateway.NotifyProfileChanged();
        gateway.ReleaseLocalTools.TrySetResult();
        await initialization.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(gateway.ListProfilesCount >= 2);
        Assert.Equal("profile-1", viewModel.SelectedProfile?.ProfileId);
    }

    private static AgentProfilesViewModel CreateViewModel(
        IAgentProfileGateway gateway,
        TimeProvider timeProvider,
        AgentPresentationDispatcher? dispatcher = null)
        => new(
            gateway,
            settingsNavigationService: null,
            dispatcher ?? InlinePresentationDispatcher.Instance,
            timeProvider);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class RuntimeAwareProfileGateway : IAgentProfileGateway, IAgentRuntimeAvailability
    {
        private AgentProfileRecord _profile = CreateProfile();
        private int _connectionState = (int)AgentRuntimeConnectionState.Connected;
        private int _listProfilesCount;

        public AgentRuntimeConnectionState ConnectionState
            => (AgentRuntimeConnectionState)Volatile.Read(ref _connectionState);

        public bool IsRuntimeAvailable => ConnectionState == AgentRuntimeConnectionState.Connected;

        public int ListProfilesCount => Volatile.Read(ref _listProfilesCount);

        public bool ThrowOnSave { get; init; }

        public bool BlockCreate { get; init; }

        public bool BlockNextLocalTools { get; set; }

        public TaskCompletionSource CreateStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseCreate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource LocalToolsStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseLocalTools { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action? OnSave { get; set; }

        public event Action<AgentRuntimeConnectionState>? ConnectionStateChanged;
        public event Action<string>? ProfileChanged;
        public event Action? SelectableCapabilitiesChanged
        {
            add { }
            remove { }
        }

        public void SetConnectionState(AgentRuntimeConnectionState state, bool notify = true)
        {
            Volatile.Write(ref _connectionState, (int)state);
            if (notify)
            {
                ConnectionStateChanged?.Invoke(state);
            }
        }

        public void NotifyProfileChanged() => ProfileChanged?.Invoke(_profile.ProfileId);

        public IReadOnlyList<AgentProfileRecord> ListProfiles()
        {
            Interlocked.Increment(ref _listProfilesCount);
            return [_profile];
        }

        public AgentProfileRecord? GetProfile(string profileId)
            => string.Equals(profileId, _profile.ProfileId, StringComparison.OrdinalIgnoreCase)
                ? _profile
                : null;

        public AgentProfileModelBindingRecord? GetChatBinding(string profileId) => null;

        public async Task<AgentProfileRecord> CreateProfileAsync(
            string displayName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BlockCreate)
            {
                CreateStarted.TrySetResult();
                await ReleaseCreate.Task;
            }
            _profile = CreateProfile() with { DisplayName = displayName };
            return _profile;
        }

        public void SaveProfile(
            string profileId,
            string displayName,
            string? description,
            string? instructions,
            string? chatProviderId,
            string? chatModelId,
            string? embeddingProviderId,
            string? embeddingModelId,
            IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>? selectableCapabilityAssignments = null,
            string? behaviorLoopId = null,
            string? behaviorLoopSourceId = null,
            string? behaviorLoopSettingsJson = null,
            string? chatModelSettingsJson = null)
        {
            if (ThrowOnSave)
            {
                throw new InvalidOperationException("Injected save failure.");
            }

            _profile = _profile with
            {
                DisplayName = displayName,
                Description = description,
                Instructions = instructions,
                ChatProviderId = chatProviderId,
                ChatModelId = chatModelId,
                EmbeddingProviderId = embeddingProviderId,
                EmbeddingModelId = embeddingModelId,
                SelectableCapabilityAssignments = selectableCapabilityAssignments,
                BehaviorLoopId = behaviorLoopId,
                BehaviorLoopSourceId = behaviorLoopSourceId,
                BehaviorLoopSettingsJson = behaviorLoopSettingsJson,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            OnSave?.Invoke();
        }

        public void DeleteProfile(string profileId) => throw new NotSupportedException();

        public IReadOnlyList<AgentBehaviorLoopDescriptor> ListBehaviorLoopDescriptors() => [];

        public IReadOnlyList<AgentProviderDescriptor> ListChatProviderDescriptors() => [];

        public IReadOnlyList<AgentEmbeddingProviderDescriptor> ListEmbeddingProviderDescriptors() => [];

        public bool HasProfileCapabilityConsumers(string capabilityKind) => false;

        public Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>
            ListSelectableProfileCapabilitiesAsync(
                AgentProfileRecord? profile = null,
                CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>([]);

        public async Task<IReadOnlyList<AgentToolCatalogEntry>> ListInstalledLocalToolsAsync(
            CancellationToken cancellationToken = default)
        {
            if (BlockNextLocalTools)
            {
                BlockNextLocalTools = false;
                LocalToolsStarted.TrySetResult();
                await ReleaseLocalTools.Task;
            }
            return [];
        }

        public Task<IReadOnlyList<AgentModelDescriptor>> ListChatModelsAsync(
            string? providerId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelDescriptor>>([]);

        public Task<IReadOnlyList<AgentEmbeddingModelDescriptor>> ListEmbeddingModelsAsync(
            string? providerId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentEmbeddingModelDescriptor>>([]);

        public Task<AgentProviderReadiness?> GetChatProviderReadinessAsync(
            string? providerId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AgentProviderReadiness?>(null);

        public Task<AgentEmbeddingProviderReadiness?> GetEmbeddingProviderReadinessAsync(
            string? providerId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AgentEmbeddingProviderReadiness?>(null);

        private static AgentProfileRecord CreateProfile()
        {
            var now = DateTimeOffset.UtcNow;
            return new AgentProfileRecord(
                "profile-1",
                "Profile",
                null,
                null,
                null,
                null,
                null,
                null,
                now,
                now);
        }
    }

    private sealed class ProductionProfileRuntime : IDisposable
    {
        private readonly RegressionTestPackageScope _scope;
        private readonly AgentProfileService _profiles;
        private readonly AgentRuntimeChangeHub _changes;

        private ProductionProfileRuntime(
            RegressionTestPackageScope scope,
            AgentLocalStore store,
            AgentProfileService profiles,
            AgentRuntimeChangeHub changes,
            ProfileHandlerRuntimeClient client)
        {
            _scope = scope;
            Store = store;
            _profiles = profiles;
            _changes = changes;
            Client = client;
        }

        public ProfileHandlerRuntimeClient Client { get; }
        public AgentLocalStore Store { get; }
        public AgentProfileService Profiles => _profiles;

        public static ProductionProfileRuntime Create(Exception? profileFailure = null)
        {
            var scope = RegressionTestPackageScope.Create();
            var extensions = new RegressionTestExtensionCatalog();
            var store = new AgentLocalStore(scope.Context);
            var sessions = new AgentSessionService(store, extensions);
            var workspaces = new AgentWorkspaceService(store, extensions, sessions);
            var targets = new AgentExecutionTargetService(extensions);
            var tools = new AgentToolService(
                sessions,
                workspaces,
                targets,
                extensions);
            var profiles = new AgentProfileService(store, tools, extensions, extensions.BehaviorLoops);
            var changes = new AgentRuntimeChangeHub(profiles, workspaces, sessions);
            var client = new ProfileHandlerRuntimeClient(
                new AgentDashboardHandler(profiles, workspaces, changes),
                new AgentCatalogHandler(profiles, targets, changes),
                new AgentProfileCommandHandler(profiles, changes),
                changes,
                profileFailure);
            return new ProductionProfileRuntime(scope, store, profiles, changes, client);
        }

        public void Dispose()
        {
            _changes.Dispose();
            _profiles.Dispose();
            _scope.Dispose();
        }
    }

    private sealed class ProfileHandlerRuntimeClient(
        AgentDashboardHandler dashboard,
        AgentCatalogHandler catalog,
        AgentProfileCommandHandler profiles,
        AgentRuntimeChangeHub changes,
        Exception? profileFailure) : IPackageRuntimeClient
    {
        public bool IsAvailable => true;

        public async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            if (ReferenceEquals(operation, AgentRuntimeOperations.Dashboard))
            {
                var response = await dashboard.HandleAsync(
                    (AgentDashboardRequest)(object)request,
                    cancellationToken);
                return (TResponse)(object)response;
            }
            if (ReferenceEquals(operation, AgentRuntimeOperations.Profiles))
            {
                if (profileFailure is not null)
                {
                    throw profileFailure;
                }
                var response = await profiles.HandleAsync(
                    (AgentProfileCommand)(object)request,
                    cancellationToken);
                return (TResponse)(object)response;
            }
            if (ReferenceEquals(operation, AgentRuntimeOperations.Catalog))
            {
                var response = await catalog.HandleAsync(
                    (AgentCatalogRequest)(object)request,
                    cancellationToken);
                return (TResponse)(object)response;
            }

            throw new NotSupportedException(operation.OperationId);
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            if (!ReferenceEquals(stream, AgentRuntimeOperations.Changes))
            {
                throw new NotSupportedException(stream.StreamId);
            }

            await foreach (var change in changes.SubscribeAsync(
                               (AgentChangeSubscription)(object)request,
                               cancellationToken))
            {
                yield return (TEvent)(object)change;
            }
        }
    }

    private sealed class InlinePresentationDispatcher : AgentPresentationDispatcher
    {
        public static InlinePresentationDispatcher Instance { get; } = new();

        public bool CheckAccess() => true;

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class DedicatedTestDispatcher : AgentPresentationDispatcher, IDisposable
    {
        private readonly BlockingCollection<WorkItem> _queue = new();
        private readonly ManualResetEventSlim _enabled = new(initialState: true);
        private readonly SemaphoreSlim _enqueued = new(0);
        private readonly Thread _thread;
        private readonly TaskCompletionSource<int> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DedicatedTestDispatcher()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Agent profiles test dispatcher",
            };
            _thread.Start();
            ThreadId = _started.Task.GetAwaiter().GetResult();
        }

        public int ThreadId { get; }

        public bool CheckAccess() => Environment.CurrentManagedThreadId == ThreadId;

        public Task InvokeAsync(Action action)
        {
            if (CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(new WorkItem(action, completion));
            _enqueued.Release();
            return completion.Task;
        }

        public void Pause() => _enabled.Reset();

        public void Resume() => _enabled.Set();

        public Task WaitForEnqueuedAsync() => _enqueued.WaitAsync();

        public Task WaitForIdleAsync() => InvokeAsync(() => { });

        public void Dispose()
        {
            Resume();
            _queue.CompleteAdding();
            _thread.Join();
            _queue.Dispose();
            _enabled.Dispose();
            _enqueued.Dispose();
        }

        private void Run()
        {
            _started.SetResult(Environment.CurrentManagedThreadId);
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                _enabled.Wait();
                try
                {
                    item.Action();
                    item.Completion.SetResult();
                }
                catch (Exception ex)
                {
                    item.Completion.SetException(ex);
                }
            }
        }

        private sealed record WorkItem(Action Action, TaskCompletionSource Completion);
    }

    private sealed class ManualTimerTimeProvider : TimeProvider
    {
        private readonly object _syncRoot = new();
        private readonly HashSet<ManualTimer> _timers = [];

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_syncRoot)
            {
                _timers.Add(timer);
            }
            return timer;
        }

        public void FireAll()
        {
            ManualTimer[] timers;
            lock (_syncRoot)
            {
                timers = [.. _timers];
            }

            foreach (var timer in timers)
            {
                timer.Fire();
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_syncRoot)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(
            ManualTimerTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            private readonly object _syncRoot = new();
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_syncRoot)
                {
                    return !_disposed;
                }
            }

            public void Dispose()
            {
                lock (_syncRoot)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    _disposed = true;
                }
                owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Fire()
            {
                lock (_syncRoot)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    _disposed = true;
                }
                owner.Remove(this);
                callback(state);
            }
        }
    }
}
