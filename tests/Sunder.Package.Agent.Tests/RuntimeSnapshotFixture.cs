extern alias AgentCore;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AgentCore::Sunder.Package.Agent.Contracts.Models;
using AgentCore::Sunder.Package.Agent.PackageViews;
using AgentCore::Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Xunit;
using AgentRuntimeChangeHub = Sunder.Package.Agent.Runtime.AgentRuntimeChangeHub;

namespace Sunder.Package.Agent.Tests;

internal static class RuntimeSnapshotFixture
{
    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The Runtime gateway did not reconnect in time.");
            await Task.Delay(25);
        }
    }

    internal static SunderRpcException RuntimeUnavailable()
        => new(new SunderRpcError(
            SunderRpcErrorKind.Unavailable,
            "agent.runtime.unavailable",
            "Runtime unavailable."));

    internal static AgentChatSnapshotProjection CreateChatSnapshot(long revision)
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new AgentProfileRecord(
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
        var workspace = new AgentWorkspaceRecord(
            "workspace-1",
            "Workspace",
            null,
            now,
            now);
        var session = new AgentSessionRecord(
            Guid.NewGuid(),
            "Session",
            AgentSessionState.Active,
            now,
            now,
            RootSessionId: null,
            ProfileId: profile.ProfileId,
            WorkspaceId: workspace.WorkspaceId);
        session = session with { RootSessionId = session.SessionId };
        var checkpoint = new AgentRunCheckpointRecord(
            Guid.NewGuid(),
            session.SessionId,
            1,
            AgentRunStatus.Idle,
            null,
            now);
        var sessionSnapshot = new AgentSessionSnapshot(session, checkpoint);
        return new AgentChatSnapshotProjection(
            revision,
            [profile],
            [workspace],
            [],
            profile,
            workspace,
            sessionSnapshot,
            [sessionSnapshot],
            new AgentTranscriptPage(revision, [], false),
            new AgentChatPermissionProjection(
                revision,
                new AgentSessionPermissionState(session.SessionId, false),
                []));
    }

    internal static ProfileDashboardFixture CreateProfileDashboardFixture(
        long revision,
        string runtimeInstanceId = "runtime-1",
        string displayName = "Authoritative profile")
    {
        var assignment = new AgentProfileSelectableCapabilityAssignmentRecord(
            AgentProfileSelectableCapabilityKinds.Tool,
            "read",
            "files");
        var now = DateTimeOffset.UtcNow;
        var profile = new AgentProfileRecord(
            "profile-1",
            displayName,
            "Authoritative description",
            "Authoritative instructions",
            null,
            null,
            null,
            null,
            now,
            now,
            SelectableCapabilityAssignments: [assignment],
            BehaviorLoopId: "default",
            BehaviorLoopSourceId: "agent",
            BehaviorLoopSettingsJson: "{}");
        var compactProfile = profile with
        {
            Description = null,
            Instructions = null,
            ModelBindings = [],
            SelectableCapabilityAssignments = [],
            BehaviorLoopSourceId = null,
            BehaviorLoopSettingsJson = null,
        };
        var snapshot = CreateChatSnapshot(revision) with
        {
            Profiles = [compactProfile],
            SelectedProfile = compactProfile,
            RuntimeInstanceId = runtimeInstanceId,
        };
        var dashboard = new AgentDashboardProjection(
            revision,
            [profile],
            snapshot.Workspaces,
            snapshot.WorkspaceBindings);
        var catalog = new AgentCatalogProjection(
            revision,
            [],
            [],
            [],
            [],
            [],
            [new AgentProfileSelectableCapabilityDescriptor(
                assignment.Kind,
                assignment.CapabilityId,
                assignment.SourceId,
                "Read file",
                "Read a file from the workspace.")],
            HasEmbeddingConsumers: false,
            [],
            [],
            ChatReadiness: null,
            EmbeddingReadiness: null);
        return new ProfileDashboardFixture(
            assignment,
            profile,
            snapshot,
            dashboard,
            catalog);
    }

    internal static (AgentChatSnapshotProjection First, AgentChatSnapshotProjection Second)
        CreateWorkspaceSelectionSnapshots()
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new AgentProfileRecord(
            "race-profile",
            "Race profile",
            null,
            null,
            null,
            null,
            null,
            null,
            now,
            now);
        var firstWorkspace = new AgentWorkspaceRecord(
            "race-workspace-1",
            "First workspace",
            null,
            now,
            now);
        var secondWorkspace = new AgentWorkspaceRecord(
            "race-workspace-2",
            "Second workspace",
            null,
            now,
            now);
        var firstSession = CreateRootSession("First session", firstWorkspace.WorkspaceId, profile.ProfileId, now);
        var secondSession = CreateRootSession("Second session", secondWorkspace.WorkspaceId, profile.ProfileId, now);
        var firstSessionSnapshot = new AgentSessionSnapshot(firstSession, null);
        var secondSessionSnapshot = new AgentSessionSnapshot(secondSession, null);
        var first = new AgentChatSnapshotProjection(
            41,
            [profile],
            [firstWorkspace, secondWorkspace],
            [],
            profile,
            firstWorkspace,
            firstSessionSnapshot,
            [firstSessionSnapshot],
            new AgentTranscriptPage(41, [], false),
            new AgentChatPermissionProjection(
                41,
                new AgentSessionPermissionState(firstSession.SessionId, false),
                []));
        var second = new AgentChatSnapshotProjection(
            42,
            [profile],
            [firstWorkspace, secondWorkspace],
            [],
            profile,
            secondWorkspace,
            secondSessionSnapshot,
            [secondSessionSnapshot],
            new AgentTranscriptPage(42, [], false),
            new AgentChatPermissionProjection(
                42,
                new AgentSessionPermissionState(secondSession.SessionId, false),
                []));
        return (first, second);
    }

    internal static AgentSessionRecord CreateRootSession(
        string title,
        string workspaceId,
        string profileId,
        DateTimeOffset now)
    {
        var sessionId = Guid.NewGuid();
        return new AgentSessionRecord(
            sessionId,
            title,
            AgentSessionState.Active,
            now,
            now,
            RootSessionId: sessionId,
            ProfileId: profileId,
            WorkspaceId: workspaceId);
    }

    internal static SnapshotRuntime CreateSnapshotRuntime(
        IPackageContext context,
        RegressionTestExtensionCatalog? catalog = null)
    {
        catalog ??= new RegressionTestExtensionCatalog();
        var store = new AgentLocalStore(context);
        var sessions = new AgentSessionService(store, catalog.SessionCleaners);
        var workspaces = new AgentWorkspaceService(store, catalog.SessionCleaners, sessions);
        var executionTargets = new AgentExecutionTargetService(catalog.ExecutionTargets);
        var tools = new AgentToolService(
            sessions,
            workspaces,
            executionTargets,
            catalog.ToolSources,
            catalog.ObservedToolExecutions);
        var profiles = catalog.CreateProfileService(store, tools);
        return new SnapshotRuntime(
            store,
            sessions,
            workspaces,
            profiles,
            new AgentRuntimeChangeHub(profiles, workspaces, sessions));
    }

    internal static AgentPendingAttachmentViewModel CreatePendingAttachment(string fileName)
    {
        var content = new byte[] { 1, 2, 3 };
        return new AgentPendingAttachmentViewModel(
            Guid.NewGuid(),
            new AgentAttachmentUploadRequest(fileName, "text/plain", content),
            new AgentAttachmentInfo(
                fileName,
                "text/plain",
                AgentAttachmentKind.Text,
                content.Length,
                IsText: true,
                WasTruncated: false));
    }

    internal static AgentChatSnapshotProjection CreateChatSnapshotWithChildLink(long revision)
    {
        var snapshot = CreateChatSnapshot(revision);
        var now = DateTimeOffset.UtcNow;
        var selectedProfile = snapshot.SelectedProfile!;
        var session = snapshot.SelectedSession!.Session with { ProfileId = "snapshot-subagent" };
        var subagentProfile = selectedProfile with
        {
            ProfileId = "snapshot-subagent",
            DisplayName = "Snapshot subagent",
        };
        var childSession = new AgentSessionRecord(
            Guid.NewGuid(),
            "Child session",
            AgentSessionState.Completed,
            now,
            now,
            ParentSessionId: session.SessionId,
            RootSessionId: session.SessionId,
            ParentToolCallId: "call-1",
            ProfileId: subagentProfile.ProfileId,
            AgentKind: "subagent",
            WorkspaceId: session.WorkspaceId);
        var childCheckpoint = new AgentRunCheckpointRecord(
            Guid.NewGuid(),
            childSession.SessionId,
            1,
            AgentRunStatus.Completed,
            "Child complete.",
            now);
        var messageTurnId = Guid.NewGuid();
        var toolTurnId = Guid.NewGuid();
        var message = new AgentTurnRecord(
            messageTurnId,
            session.SessionId,
            AgentMessageRole.Assistant,
            AgentTurnKind.Message,
            [new AgentTurnItemRecord(
                Guid.NewGuid(),
                messageTurnId,
                0,
                AgentTurnItemKind.Text,
                "Snapshot response.",
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
            now,
            now);
        var tool = new AgentTurnRecord(
            toolTurnId,
            session.SessionId,
            AgentMessageRole.Assistant,
            AgentTurnKind.ToolCall,
            [new AgentTurnItemRecord(
                Guid.NewGuid(),
                toolTurnId,
                0,
                AgentTurnItemKind.ToolCall,
                null,
                "call-1",
                "task",
                "{}",
                null,
                null,
                null,
                false,
                false,
                null,
                null)],
            now.AddMilliseconds(1),
            now.AddMilliseconds(1));
        var selectedSession = snapshot.SelectedSession with { Session = session };
        return snapshot with
        {
            Profiles = [selectedProfile, subagentProfile],
            SelectedSession = selectedSession,
            WorkspaceSessions =
            [
                selectedSession,
                new AgentSessionSnapshot(childSession, childCheckpoint),
            ],
            InitialTranscript = new AgentTranscriptPage(revision, [message, tool], false),
        };
    }

    internal sealed record SnapshotRuntime(
        AgentLocalStore Store,
        AgentSessionService Sessions,
        AgentWorkspaceService Workspaces,
        AgentProfileService Profiles,
        AgentRuntimeChangeHub Changes);

    internal sealed class BlockingChatSnapshotRuntimeClient(AgentChatSnapshotProjection snapshot)
        : OwnerServiceStub
    {
        private readonly TaskCompletionSource _snapshotRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _snapshotInvocationCount;

        public bool IsAvailable => true;
        public TaskCompletionSource SnapshotStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<long> AfterRevisions { get; } = new();
        public int SnapshotInvocationCount => Volatile.Read(ref _snapshotInvocationCount);

        public void ReleaseSnapshot() => _snapshotRelease.TrySetResult();

        public override async ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            if (request is AgentChatSnapshotRequest)
            {
                Interlocked.Increment(ref _snapshotInvocationCount);
                SnapshotStarted.TrySetResult();
                await _snapshotRelease.Task.WaitAsync(cancellationToken);
                return (TResponse)(object)snapshot;
            }

            throw new NotSupportedException(typeof(TRequest).Name);
        }

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            var subscription = (AgentChangeSubscription)(object)request;
            AfterRevisions.Enqueue(subscription.AfterRevision);
            if (subscription.AfterRevision == snapshot.Revision
                && snapshot.SelectedSession is { } selectedSession)
            {
                var turnId = Guid.NewGuid();
                var now = DateTimeOffset.UtcNow;
                var turn = new AgentTurnRecord(
                    turnId,
                    selectedSession.Session.SessionId,
                    AgentMessageRole.Assistant,
                    AgentTurnKind.Message,
                    [new AgentTurnItemRecord(
                        Guid.NewGuid(),
                        turnId,
                        0,
                        AgentTurnItemKind.Text,
                        "Live after snapshot.",
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
                    now,
                    now);
                yield return (TEvent)(object)new AgentRuntimeChange(
                    snapshot.Revision + 1,
                    AgentRuntimeChangeKind.Turn,
                    SessionId: selectedSession.Session.SessionId,
                    Turn: turn);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    internal sealed record ProfileDashboardFixture(
        AgentProfileSelectableCapabilityAssignmentRecord Assignment,
        AgentProfileRecord Profile,
        AgentChatSnapshotProjection Snapshot,
        AgentDashboardProjection Dashboard,
        AgentCatalogProjection Catalog);

    internal sealed class StaticChatSnapshotRuntimeClient(AgentChatSnapshotProjection snapshot)
        : OwnerServiceStub
    {
        public bool IsAvailable => true;
        public ConcurrentQueue<AgentChatSnapshotRequest> ChatSnapshotRequests { get; } = new();

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
            }
            return request is AgentChatSnapshotRequest
                ? ValueTask.FromResult((TResponse)(object)snapshot)
                : ValueTask.FromException<TResponse>(new NotSupportedException(typeof(TRequest).Name));
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
}
