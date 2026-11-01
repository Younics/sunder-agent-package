extern alias AgentCore;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AgentCore::Sunder.Package.Agent.Contracts.Models;
using AgentCore::Sunder.Package.Agent.PackageViews;
using AgentCore::Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Rpc;
using Xunit;
using AgentChatSelectionStateService = AgentCore::Sunder.Package.Agent.Services.AgentChatSelectionStateService;
using static Sunder.Package.Agent.Tests.RuntimeSnapshotFixture;

namespace Sunder.Package.Agent.Tests;

[Collection("Runtime correctness")]
public sealed class ChatSessionDeletionTests
{
    [Fact]
    public async Task ChatDeleteCommand_ExpectedGatewayFailureDoesNotEscapeICommandExecute()
    {
        var snapshot = CreateChatSnapshot(revision: 90);
        var client = new SessionMutationOwnerClient(
            snapshot,
            SessionMutationMode.ExpectedPrecommitFailure);
        using var gateway = new AgentAppRuntimeGateway(client);
        using var viewModel = new AgentChatViewModel(
            gateway,
            gateway,
            gateway,
            gateway,
            gateway);
        await viewModel.InitializeAsync();
        var deleted = Assert.IsType<AgentSessionListItemViewModel>(viewModel.SelectedSession);
        var context = new CapturingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            viewModel.DeleteSessionCommand.Execute(deleted);
            await client.ReconciliationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(25);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.Empty(context.Exceptions);
        Assert.Same(deleted, viewModel.SelectedSession);
        Assert.Single(viewModel.Sessions);
    }

    [Theory]
    [InlineData(SessionMutationMode.UnknownFailure)]
    [InlineData(SessionMutationMode.WrappedFatalFailure)]
    public async Task ChatDeleteCommand_UnknownAndWrappedFatalGatewayFailuresRemainObservable(
        SessionMutationMode mode)
    {
        var snapshot = CreateChatSnapshot(revision: 91);
        var client = new SessionMutationOwnerClient(snapshot, mode);
        using var gateway = new AgentAppRuntimeGateway(client);
        using var viewModel = new AgentChatViewModel(
            gateway,
            gateway,
            gateway,
            gateway,
            gateway);
        await viewModel.InitializeAsync();

        var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            viewModel.DeleteSessionCommand.ExecuteAsync(viewModel.SelectedSession));

