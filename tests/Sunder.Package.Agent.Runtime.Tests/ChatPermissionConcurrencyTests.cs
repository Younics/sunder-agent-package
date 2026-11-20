extern alias AgentCore;

using System.Collections.Concurrent;
using System.Reflection;
using AgentCore::Sunder.Package.Agent.Contracts.Models;
using AgentCore::Sunder.Package.Agent.Models;
using AgentCore::Sunder.Package.Agent.PackageViews;
using AgentCore::Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Xunit;
using AgentPermissionCommandHandler = Sunder.Package.Agent.Runtime.AgentPermissionCommandHandler;
using WorkerRuntime = Sunder.Package.Agent.Runtime;
using static Sunder.Package.Agent.Tests.RuntimeSnapshotFixture;

namespace Sunder.Package.Agent.Tests;

[Collection("Runtime correctness")]
public sealed class ChatPermissionConcurrencyTests
{
    [Fact]
    public async Task ChatViewModel_RuntimeInstanceResetAcceptsLowerPermissionRevision()
    {
        var initial = CreateChatSnapshot(100);
        var sessionId = initial.SelectedSession!.Session.SessionId;
        initial = initial with
        {
            Permissions = initial.Permissions with
            {
                SessionState = new AgentSessionPermissionState(
                    sessionId,
                    true),
            },
        };
        using var gateway = new AgentAppRuntimeGateway(
            new StaticChatSnapshotRuntimeClient(initial));
        using var viewModel = new AgentChatViewModel(
            gateway,
            gateway,
            gateway,
            gateway,
            gateway);
        await viewModel.InitializeAsync();
        Assert.True(viewModel.IsUnrestrictedModeEnabled);
        var restarted = initial with
        {
            Revision = 1,
            RuntimeInstanceId = "runtime-2",
            InitialTranscript = initial.InitialTranscript with { Revision = 1 },
            Permissions = new AgentChatPermissionProjection(
                1,
                new AgentSessionPermissionState(
                    sessionId,
                    false),
                []),
        };
        var applySnapshot = typeof(AgentChatViewModel).GetMethod(
            "ApplyChatSnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        applySnapshot.Invoke(viewModel, [restarted, true]);

        Assert.False(viewModel.IsUnrestrictedModeEnabled);
    }

    [Fact]
    public async Task ChatViewModel_StalePermissionReadCannotOverwriteNewerCommandResult()
    {
        using var scope = RegressionTestPackageScope.Create();
        var runtime = CreateSnapshotRuntime(scope.Context);
        using var profiles = runtime.Profiles;
        var profile = await profiles.CreateProfileAsync("Permission profile");
        var workspace = runtime.Workspaces.CreateWorkspace("Permission workspace");
        var session = runtime.Sessions.CreateSession(
            "Permission session",
            profileId: profile.ProfileId,
            workspaceId: workspace.WorkspaceId);
        var permissions = new RacingPermissionGateway(session.SessionId);
        using var viewModel = new AgentChatViewModel(
            AgentRoleBoundary.As<IAgentProfileGateway>(profiles),
            AgentRoleBoundary.As<IAgentWorkspaceGateway>(runtime.Workspaces),
            AgentRoleBoundary.As<IAgentSessionGateway>(runtime.Sessions),
            permissions,
            NoOpRunGateway.Instance);
        await viewModel.InitializeAsync();

        runtime.Sessions.UpdateSession(session with
        {
            Title = "Permission session updated",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        await permissions.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        viewModel.IsUnrestrictedModeEnabled = true;
        await permissions.WriteCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => viewModel.IsUnrestrictedModeEnabled);

        permissions.ReleaseRead();
        await permissions.ReadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(25);

        Assert.True(viewModel.IsUnrestrictedModeEnabled);
    }

    [Fact]
    public async Task ChatViewModel_NewerPermissionReadCanFollowOlderCommandResult()
    {
        using var scope = RegressionTestPackageScope.Create();
        var runtime = CreateSnapshotRuntime(scope.Context);
        using var profiles = runtime.Profiles;
        var profile = await profiles.CreateProfileAsync("Permission profile");
        var workspace = runtime.Workspaces.CreateWorkspace("Permission workspace");
        var session = runtime.Sessions.CreateSession(
            "Permission session",
            profileId: profile.ProfileId,
            workspaceId: workspace.WorkspaceId);
        var permissions = new RacingPermissionGateway(
            session.SessionId,
            readRevision: 11,
            writeRevision: 10,
            readEnabled: false);
        using var viewModel = new AgentChatViewModel(
            AgentRoleBoundary.As<IAgentProfileGateway>(profiles),
            AgentRoleBoundary.As<IAgentWorkspaceGateway>(runtime.Workspaces),
            AgentRoleBoundary.As<IAgentSessionGateway>(runtime.Sessions),
            permissions,
            NoOpRunGateway.Instance);
        await viewModel.InitializeAsync();

        runtime.Sessions.UpdateSession(session with
        {
            Title = "Permission session updated",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        await permissions.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        viewModel.IsUnrestrictedModeEnabled = true;
        await permissions.WriteCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => viewModel.IsUnrestrictedModeEnabled);

        permissions.ReleaseRead();
        await permissions.ReadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => !viewModel.IsUnrestrictedModeEnabled);
    }

    [Fact]
    public async Task ChatViewModel_RapidPermissionTogglesCommitLatestIntentLast()
    {
        using var scope = RegressionTestPackageScope.Create();
        var runtime = CreateSnapshotRuntime(scope.Context);
        using var profiles = runtime.Profiles;
        var profile = await profiles.CreateProfileAsync("Serialized permission profile");
        var workspace = runtime.Workspaces.CreateWorkspace("Serialized permission workspace");
        var session = runtime.Sessions.CreateSession(
            "Serialized permission session",
            profileId: profile.ProfileId,
            workspaceId: workspace.WorkspaceId);
        var permissions = new DelayedPermissionMutationGateway(session.SessionId);
        using var viewModel = new AgentChatViewModel(
            AgentRoleBoundary.As<IAgentProfileGateway>(profiles),
            AgentRoleBoundary.As<IAgentWorkspaceGateway>(runtime.Workspaces),
            AgentRoleBoundary.As<IAgentSessionGateway>(runtime.Sessions),
            permissions,
            NoOpRunGateway.Instance);
        await viewModel.InitializeAsync();

        viewModel.IsUnrestrictedModeEnabled = true;
        await permissions.FirstMutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        viewModel.IsUnrestrictedModeEnabled = false;
        await Task.Delay(25);

        Assert.False(permissions.SecondMutationStarted.Task.IsCompleted);
        permissions.ReleaseFirstMutation.TrySetResult();
        await permissions.SecondMutationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => !viewModel.IsUnrestrictedModeEnabled);

        Assert.False(permissions.AuthoritativeValue);
        Assert.Equal([true, false], permissions.CommittedValues.ToArray());
    }

    [Fact]
    public async Task PermissionHandler_UnknownCommandIsRejected()
    {
        using var scope = RegressionTestPackageScope.Create();
        var runtime = CreateSnapshotRuntime(scope.Context);
        using var profiles = runtime.Profiles;
        using var catalog = new RegressionTestExtensionCatalog();
        var permissions = new AgentPermissionService(runtime.Store, catalog.PermissionSurfaces);
        var handler = new AgentPermissionCommandHandler(permissions, runtime.Changes);

        var exception = await Assert.ThrowsAsync<WorkerRuntime.AgentOwnerOperationException>(() => handler.HandleAsync(
            new WorkerRuntime.AgentPermissionCommand((WorkerRuntime.AgentPermissionCommandKind)int.MaxValue)).AsTask());

        Assert.Equal("Unknown permission command.", exception.Message);
    }

    private sealed class RacingPermissionGateway(
        Guid sessionId,
        long readRevision = 1,
        long writeRevision = 2,
        bool readEnabled = false)
        : IAgentPermissionGateway,
          IAgentChatPermissionCommandGateway
    {
        private readonly TaskCompletionSource _releaseRead = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WriteCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public AgentSessionPermissionState GetSessionState(Guid requestedSessionId)
            => new(requestedSessionId, false);

        public void SetSessionUnrestrictedMode(Guid requestedSessionId, bool isEnabled) { }

        public Task<IReadOnlyList<AgentPermissionActionDescriptor>> ListActionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentPermissionActionDescriptor>>([]);

        public IReadOnlyList<AgentPermissionOverride> ListOverrides() => [];

        public void SaveOverride(
            string actionId,
            string boundaryId,
            AgentPermissionDecision decision)
        { }

        public void DeleteOverride(string actionId, string boundaryId) { }

        public IReadOnlyList<AgentPendingPermissionRequestRecord> ListPendingRequestsForSessionTree(
            Guid requestedSessionId) => [];

        public async Task<AgentChatPermissionProjection> LoadSessionPermissionsAsync(
            Guid requestedSessionId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(sessionId, requestedSessionId);
            ReadStarted.TrySetResult();
            await _releaseRead.Task.WaitAsync(cancellationToken);
            ReadCompleted.TrySetResult();
            return new AgentChatPermissionProjection(
                readRevision,
                new AgentSessionPermissionState(sessionId, readEnabled),
                []);
        }

        public Task<AgentChatPermissionProjection> SetSessionUnrestrictedModeAsync(
            Guid requestedSessionId,
            bool isEnabled,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(sessionId, requestedSessionId);
            Assert.True(isEnabled);
            WriteCompleted.TrySetResult();
            return Task.FromResult(new AgentChatPermissionProjection(
                writeRevision,
                new AgentSessionPermissionState(sessionId, true),
                []));
        }

        public void ReleaseRead() => _releaseRead.TrySetResult();
    }

    private sealed class DelayedPermissionMutationGateway(Guid sessionId) :
        IAgentPermissionGateway,
        IAgentChatPermissionCommandGateway
    {
        private readonly object _syncRoot = new();
        private readonly ConcurrentQueue<bool> _committedValues = new();
        private bool _authoritativeValue;
        private long _revision = 1;
        private int _mutationCount;

        public TaskCompletionSource FirstMutationStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstMutation { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondMutationStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondMutationCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool AuthoritativeValue
        {
            get
            {
                lock (_syncRoot) return _authoritativeValue;
            }
        }

        public IReadOnlyCollection<bool> CommittedValues => _committedValues.ToArray();

        public AgentSessionPermissionState GetSessionState(Guid requestedSessionId)
            => new(requestedSessionId, AuthoritativeValue);

        public void SetSessionUnrestrictedMode(Guid requestedSessionId, bool isEnabled)
        {
            lock (_syncRoot) _authoritativeValue = isEnabled;
        }

        public Task<IReadOnlyList<AgentPermissionActionDescriptor>> ListActionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentPermissionActionDescriptor>>([]);
        public IReadOnlyList<AgentPermissionOverride> ListOverrides() => [];
        public void SaveOverride(string actionId, string boundaryId, AgentPermissionDecision decision) { }
        public void DeleteOverride(string actionId, string boundaryId) { }
        public IReadOnlyList<AgentPendingPermissionRequestRecord> ListPendingRequestsForSessionTree(
            Guid requestedSessionId) => [];

        public Task<AgentChatPermissionProjection> LoadSessionPermissionsAsync(
            Guid requestedSessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_syncRoot)
            {
                return Task.FromResult(new AgentChatPermissionProjection(
                    _revision,
                    new AgentSessionPermissionState(requestedSessionId, _authoritativeValue),
                    []));
            }
        }

        public async Task<AgentChatPermissionProjection> SetSessionUnrestrictedModeAsync(
            Guid requestedSessionId,
            bool isEnabled,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(sessionId, requestedSessionId);
            var mutation = Interlocked.Increment(ref _mutationCount);
            if (mutation == 1)
            {
                FirstMutationStarted.TrySetResult();
                await ReleaseFirstMutation.Task.WaitAsync(cancellationToken);
            }
            else
            {
                SecondMutationStarted.TrySetResult();
            }

            AgentChatPermissionProjection projection;
            lock (_syncRoot)
            {
                _authoritativeValue = isEnabled;
                _committedValues.Enqueue(isEnabled);
                projection = new AgentChatPermissionProjection(
                    ++_revision,
                    new AgentSessionPermissionState(sessionId, _authoritativeValue),
                    []);
            }
            if (mutation == 2)
            {
                SecondMutationCompleted.TrySetResult();
            }
            return projection;
        }
    }

    private sealed class NoOpRunGateway : IAgentRunGateway
    {
        public static NoOpRunGateway Instance { get; } = new();

        public Task<AgentRunCheckpointRecord> QueueUserMessageAsync(
            Guid sessionId,
            string profileId,
            string userMessage,
            string workspaceId,
            IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CreateCheckpoint(sessionId));

        public Task<AgentRunCheckpointRecord> RollbackAndQueueUserMessageAsync(
            Guid sessionId,
            Guid rollbackAnchorTurnId,
            string profileId,
            string userMessage,
            string workspaceId,
            IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CreateCheckpoint(sessionId));

        public Task<AgentRunCheckpointRecord?> StopAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AgentRunCheckpointRecord?>(null);

        public Task<AgentRunCheckpointRecord?> ApprovePendingPermissionAsync(
            Guid sessionId,
            string requestId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AgentRunCheckpointRecord?>(null);

        public Task<AgentRunCheckpointRecord?> DenyPendingPermissionAsync(
            Guid sessionId,
            string requestId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AgentRunCheckpointRecord?>(null);

        private static AgentRunCheckpointRecord CreateCheckpoint(Guid sessionId)
            => new(
                Guid.NewGuid(),
                sessionId,
                1,
                AgentRunStatus.Running,
                "Running",
                DateTimeOffset.UtcNow);
    }
}