        if (mode == SessionMutationMode.UnknownFailure)
        {
            Assert.IsType<InvalidOperationException>(failure);
        }
        else
        {
            Assert.IsType<Exception>(failure);
            Assert.IsType<OutOfMemoryException>(failure.InnerException);
        }
        Assert.Single(viewModel.Sessions);
    }

    [Fact]
    public async Task ChatDeleteCommand_FinalSessionSuccessAndAmbiguousSnapshotConvergeToExplicitEmptyState()
    {
        using var scope = RegressionTestPackageScope.Create();
        var snapshot = CreateChatSnapshotWithChildLink(revision: 92);
        var sessionId = snapshot.SelectedSession!.Session.SessionId;
        snapshot = snapshot with
        {
            Permissions = snapshot.Permissions with
            {
                SessionState = new AgentSessionPermissionState(sessionId, true),
            },
        };
        var client = new SessionMutationOwnerClient(
            snapshot,
            SessionMutationMode.SuccessWithAmbiguousSnapshot);
        using var gateway = new AgentAppRuntimeGateway(client);
        var selections = new AgentChatSelectionStateService(scope.Context);
        using var viewModel = new AgentChatViewModel(
            gateway,
            gateway,
            gateway,
            gateway,
            gateway,
            selections);
        viewModel.IsDetailedTranscript = true;
        await viewModel.InitializeAsync();
        viewModel.DraftMessage = "unsent draft";
        Assert.NotEmpty(viewModel.Messages);
        Assert.True(viewModel.IsUnrestrictedModeEnabled);

        await viewModel.DeleteSessionCommand.ExecuteAsync(viewModel.SelectedSession);
        await client.ReconciliationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Empty(viewModel.Sessions);
        Assert.Null(viewModel.SelectedSession);
        Assert.Null(viewModel.DisplayedSession);
        Assert.Empty(viewModel.Messages);
        Assert.Empty(viewModel.DraftMessage);
        Assert.Empty(viewModel.PendingAttachments);
        Assert.Empty(viewModel.PendingPermissionRequests);
        Assert.False(viewModel.IsUnrestrictedModeEnabled);
        Assert.Null(await selections.GetSelectedSessionIdAsync(
            snapshot.SelectedWorkspace!.WorkspaceId));
        Assert.Null(await scope.Context.Storage.State.GetValueAsync("agent.chat.selectedSessionId"));
    }

    [Fact]
    public async Task ChatDeleteCommand_LateFinalSessionCompletionDoesNotClearNewWorkspaceAuthority()
    {
        using var scope = RegressionTestPackageScope.Create();
        var snapshots = CreateWorkspaceSelectionSnapshots();
        var secondWorkspace = Assert.IsType<AgentWorkspaceRecord>(snapshots.Second.SelectedWorkspace);
        var secondSession = Assert.IsType<AgentSessionSnapshot>(snapshots.Second.SelectedSession);
        var client = new BlockingWorkspaceSwitchDeleteOwnerClient(snapshots.First, snapshots.Second);
        using var gateway = new AgentAppRuntimeGateway(client);
        var selections = new AgentChatSelectionStateService(scope.Context);
        using var viewModel = new AgentChatViewModel(
            gateway,
            gateway,
            gateway,
            gateway,
            gateway,
            selections);
        await viewModel.InitializeAsync();
        var deletedSession = Assert.IsType<AgentSessionListItemViewModel>(viewModel.SelectedSession);

        var deletion = viewModel.DeleteSessionCommand.ExecuteAsync(deletedSession);
        await client.DeleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.SelectedWorkspace = viewModel.Workspaces.Single(workspace =>
            workspace.WorkspaceId == secondWorkspace.WorkspaceId);
        await client.SecondSnapshotServed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => viewModel.SelectedSession?.SessionId
                                   == secondSession.Session.SessionId);
        var pendingAttachment = CreatePendingAttachment("new-workspace.txt");
        viewModel.PendingAttachments.Add(pendingAttachment);

        client.ReleaseDelete.TrySetResult();
        await deletion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            secondWorkspace.WorkspaceId,
            viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.Equal(
            secondSession.Session.SessionId,
            viewModel.SelectedSession?.SessionId);
        Assert.Same(pendingAttachment, Assert.Single(viewModel.PendingAttachments));
        Assert.Equal(
            secondSession.Session.SessionId,
            await selections.GetSelectedSessionIdAsync(secondWorkspace.WorkspaceId));
    }

    public enum SessionMutationMode
    {
        ExpectedPrecommitFailure,
        UnknownFailure,
        WrappedFatalFailure,
        SuccessWithAmbiguousSnapshot,
    }

    private sealed class SessionMutationOwnerClient(
        AgentChatSnapshotProjection initialSnapshot,
        SessionMutationMode mode) : OwnerServiceStub
    {
        private int _snapshotCount;

        public TaskCompletionSource ReconciliationCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request is AgentChatSnapshotRequest)
            {
                var count = Interlocked.Increment(ref _snapshotCount);
                if (mode == SessionMutationMode.SuccessWithAmbiguousSnapshot && count == 2)
                {
                    throw new SunderRpcException(new SunderRpcError(
                        SunderRpcErrorKind.Unavailable,
                        "test.snapshot-ambiguous",
                        "Injected ambiguous snapshot response."));
                }

                var snapshot = mode == SessionMutationMode.SuccessWithAmbiguousSnapshot && count >= 3
                    ? Empty(initialSnapshot)
                    : initialSnapshot;
                if (count >= 2 && mode != SessionMutationMode.SuccessWithAmbiguousSnapshot
                    || count >= 3)
                {
                    ReconciliationCompleted.TrySetResult();
                }
                return ValueTask.FromResult((TResponse)(object)snapshot);
            }

            if (request is AgentSessionCommand)
            {
                switch (mode)
                {
                    case SessionMutationMode.ExpectedPrecommitFailure:
                        throw new SunderRpcException(new SunderRpcError(
                            SunderRpcErrorKind.Domain,
                            "test.delete-rejected",
                            "Injected delete rejection."));
                    case SessionMutationMode.UnknownFailure:
                        throw new InvalidOperationException("Injected invariant failure.");
                    case SessionMutationMode.WrappedFatalFailure:
                        throw new Exception(
                            "Injected wrapped fatal failure.",
                            new OutOfMemoryException("Injected fatal failure."));
                    case SessionMutationMode.SuccessWithAmbiguousSnapshot:
                        return ValueTask.FromResult((TResponse)(object)new AgentSessionCommandResult(
                            initialSnapshot.Revision + 1,
                            Session: null));
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            throw new NotSupportedException(typeof(TRequest).Name);
        }

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            yield return (TEvent)(object)new AgentRuntimeChange(
                initialSnapshot.Revision,
                AgentRuntimeChangeKind.Connected,
                RuntimeInstanceId: initialSnapshot.RuntimeInstanceId);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        private static AgentChatSnapshotProjection Empty(AgentChatSnapshotProjection snapshot)
            => snapshot with
            {
                Revision = snapshot.Revision + 2,
                SelectedSession = null,
                WorkspaceSessions = [],
                InitialTranscript = new AgentTranscriptPage(snapshot.Revision + 2, [], false),
                Permissions = new AgentChatPermissionProjection(
                    snapshot.Revision + 2,
                    SessionState: null,
                    PendingRequests: []),
            };
    }

    private sealed class BlockingWorkspaceSwitchDeleteOwnerClient(
        AgentChatSnapshotProjection first,
        AgentChatSnapshotProjection second) : OwnerServiceStub
    {
        public TaskCompletionSource DeleteStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseDelete { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondSnapshotServed { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            if (request is AgentChatSnapshotRequest)
            {
                var snapshotRequest = Assert.IsType<AgentChatSnapshotRequest>(request);
                if (string.Equals(
                        snapshotRequest.PreferredWorkspaceId,
                        second.SelectedWorkspace!.WorkspaceId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    SecondSnapshotServed.TrySetResult();
                    return (TResponse)(object)second;
                }
                return (TResponse)(object)first;
            }

            if (request is AgentSessionCommand)
            {
                DeleteStarted.TrySetResult();
                await ReleaseDelete.Task.WaitAsync(cancellationToken);
                return (TResponse)(object)new AgentSessionCommandResult(
                    first.Revision + 1,
                    Session: null);
            }

            throw new NotSupportedException(typeof(TRequest).Name);
        }

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            yield return (TEvent)(object)new AgentRuntimeChange(
                first.Revision,
                AgentRuntimeChangeKind.Connected,
                RuntimeInstanceId: first.RuntimeInstanceId);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class CapturingSynchronizationContext : SynchronizationContext
    {
        public ConcurrentQueue<Exception> Exceptions { get; } = [];

        public override void Post(SendOrPostCallback callback, object? state)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    callback(state);
                }
                catch (Exception exception)
                {
                    Exceptions.Enqueue(exception);
                }
            });
        }
    }
}
