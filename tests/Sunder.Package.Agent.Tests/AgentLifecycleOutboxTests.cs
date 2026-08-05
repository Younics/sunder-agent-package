using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Memory.Semantic;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentLifecycleOutboxTests
{
    [Fact]
    public void RunStart_OutboxFailure_RollsBackSourceMutation()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var (session, run) = CreateReservedRun(store);
        using (var connection = Open(store.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER RejectLifecycleOutboxInsert
                BEFORE INSERT ON AgentLifecycleOutbox
                BEGIN
                    SELECT RAISE(ABORT, 'outbox rejected');
                END;
                """;
            command.ExecuteNonQuery();
        }

        Assert.Throws<SqliteException>(() => store.TryStartRun(
            run.Key,
            run.Epoch,
            "Remember that the project uses cobalt.",
            [],
            rollbackAnchorTurnId: null,
            "Running."));

        Assert.Empty(store.ListTurns(session.SessionId));
        Assert.Equal(AgentDurableRunStatus.Preparing, store.GetRun(run.Key.RunId)?.Status);
        Assert.Empty(store.ListLifecycleOutboxEvents());
    }

    [Fact]
    public void WorkspaceDeletion_OutboxFailure_RollsBackSourceMutation()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessions = new AgentSessionService(store);
        var workspaces = new AgentWorkspaceService(store, sessionService: sessions);
        var workspace = workspaces.CreateWorkspace("Lifecycle workspace");
        var session = sessions.CreateSession(
            "Lifecycle session",
            profileId: "profile.lifecycle",
            workspaceId: workspace.WorkspaceId);
        using (var connection = Open(store.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER RejectWorkspaceLifecycleOutboxInsert
                BEFORE INSERT ON AgentLifecycleOutbox
                WHEN NEW.EventType = 'WorkspaceDeleted'
                BEGIN
                    SELECT RAISE(ABORT, 'outbox rejected');
                END;
                """;
            command.ExecuteNonQuery();
        }

        Assert.Throws<SqliteException>(() => workspaces.DeleteWorkspace(workspace.WorkspaceId));

        Assert.NotNull(workspaces.GetWorkspace(workspace.WorkspaceId));
        Assert.NotNull(sessions.GetSession(session.SessionId));
        Assert.Empty(store.ListLifecycleOutboxEvents());
    }

    [Fact]
    public async Task WorkspaceDeletion_CleanerFailure_RetriesDurableIdsOnlyJob()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var catalog = new RegressionTestExtensionCatalog();
        var cleaner = new ThrowingSessionDataCleaner();
        catalog.AddProvider(AgentRpcServices.SessionCleaners, cleaner);
        var sessions = new AgentSessionService(store, catalog);
        var workspaces = new AgentWorkspaceService(store, catalog, sessions);
        var workspace = workspaces.CreateWorkspace("Lifecycle workspace");
        var session = sessions.CreateSession(
            "Lifecycle session",
            profileId: "profile.lifecycle",
            workspaceId: workspace.WorkspaceId);

        workspaces.DeleteWorkspace(workspace.WorkspaceId);

        Assert.Equal([session.SessionId], cleaner.SessionIds);
        Assert.Null(workspaces.GetWorkspace(workspace.WorkspaceId));
        Assert.Null(sessions.GetSession(session.SessionId));
        var lifecycleEvent = Assert.Single(store.ListLifecycleOutboxEvents());
        Assert.Equal(AgentLifecycleEventKind.WorkspaceDeleted, lifecycleEvent.Kind);
        Assert.Equal([session.SessionId], lifecycleEvent.ToEnvelope().Payload.DeletedSessionIds);

        var pending = Assert.Single(store.ListSessionCleanupJobs());
        Assert.Equal("test.package", pending.PackageId);
        Assert.Equal(cleaner.CleanerId, pending.CleanerId);
        Assert.Equal(session.SessionId, pending.SessionId);
        Assert.Equal("Pending", pending.Status);
        Assert.Equal(1, pending.AttemptCount);
        Assert.Equal("cleanup_callback_failure (System.InvalidOperationException)", pending.LastError);

        await using var dispatcher = new AgentSessionCleanupDispatcher(store, catalog);
        await Task.Delay(300);
        await dispatcher.FlushAsync();
        Assert.Equal("Completed", Assert.Single(store.ListSessionCleanupJobs()).Status);
        Assert.Equal([session.SessionId, session.SessionId], cleaner.SessionIds);
    }

    [Fact]
    public void ExpiredLease_IsReclaimedWithoutAdvancingPastTheEvent()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        CreateStartedRun(store);
        var subscription = CreateSubscription("lease-observer");
        var now = DateTimeOffset.UtcNow;
        store.ReconcileLifecycleSubscription(subscription, now);

        var first = Assert.IsType<AgentLifecycleDeliveryClaim>(store.TryClaimLifecycleDelivery(
            subscription.SubscriptionId,
            now,
            TimeSpan.FromSeconds(1)));
        var recovered = Assert.IsType<AgentLifecycleDeliveryClaim>(store.TryClaimLifecycleDelivery(
            subscription.SubscriptionId,
            now.AddSeconds(2),
            TimeSpan.FromSeconds(1)));

        Assert.Equal(first.Event.EventId, recovered.Event.EventId);
        Assert.NotEqual(first.LeaseToken, recovered.LeaseToken);
    }

    [Fact]
    public async Task ObserverSideEffectThenFailure_IsDeliveredAtLeastOnce()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        CreateStartedRun(store);
        var catalog = new RegressionTestExtensionCatalog();
        var observer = new RecordingObserver("duplicate-observer") { FailuresRemaining = 1 };
        catalog.AddProvider(AgentRpcServices.DurableLifecycleObservers, observer);
        await using var dispatcher = new AgentLifecycleDispatcher(store, catalog);

        await dispatcher.FlushAsync();
        await Task.Delay(300);
        await dispatcher.FlushAsync();

        Assert.Equal(2, observer.DeliveryCount);
        Assert.Single(observer.EventIds.Distinct(StringComparer.Ordinal));
        var subscriptionId = AgentLocalStore.BuildLifecycleSubscriptionId(
            "test.package",
            observer.ObserverId,
            "Durable");
        Assert.Equal("Delivered", store.GetLifecycleDeliveryState(subscriptionId, 1)?.Status);
    }

    [Fact]
    public async Task MissingKnownObserver_ReceivesPendingEventAfterReappearance()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var catalog = new RegressionTestExtensionCatalog();
        var firstActivation = new RecordingObserver("stable-observer");
        catalog.AddProvider(AgentRpcServices.DurableLifecycleObservers, firstActivation);
        await using var dispatcher = new AgentLifecycleDispatcher(store, catalog);
        await dispatcher.FlushAsync();
        catalog.RemoveProvider(AgentRpcServices.DurableLifecycleObservers, firstActivation);

        CreateStartedRun(store);
        await dispatcher.FlushAsync();
        Assert.Equal(0, firstActivation.DeliveryCount);

        var secondActivation = new RecordingObserver("stable-observer");
        catalog.AddProvider(AgentRpcServices.DurableLifecycleObservers, secondActivation);
        await dispatcher.FlushAsync();

        Assert.Equal(1, secondActivation.DeliveryCount);
    }

    [Fact]
    public async Task DiscoveryRemovalRace_ReleasesDeliveryWithoutIncrementingAttempt()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        CreateStartedRun(store);
        var catalog = new RegressionTestExtensionCatalog();
        SelfRemovingDurableObserver? observer = null;
        observer = new SelfRemovingDurableObserver(
            "discovery-race-observer",
            () => catalog.RemoveProvider(AgentRpcServices.DurableLifecycleObservers, observer!));
        catalog.AddProvider(AgentRpcServices.DurableLifecycleObservers, observer);
        await using var dispatcher = new AgentLifecycleDispatcher(store, catalog);

        await dispatcher.FlushAsync();

        var subscriptionId = AgentLocalStore.BuildLifecycleSubscriptionId(
            "test.package",
            "discovery-race-observer",
            "Durable");
        var state = Assert.IsType<AgentLifecycleDeliveryState>(store.GetLifecycleDeliveryState(subscriptionId, 1));
        Assert.Equal("Pending", state.Status);
        Assert.Equal(0, state.AttemptCount);
        Assert.Equal(0, observer.DeliveryCount);
    }

    [Fact]
    public async Task DurableObserverRetirement_CancelsCallbackAndReleasesDeliveryWithoutAttempt()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        CreateStartedRun(store);
        var catalog = new RegressionTestExtensionCatalog();
        var observer = new BlockingDurableObserver("blocking-durable-observer");
        catalog.AddProvider(AgentRpcServices.DurableLifecycleObservers, observer);
        await using var dispatcher = new AgentLifecycleDispatcher(store, catalog);

        var flush = dispatcher.FlushAsync();
        await observer.Started.WaitAsync(TimeSpan.FromSeconds(2));
        catalog.RemoveProvider(AgentRpcServices.DurableLifecycleObservers, observer);
        await flush.WaitAsync(TimeSpan.FromSeconds(2));

        var subscriptionId = AgentLocalStore.BuildLifecycleSubscriptionId(
            "test.package",
            observer.ObserverId,
            "Durable");
        var state = Assert.IsType<AgentLifecycleDeliveryState>(store.GetLifecycleDeliveryState(subscriptionId, 1));
        Assert.True(observer.RetirementObserved);
        Assert.Equal("Pending", state.Status);
        Assert.Equal(0, state.AttemptCount);
    }

    [Fact]
    public async Task PermanentlyFailingObserver_ReachesBoundedPoisonState()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        CreateStartedRun(store);
        var catalog = new RegressionTestExtensionCatalog();
        var observer = new RecordingObserver("poison-observer") { AlwaysFail = true };
        catalog.AddProvider(AgentRpcServices.DurableLifecycleObservers, observer);
        await using var dispatcher = new AgentLifecycleDispatcher(store, catalog);
        await dispatcher.StartAsync();
        var subscriptionId = AgentLocalStore.BuildLifecycleSubscriptionId(
            "test.package",
            observer.ObserverId,
            "Durable");

        await WaitUntilAsync(
            () => store.GetLifecycleDeliveryState(subscriptionId, 1)?.Status == "Poison",
            TimeSpan.FromSeconds(8));
        await dispatcher.StopAsync();

        var state = Assert.IsType<AgentLifecycleDeliveryState>(store.GetLifecycleDeliveryState(subscriptionId, 1));
        Assert.Equal(AgentLocalStore.MaxLifecycleDeliveryAttempts, state.AttemptCount);
        Assert.Equal(AgentLocalStore.MaxLifecycleDeliveryAttempts, observer.DeliveryCount);
        Assert.NotNull(state.LastError);
    }

    [Fact]
    public void RollbackBarrier_AppliesToLaterRunsAcrossTheSessionRoot()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Barrier workspace");
        var root = store.CreateSession(
            "Root session",
            profileId: "profile.lifecycle",
            workspaceId: workspace.WorkspaceId);
        var anchor = store.AppendTextTurn(root.SessionId, AgentMessageRole.User, "replace this turn");
        store.AppendTextTurn(root.SessionId, AgentMessageRole.Assistant, "stale response");

        var rollback = store.RollbackTranscript(root.SessionId, anchor.TurnId);
        var laterRootRun = store.ReserveRun(root.SessionId, "profile.lifecycle", "later root request");
        var child = store.CreateSession(
            "Child session",
            parentSessionId: root.SessionId,
            rootSessionId: root.SessionId,
            profileId: "profile.lifecycle",
            workspaceId: workspace.WorkspaceId);
        var laterChildRun = store.ReserveRun(child.SessionId, "profile.lifecycle", "later child request");

        Assert.NotNull(rollback.MemoryConsistencyBarrier);
        Assert.Equal(rollback.MemoryConsistencyBarrier, store.GetMemoryConsistencyBarrier(laterRootRun.Key.RunId));
        Assert.Equal(rollback.MemoryConsistencyBarrier, store.GetMemoryConsistencyBarrier(laterChildRun.Key.RunId));
    }

    [Fact]
    public void PoisonedDelivery_BlocksItsOrderingKeyUntilCooldownThenRetries()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var (_, firstRun) = CreateReservedRun(store);
        Assert.NotNull(store.TryStartRun(
            firstRun.Key,
            firstRun.Epoch,
            firstRun.UserMessage,
            [],
            rollbackAnchorTurnId: null,
            "Running."));
        store.SaveCheckpoint(
            firstRun.Key.SessionId,
            firstRun.Key.RunRevision,
            AgentRunStatus.Completed,
            "Completed.");
        var (_, unrelatedRun) = CreateReservedRun(store);
        Assert.NotNull(store.TryStartRun(
            unrelatedRun.Key,
            unrelatedRun.Epoch,
            unrelatedRun.UserMessage,
            [],
            rollbackAnchorTurnId: null,
            "Running."));
        var events = store.ListLifecycleOutboxEvents();
        Assert.Equal(3, events.Count);
        Assert.Equal(events[0].OrderingKey, events[1].OrderingKey);
        Assert.NotEqual(events[0].OrderingKey, events[2].OrderingKey);

        var subscription = CreateSubscription("ordering-barrier-observer");
        var now = DateTimeOffset.UtcNow;
        store.ReconcileLifecycleSubscription(subscription, now);
        var poisonedClaim = Assert.IsType<AgentLifecycleDeliveryClaim>(store.TryClaimLifecycleDelivery(
            subscription.SubscriptionId,
            now,
            TimeSpan.FromSeconds(30)));
        var failure = store.FailLifecycleDelivery(
            poisonedClaim,
            new AgentDurableLifecycleIntegrityException("poison fixture"),
            now,
            permanent: true);

        Assert.True(failure.Poisoned);
        var unrelatedClaim = Assert.IsType<AgentLifecycleDeliveryClaim>(store.TryClaimLifecycleDelivery(
            subscription.SubscriptionId,
            now,
            TimeSpan.FromSeconds(30)));
        Assert.Equal(events[2].Sequence, unrelatedClaim.Event.Sequence);
        Assert.True(store.CompleteLifecycleDelivery(unrelatedClaim, now));
        Assert.Null(store.TryClaimLifecycleDelivery(subscription.SubscriptionId, now, TimeSpan.FromSeconds(30)));
        Assert.Null(store.TryClaimLifecycleDelivery(
            subscription.SubscriptionId,
            failure.NextAttemptAtUtc.AddTicks(-1),
            TimeSpan.FromSeconds(30)));

        var retryClaim = Assert.IsType<AgentLifecycleDeliveryClaim>(store.TryClaimLifecycleDelivery(
            subscription.SubscriptionId,
            failure.NextAttemptAtUtc,
            TimeSpan.FromSeconds(30)));
        Assert.Equal(events[0].Sequence, retryClaim.Event.Sequence);
        Assert.True(store.CompleteLifecycleDelivery(retryClaim, failure.NextAttemptAtUtc));
        var unblocked = Assert.IsType<AgentLifecycleDeliveryClaim>(store.TryClaimLifecycleDelivery(
            subscription.SubscriptionId,
            failure.NextAttemptAtUtc,
            TimeSpan.FromSeconds(30)));
        Assert.Equal(events[1].Sequence, unblocked.Event.Sequence);
    }

    [Fact]
    public void ExplicitPoisonRecovery_PreservesFailureDiagnostics()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        CreateStartedRun(store);
        var subscription = CreateSubscription("diagnostic-recovery-observer");
        var now = DateTimeOffset.UtcNow;
        store.ReconcileLifecycleSubscription(subscription, now);
        var claim = Assert.IsType<AgentLifecycleDeliveryClaim>(store.TryClaimLifecycleDelivery(
            subscription.SubscriptionId,
            now,
            TimeSpan.FromSeconds(30)));
        var failure = store.FailLifecycleDelivery(
            claim,
            new AgentDurableLifecycleIntegrityException("preserve this diagnostic"),
            now,
            permanent: true);
        var poisoned = Assert.IsType<AgentLifecycleDeliveryState>(
            store.GetLifecycleDeliveryState(subscription.SubscriptionId, claim.Event.Sequence));
        var recoveredAt = now.AddSeconds(1);

        Assert.Equal(1, store.RecoverPoisonedLifecycleDeliveries(recoveredAt));

        var recovered = Assert.IsType<AgentLifecycleDeliveryState>(
            store.GetLifecycleDeliveryState(subscription.SubscriptionId, claim.Event.Sequence));
        Assert.Equal("Pending", recovered.Status);
        Assert.Equal(failure.AttemptCount, recovered.AttemptCount);
        Assert.Equal(recoveredAt, recovered.NextAttemptAtUtc);
        Assert.Equal(poisoned.LastError, recovered.LastError);
    }

    [Fact]
    public async Task DispatcherRestart_ImmediatelyRetriesPoisonedDelivery()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        CreateStartedRun(store);
        var observer = new RecordingObserver("restart-recovery-observer");
        var subscription = CreateSubscription(observer.ObserverId);
        var now = DateTimeOffset.UtcNow;
        store.ReconcileLifecycleSubscription(subscription, now);
        var claim = Assert.IsType<AgentLifecycleDeliveryClaim>(store.TryClaimLifecycleDelivery(
            subscription.SubscriptionId,
            now,
            TimeSpan.FromSeconds(30)));
        var failure = store.FailLifecycleDelivery(
            claim,
            new InvalidOperationException("observer failed before restart"),
            now,
            permanent: true);
        Assert.True(failure.NextAttemptAtUtc > now.AddMinutes(1));

        var catalog = new RegressionTestExtensionCatalog();
        catalog.AddProvider(AgentRpcServices.DurableLifecycleObservers, observer);
        await using var dispatcher = new AgentLifecycleDispatcher(store, catalog);
        await dispatcher.StartAsync();
        await WaitUntilAsync(
            () => store.GetLifecycleDeliveryState(subscription.SubscriptionId, claim.Event.Sequence)?.Status == "Delivered",
            TimeSpan.FromSeconds(2));
        await dispatcher.StopAsync();

        Assert.Equal(1, observer.DeliveryCount);
    }

    [Fact]
    public void LegacyRollbackOrderingKey_BlocksRecallAndDispatchesBeforeCurrentEvents()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Legacy ordering workspace");
        var session = store.CreateSession(
            "Legacy ordering session",
            profileId: "profile.lifecycle",
            workspaceId: workspace.WorkspaceId);
        var anchor = store.AppendTextTurn(session.SessionId, AgentMessageRole.User, "replace this turn");
        store.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, "stale response");
        var rollback = store.RollbackTranscript(session.SessionId, anchor.TurnId);
        var rollbackEvent = Assert.Single(store.ListLifecycleOutboxEvents());
        var legacyOrderingKey = $"workspace:{workspace.WorkspaceId}:root:{session.SessionId:N}";
        RewriteLifecycleOrderingKey(store.DatabasePath, rollbackEvent.Sequence, legacyOrderingKey);

        var laterRun = store.ReserveRun(session.SessionId, "profile.lifecycle", "later request");
        Assert.NotNull(store.TryStartRun(
            laterRun.Key,
            laterRun.Epoch,
            laterRun.UserMessage,
            [],
            rollbackAnchorTurnId: null,
            "Running."));
        var events = store.ListLifecycleOutboxEvents();
        Assert.Equal(2, events.Count);
        Assert.Equal(legacyOrderingKey, events[0].OrderingKey);
        Assert.NotEqual(events[0].OrderingKey, events[1].OrderingKey);
        Assert.Equal(rollback.MemoryConsistencyBarrier, store.GetMemoryConsistencyBarrier(laterRun.Key.RunId));

        var subscription = CreateSubscription("legacy-ordering-observer");
        var now = DateTimeOffset.UtcNow;
        store.ReconcileLifecycleSubscription(subscription, now);
        var legacyClaim = Assert.IsType<AgentLifecycleDeliveryClaim>(store.TryClaimLifecycleDelivery(
            subscription.SubscriptionId,
            now,
            TimeSpan.FromSeconds(30)));
        Assert.Equal(events[0].Sequence, legacyClaim.Event.Sequence);
        Assert.True(store.CompleteLifecycleDelivery(legacyClaim, now));
        var currentClaim = Assert.IsType<AgentLifecycleDeliveryClaim>(store.TryClaimLifecycleDelivery(
            subscription.SubscriptionId,
            now,
            TimeSpan.FromSeconds(30)));
        Assert.Equal(events[1].Sequence, currentClaim.Event.Sequence);
    }

    [Fact]
    public void WorkspaceDeleteRecreateDelete_UsesDistinctDurableIncarnations()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspaces = new AgentWorkspaceService(store);
        var exportedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var imported = new AgentWorkspaceRecord(
            "reused-workspace-id",
            "Imported workspace",
            null,
            exportedAt,
            exportedAt);

        workspaces.ImportWorkspace(imported);
        var first = Assert.IsType<AgentWorkspaceRecord>(workspaces.GetWorkspace(imported.WorkspaceId));
        workspaces.DeleteWorkspace(imported.WorkspaceId);
        workspaces.ImportWorkspace(imported);
        var second = Assert.IsType<AgentWorkspaceRecord>(workspaces.GetWorkspace(imported.WorkspaceId));
        workspaces.DeleteWorkspace(imported.WorkspaceId);

        var deletions = store.ListLifecycleOutboxEvents()
            .Where(item => item.Kind == AgentLifecycleEventKind.WorkspaceDeleted)
            .ToArray();
        Assert.Equal(2, deletions.Length);
        Assert.NotEqual(first.CreatedAtUtc, second.CreatedAtUtc);
        Assert.NotEqual(deletions[0].SourceKey, deletions[1].SourceKey);
        Assert.NotEqual(deletions[0].EventId, deletions[1].EventId);
        Assert.NotEqual(
            deletions[0].ToEnvelope().Payload.WorkspaceIncarnationId,
            deletions[1].ToEnvelope().Payload.WorkspaceIncarnationId);
    }

    [Fact]
    public void LargeRollback_EmitsExactBoundedChunksAndFinalReceiptManifest()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Large rollback workspace");
        var session = store.CreateSession(
            "Large rollback session",
            profileId: "profile.lifecycle",
            workspaceId: workspace.WorkspaceId);
        var anchor = store.AppendTextTurn(session.SessionId, AgentMessageRole.User, "large rollback anchor");
        const int additionalTurnCount = 7_000;
        InsertTurns(store.DatabasePath, session.SessionId, anchor.CreatedAtUtc, additionalTurnCount);

        var rollback = store.RollbackTranscript(session.SessionId, anchor.TurnId);

        Assert.Equal(additionalTurnCount + 1, rollback.DeletedTurnIds.Count);
        var events = store.ListLifecycleOutboxEvents()
            .Where(item => item.Kind == AgentLifecycleEventKind.TranscriptRolledBack)
            .ToArray();
        Assert.True(events.Length > 2);
        Assert.All(events, item => Assert.InRange(
            Encoding.UTF8.GetByteCount(item.PayloadJson),
            1,
            AgentLocalStore.MaxLifecyclePayloadBytes));
        var manifest = events[^1].ToEnvelope();
        Assert.Equal(rollback.MemoryConsistencyBarrier, new AgentMemoryConsistencyBarrier(
            manifest.EventId,
            manifest.PayloadHash));
        Assert.Null(manifest.Payload.DeletionChunkIndex);
        Assert.Equal(events.Length - 1, manifest.Payload.DeletionChunkCount);
        Assert.Equal(events.Length - 1, manifest.Payload.DeletionChunkReceipts.Count);
        Assert.Equal(additionalTurnCount + 1, manifest.Payload.DeletedTurnCount);
        Assert.Equal(
            events[..^1].Select(item => new AgentMemoryConsistencyBarrier(item.EventId, item.PayloadHash)),
            manifest.Payload.DeletionChunkReceipts);
        var representedIds = events[..^1]
            .SelectMany(item => item.ToEnvelope().Payload.DeletedTurnIds)
            .ToArray();
        Assert.Equal(rollback.DeletedTurnIds, representedIds);

        var semantic = new MemoryLocalStore(scope.Context);
        Assert.Throws<AgentDurableLifecycleIntegrityException>(() =>
        {
            _ = semantic.ProcessDurableLifecycleEvent(manifest, []);
        });
        foreach (var lifecycleEvent in events)
        {
            semantic.ProcessDurableLifecycleEvent(lifecycleEvent.ToEnvelope(), []);
        }
        Assert.True(semantic.HasLifecycleInboxReceipt(rollback.MemoryConsistencyBarrier!));
    }

    [Fact]
    public async Task SessionDeletion_ErasesHistoricalPayloadBeforeOrderedTombstoneReplay()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Session erasure workspace");
        var session = store.CreateSession(
            "Session erasure source",
            profileId: "profile.lifecycle",
            workspaceId: workspace.WorkspaceId);
        var fixture = InsertSensitiveLifecycleEvent(store, workspace, session, "session-delete");
        var knownObserver = new RecordingObserver("known-session-erasure-observer");
        store.ReconcileLifecycleSubscription(CreateSubscription(knownObserver.ObserverId), DateTimeOffset.UtcNow);

        store.DeleteSessionTree(session.SessionId);

        var events = store.ListLifecycleOutboxEvents();
        Assert.Equal(2, events.Count);
        AssertErasedRecord(events[0], fixture.OriginalPayloadHash);
        Assert.Equal(AgentLifecycleEventKind.SessionDeleted, events[1].Kind);
        AssertDatabaseHasNoCanaries(store.DatabasePath, fixture.Canaries);

        var catalog = new RegressionTestExtensionCatalog();
        catalog.AddProvider(AgentRpcServices.DurableLifecycleObservers, knownObserver);
        await using var dispatcher = new AgentLifecycleDispatcher(store, catalog);
        await dispatcher.FlushAsync();

        Assert.Collection(
            knownObserver.Events,
            erased =>
            {
                Assert.True(erased.Payload.ContentErased);
                Assert.Equal(fixture.OriginalPayloadHash, erased.OriginalPayloadHash);
                AssertNoCanaries(erased.PayloadJson, fixture.Canaries);
            },
            deleted => Assert.Equal(AgentLifecycleEventKind.SessionDeleted, deleted.Kind));

        var newObserver = new RecordingObserver("new-session-erasure-observer");
        catalog.AddProvider(AgentRpcServices.DurableLifecycleObservers, newObserver);
        await dispatcher.FlushAsync();

        var replayedTombstone = Assert.Single(newObserver.Events);
        Assert.Equal(AgentLifecycleEventKind.SessionDeleted, replayedTombstone.Kind);
        Assert.False(replayedTombstone.Payload.ContentErased);
        AssertNoCanaries(replayedTombstone.PayloadJson, fixture.Canaries);
    }

    [Fact]
    public async Task WorkspaceDeletion_ErasesAllCurrentIncarnationPayloadsBeforeTombstoneReplay()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Workspace erasure source");
        var session = store.CreateSession(
            "Workspace erasure session",
            profileId: "profile.lifecycle",
            workspaceId: workspace.WorkspaceId);
        var fixture = InsertSensitiveLifecycleEvent(store, workspace, session, "workspace-delete");
        var observer = new RecordingObserver("known-workspace-erasure-observer");
        store.ReconcileLifecycleSubscription(CreateSubscription(observer.ObserverId), DateTimeOffset.UtcNow);

        store.DeleteWorkspace(workspace.WorkspaceId);

        var events = store.ListLifecycleOutboxEvents();
        Assert.Equal(2, events.Count);
        AssertErasedRecord(events[0], fixture.OriginalPayloadHash);
        Assert.Equal(AgentLifecycleEventKind.WorkspaceDeleted, events[1].Kind);
        AssertDatabaseHasNoCanaries(store.DatabasePath, fixture.Canaries);

        var catalog = new RegressionTestExtensionCatalog();
        catalog.AddProvider(AgentRpcServices.DurableLifecycleObservers, observer);
        await using var dispatcher = new AgentLifecycleDispatcher(store, catalog);
        await dispatcher.FlushAsync();

        Assert.Collection(
            observer.Events,
            erased => Assert.True(erased.Payload.ContentErased),
            deleted => Assert.Equal(AgentLifecycleEventKind.WorkspaceDeleted, deleted.Kind));
        Assert.All(observer.Events, item => AssertNoCanaries(item.PayloadJson, fixture.Canaries));
    }

    [Fact]
    public void SessionDeletion_ErasureKeepsPoisonRetryAsTheTombstoneOrderingBarrier()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Poison erasure workspace");
        var session = store.CreateSession(
            "Poison erasure session",
            profileId: "profile.lifecycle",
            workspaceId: workspace.WorkspaceId);
        InsertSensitiveLifecycleEvent(store, workspace, session, "poison-delete");
        var subscription = CreateSubscription("poison-erasure-observer");
        var now = DateTimeOffset.UtcNow;
        store.ReconcileLifecycleSubscription(subscription, now);
        var originalClaim = Assert.IsType<AgentLifecycleDeliveryClaim>(store.TryClaimLifecycleDelivery(
            subscription.SubscriptionId,
            now,
            TimeSpan.FromSeconds(30)));
        var failure = store.FailLifecycleDelivery(
            originalClaim,
            new AgentDurableLifecycleIntegrityException("fixture integrity failure"),
            now,
            permanent: true);

        store.DeleteSessionTree(session.SessionId);

        Assert.Null(store.TryClaimLifecycleDelivery(
            subscription.SubscriptionId,
            failure.NextAttemptAtUtc.AddTicks(-1),
            TimeSpan.FromSeconds(30)));
        var erasedRetry = Assert.IsType<AgentLifecycleDeliveryClaim>(store.TryClaimLifecycleDelivery(
            subscription.SubscriptionId,
            failure.NextAttemptAtUtc,
            TimeSpan.FromSeconds(30)));
        Assert.Equal(originalClaim.Event.Sequence, erasedRetry.Event.Sequence);
        Assert.True(erasedRetry.Event.ToEnvelope().Payload.ContentErased);
        Assert.True(store.CompleteLifecycleDelivery(erasedRetry, failure.NextAttemptAtUtc));
        var tombstone = Assert.IsType<AgentLifecycleDeliveryClaim>(store.TryClaimLifecycleDelivery(
            subscription.SubscriptionId,
            failure.NextAttemptAtUtc,
            TimeSpan.FromSeconds(30)));
        Assert.Equal(AgentLifecycleEventKind.SessionDeleted, tombstone.Event.Kind);
    }

    [Fact]
    public void SessionDeletion_RearmsDeliveredAndMissingReceiptsAndRejectsStaleInflightCompletion()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Delivery-version workspace");
        var session = store.CreateSession(
            "Delivery-version session",
            profileId: "profile.lifecycle",
            workspaceId: workspace.WorkspaceId);
        InsertSensitiveLifecycleEvent(store, workspace, session, "delivery-version");
        var now = DateTimeOffset.UtcNow;
        var deliveredSubscription = CreateSubscription("delivered-erasure-observer");
        var inflightSubscription = CreateSubscription("inflight-erasure-observer");
        var missingSubscription = CreateSubscription("missing-erasure-observer");
        store.ReconcileLifecycleSubscription(deliveredSubscription, now);
        store.ReconcileLifecycleSubscription(inflightSubscription, now);
        store.ReconcileLifecycleSubscription(missingSubscription, now);

        var deliveredClaim = Assert.IsType<AgentLifecycleDeliveryClaim>(store.TryClaimLifecycleDelivery(
            deliveredSubscription.SubscriptionId,
            now,
            TimeSpan.FromMinutes(1)));
        Assert.True(store.CompleteLifecycleDelivery(deliveredClaim, now));
        var deliveredVersion = Assert.IsType<AgentLifecycleDeliveryState>(
            store.GetLifecycleDeliveryState(deliveredSubscription.SubscriptionId, 1)).DeliveryVersion;
        var staleInflight = Assert.IsType<AgentLifecycleDeliveryClaim>(store.TryClaimLifecycleDelivery(
            inflightSubscription.SubscriptionId,
            now,
            TimeSpan.FromMinutes(1)));
        using (var connection = Open(store.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DELETE FROM AgentLifecycleDeliveries
                WHERE SubscriptionId = $subscriptionId AND EventSequence = 1;
                """;
            command.Parameters.AddWithValue("$subscriptionId", missingSubscription.SubscriptionId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        store.DeleteSessionTree(session.SessionId);

        var rearmedDelivered = Assert.IsType<AgentLifecycleDeliveryState>(
            store.GetLifecycleDeliveryState(deliveredSubscription.SubscriptionId, 1));
        Assert.Equal("Pending", rearmedDelivered.Status);
        Assert.Equal(deliveredVersion + 1, rearmedDelivered.DeliveryVersion);
        Assert.False(store.CompleteLifecycleDelivery(staleInflight, now.AddSeconds(1)));
        var rearmedInflight = Assert.IsType<AgentLifecycleDeliveryState>(
            store.GetLifecycleDeliveryState(inflightSubscription.SubscriptionId, 1));
        Assert.Equal("Pending", rearmedInflight.Status);
        Assert.True(rearmedInflight.DeliveryVersion > staleInflight.DeliveryVersion);
        var materializedMissing = Assert.IsType<AgentLifecycleDeliveryState>(
            store.GetLifecycleDeliveryState(missingSubscription.SubscriptionId, 1));
        Assert.Equal("Pending", materializedMissing.Status);
        Assert.Equal(1, materializedMissing.DeliveryVersion);
    }

    [Fact]
    public void ErasureReplayEligibility_UsesMonotonicMembershipInsteadOfWallClock()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Generation workspace");
        var session = store.CreateSession(
            "Generation session",
            profileId: "profile.lifecycle",
            workspaceId: workspace.WorkspaceId);
        InsertSensitiveLifecycleEvent(store, workspace, session, "generation");
        var existing = CreateSubscription("generation-existing-observer");
        store.ReconcileLifecycleSubscription(existing, DateTimeOffset.Parse("2099-01-01T00:00:00Z"));

        store.DeleteSessionTree(session.SessionId);

        var existingClaim = Assert.IsType<AgentLifecycleDeliveryClaim>(store.TryClaimLifecycleDelivery(
            existing.SubscriptionId,
            DateTimeOffset.Parse("2100-01-01T00:00:00Z"),
            TimeSpan.FromSeconds(30)));
        Assert.True(existingClaim.Event.ToEnvelope().Payload.ContentErased);
        var later = CreateSubscription("generation-later-observer");
        store.ReconcileLifecycleSubscription(later, DateTimeOffset.Parse("1900-01-01T00:00:00Z"));
        var laterClaim = Assert.IsType<AgentLifecycleDeliveryClaim>(store.TryClaimLifecycleDelivery(
            later.SubscriptionId,
            DateTimeOffset.Parse("2100-01-01T00:00:00Z"),
            TimeSpan.FromSeconds(30)));
        Assert.Equal(AgentLifecycleEventKind.SessionDeleted, laterClaim.Event.Kind);
    }

    [Fact]
    public void RetiredSubscription_ReactivationGetsNewMembershipAndSkipsPriorErasure()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Reactivation workspace");
        var session = store.CreateSession(
            "Reactivation session",
            profileId: "profile.lifecycle",
            workspaceId: workspace.WorkspaceId);
        InsertSensitiveLifecycleEvent(store, workspace, session, "reactivation");
        var subscription = CreateSubscription("reactivated-observer");
        var now = DateTimeOffset.UtcNow;
        store.ReconcileLifecycleSubscription(subscription, now);
        var originalGeneration = ReadSubscriptionGeneration(store.DatabasePath, subscription.SubscriptionId);
        store.DeleteSessionTree(session.SessionId);
        using (var connection = Open(store.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE AgentLifecycleSubscriptions
                SET LastSeenAtUtc = $lastSeenAtUtc
                WHERE SubscriptionId = $subscriptionId;
                """;
            command.Parameters.AddWithValue("$lastSeenAtUtc", now.AddDays(-31).ToString("O"));
            command.Parameters.AddWithValue("$subscriptionId", subscription.SubscriptionId);
            command.ExecuteNonQuery();
        }
        Assert.Equal(1, store.RetireInactiveLifecycleSubscriptions(now.AddDays(-30), now));

        store.ReconcileLifecycleSubscription(subscription, now.AddMinutes(1));

        Assert.True(ReadSubscriptionGeneration(store.DatabasePath, subscription.SubscriptionId) > originalGeneration);
        var claim = Assert.IsType<AgentLifecycleDeliveryClaim>(store.TryClaimLifecycleDelivery(
            subscription.SubscriptionId,
            now.AddMinutes(1),
            TimeSpan.FromSeconds(30)));
        Assert.Equal(AgentLifecycleEventKind.SessionDeleted, claim.Event.Kind);
    }

    [Fact]
    public void LifecycleOutbox_DirectSqlRejectsMalformedOrInsertedErasureState()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Constraint workspace");
        var session = store.CreateSession(
            "Constraint session",
            profileId: "profile.lifecycle",
            workspaceId: workspace.WorkspaceId);
        InsertSensitiveLifecycleEvent(store, workspace, session, "constraints");
        using var connection = Open(store.DatabasePath);

        using (var malformedUpdate = connection.CreateCommand())
        {
            malformedUpdate.CommandText = """
                UPDATE AgentLifecycleOutbox
                SET PayloadJson = '{"contentErased":true}',
                    PayloadHash = $canonicalHash,
                    PayloadState = 'Erased',
                    OriginalPayloadHash = PayloadHash,
                    PayloadErasedAtUtc = NULL,
                    PayloadErasureGeneration = (
                        SELECT CurrentGeneration FROM AgentLifecycleGenerationState WHERE SingletonId = 1)
                WHERE Sequence = 1;
                """;
            malformedUpdate.Parameters.AddWithValue(
                "$canonicalHash",
                "2cab2434dff48e3639c975cd3ac9693994e3e1ec44a4921628100a047add6950");
            Assert.Throws<SqliteException>(() => malformedUpdate.ExecuteNonQuery());
        }

        using var insertedErasure = connection.CreateCommand();
        insertedErasure.CommandText = """
            INSERT INTO AgentLifecycleOutbox (
                EventId, SourceKey, EventType, OrderingKey, WorkspaceId, SessionId,
                PayloadJson, PayloadHash, CreatedAtUtc, PayloadState, OriginalPayloadHash,
                PayloadErasedAtUtc, PayloadErasureGeneration)
            VALUES (
                'evt_direct_erasure', 'direct-erasure', 'UserTurnAdded', 'workspace:direct:root:test',
                NULL, NULL, '{"contentErased":true}', $canonicalHash, $now, 'Erased',
                $originalHash, $now,
                (SELECT CurrentGeneration FROM AgentLifecycleGenerationState WHERE SingletonId = 1));
            """;
        insertedErasure.Parameters.AddWithValue(
            "$canonicalHash",
            "2cab2434dff48e3639c975cd3ac9693994e3e1ec44a4921628100a047add6950");
        insertedErasure.Parameters.AddWithValue("$originalHash", new string('a', 64));
        insertedErasure.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        Assert.Throws<SqliteException>(() => insertedErasure.ExecuteNonQuery());
    }

    [Fact]
    public async Task SessionCleanupJob_WaitsForSamePackageCleanerReactivation()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var catalog = new RegressionTestExtensionCatalog();
        var first = new RecordingSessionDataCleaner("stable-cleaner");
        catalog.AddProvider(AgentRpcServices.SessionCleaners, first, "package.cleaner");
        await using var dispatcher = new AgentSessionCleanupDispatcher(store, catalog);
        await dispatcher.FlushAsync();
        catalog.RemoveProvider(AgentRpcServices.SessionCleaners, first);
        var sessions = new AgentSessionService(store, catalog);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Cleaner reactivation workspace");
        var session = sessions.CreateSession(
            "Cleaner reactivation session",
            profileId: "profile.lifecycle",
            workspaceId: workspace.WorkspaceId);

        sessions.DeleteSession(session.SessionId);
        await dispatcher.FlushAsync();

        Assert.Empty(first.SessionIds);
        Assert.Equal("Pending", Assert.Single(store.ListSessionCleanupJobs()).Status);
        var replacement = new RecordingSessionDataCleaner("stable-cleaner");
        catalog.AddProvider(AgentRpcServices.SessionCleaners, replacement, "package.cleaner");
        await dispatcher.FlushAsync();
        Assert.Equal([session.SessionId], replacement.SessionIds);
        Assert.Equal("Completed", Assert.Single(store.ListSessionCleanupJobs()).Status);
    }

    [Fact]
    public void LifecycleCompaction_IgnoresRetiredSubscriptionButNeverDeletesLiveSessionHistory()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Compaction workspace");
        var session = store.CreateSession(
            "Compaction session",
            profileId: "profile.lifecycle",
            workspaceId: workspace.WorkspaceId);
        var old = DateTimeOffset.UtcNow.AddDays(-60);
        InsertSensitiveLifecycleEvent(store, workspace, session, "compaction", old);
        var active = CreateSubscription("compaction-active-observer");
        var retiring = CreateSubscription("compaction-retired-observer");
        var now = DateTimeOffset.UtcNow;
        store.ReconcileLifecycleSubscription(active, now);
        store.ReconcileLifecycleSubscription(retiring, now.AddDays(-31));
        var activeOriginal = Assert.IsType<AgentLifecycleDeliveryClaim>(store.TryClaimLifecycleDelivery(
            active.SubscriptionId,
            now,
            TimeSpan.FromSeconds(30)));
        Assert.True(store.CompleteLifecycleDelivery(activeOriginal, now));

        Assert.Equal(0, store.CompactLifecycleOutbox(now.AddDays(1)));
        store.DeleteSessionTree(session.SessionId);
        var activeErasure = Assert.IsType<AgentLifecycleDeliveryClaim>(store.TryClaimLifecycleDelivery(
            active.SubscriptionId,
            now.AddSeconds(1),
            TimeSpan.FromSeconds(30)));
        Assert.True(activeErasure.Event.ToEnvelope().Payload.ContentErased);
        Assert.True(store.CompleteLifecycleDelivery(activeErasure, now.AddSeconds(1)));
        var activeTombstone = Assert.IsType<AgentLifecycleDeliveryClaim>(store.TryClaimLifecycleDelivery(
            active.SubscriptionId,
            now.AddSeconds(1),
            TimeSpan.FromSeconds(30)));
        Assert.Equal(AgentLifecycleEventKind.SessionDeleted, activeTombstone.Event.Kind);
        Assert.True(store.CompleteLifecycleDelivery(activeTombstone, now.AddSeconds(1)));
        Assert.Equal(1, store.RetireInactiveLifecycleSubscriptions(now.AddDays(-30), now));

        Assert.Equal(2, store.CompactLifecycleOutbox(now.AddDays(1)));
        Assert.Empty(store.ListLifecycleOutboxEvents());
    }

    [Fact]
    public void LifecyclePoisonRecovery_UpdatesAtMostOneBoundedBatch()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Recovery bound workspace");
        var session = store.CreateSession(
            "Recovery bound session",
            profileId: "profile.lifecycle",
            workspaceId: workspace.WorkspaceId);
        var count = AgentLocalStore.MaxLifecycleReconciliationBatchSize + 44;
        InsertLifecycleEvents(store, workspace, session, count);
        var subscription = CreateSubscription("recovery-bound-observer");
        var now = DateTimeOffset.UtcNow;
        Assert.True(store.ReconcileLifecycleSubscription(subscription, now));
        Assert.False(store.ReconcileLifecycleSubscription(subscription, now));
        using (var connection = Open(store.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE AgentLifecycleDeliveries
                SET Status = 'Poison', NextAttemptAtUtc = $nextAttemptAtUtc, PoisonedAtUtc = $now;
                """;
            command.Parameters.AddWithValue("$nextAttemptAtUtc", now.AddHours(1).ToString("O"));
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            Assert.Equal(count, command.ExecuteNonQuery());
        }

        Assert.Equal(
            AgentLocalStore.MaxLifecycleReconciliationBatchSize,
            store.RecoverPoisonedLifecycleDeliveries(now));
        using var verification = Open(store.DatabasePath);
        using var remaining = verification.CreateCommand();
        remaining.CommandText = "SELECT COUNT(*) FROM AgentLifecycleDeliveries WHERE Status = 'Poison';";
        Assert.Equal(44L, Convert.ToInt64(remaining.ExecuteScalar()));
    }

    [Fact]
    public async Task HistoricalReplay_UsesBoundedPersistedCursorWithoutSkippingActivePayloads()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Replay cursor workspace");
        var session = store.CreateSession(
            "Replay cursor session",
            profileId: "profile.lifecycle",
            workspaceId: workspace.WorkspaceId);
        var totalEventCount = AgentLocalStore.MaxLifecycleReconciliationBatchSize + 44;
        InsertLifecycleEvents(store, workspace, session, totalEventCount);
        var observer = new RecordingObserver("bounded-replay-observer");
        var catalog = new RegressionTestExtensionCatalog();
        catalog.AddProvider(AgentRpcServices.DurableLifecycleObservers, observer);
        await using var dispatcher = new AgentLifecycleDispatcher(store, catalog);

        await dispatcher.FlushAsync();

        Assert.Equal(AgentLifecycleDispatcher.MaxLifecycleDispatchesPerPass, observer.DeliveryCount);
        var subscriptionId = AgentLocalStore.BuildLifecycleSubscriptionId(
            "test.package",
            observer.ObserverId,
            "Durable");
        Assert.Equal(
            (ReplayStartSequence: 1L, ReconciledThroughSequence: (long)AgentLocalStore.MaxLifecycleReconciliationBatchSize),
            ReadSubscriptionWatermarks(store.DatabasePath, subscriptionId));

        await dispatcher.FlushAsync();

        Assert.Equal(totalEventCount, observer.DeliveryCount);
        Assert.Equal(totalEventCount, observer.EventIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            (ReplayStartSequence: 1L, ReconciledThroughSequence: (long)totalEventCount),
            ReadSubscriptionWatermarks(store.DatabasePath, subscriptionId));
    }

    [Fact]
    public async Task DeletionDuringBoundedCatchUp_ReplaysErasureReceiptsBeforeTombstoneOnlyToKnownObserver()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Catch-up erasure workspace");
        var session = store.CreateSession(
            "Catch-up erasure session",
            profileId: "profile.lifecycle",
            workspaceId: workspace.WorkspaceId);
        var totalEventCount = AgentLocalStore.MaxLifecycleReconciliationBatchSize + 44;
        InsertLifecycleEvents(store, workspace, session, totalEventCount);
        var knownObserver = new RecordingObserver("catch-up-erasure-known-observer");
        var catalog = new RegressionTestExtensionCatalog();
        catalog.AddProvider(AgentRpcServices.DurableLifecycleObservers, knownObserver);
        await using var dispatcher = new AgentLifecycleDispatcher(store, catalog);
        await dispatcher.FlushAsync();
        Assert.Equal(AgentLocalStore.MaxLifecycleReconciliationBatchSize, knownObserver.DeliveryCount);

        store.DeleteSessionTree(session.SessionId);
        await dispatcher.FlushAsync();
        await dispatcher.FlushAsync();

        Assert.Equal(
            AgentLocalStore.MaxLifecycleReconciliationBatchSize + totalEventCount + 1,
            knownObserver.DeliveryCount);
        Assert.All(
            knownObserver.Events
                .Skip(AgentLocalStore.MaxLifecycleReconciliationBatchSize)
                .Take(totalEventCount),
            item => Assert.True(item.Payload.ContentErased));
        Assert.Equal(AgentLifecycleEventKind.SessionDeleted, knownObserver.Events[^1].Kind);

        var newObserver = new RecordingObserver("catch-up-erasure-new-observer");
        catalog.AddProvider(AgentRpcServices.DurableLifecycleObservers, newObserver);
        await dispatcher.FlushAsync();

        Assert.Equal(AgentLifecycleEventKind.SessionDeleted, Assert.Single(newObserver.Events).Kind);
    }

    [Fact]
    public async Task ObserverFailure_PersistsAndLogsOnlyStableRedactedDiagnostics()
    {
        const string secretCanary = "observer-secret-canary-never-persist-or-log";
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        CreateStartedRun(store);
        var observer = new RecordingObserver("redacted-failure-observer")
        {
            AlwaysFail = true,
            FailureMessage = secretCanary,
        };
        var catalog = new RegressionTestExtensionCatalog();
        catalog.AddProvider(AgentRpcServices.DurableLifecycleObservers, observer);
        var logger = new RecordingEventLogger();
        await using var dispatcher = new AgentLifecycleDispatcher(store, catalog, logger);

        await dispatcher.FlushAsync();

        var subscriptionId = AgentLocalStore.BuildLifecycleSubscriptionId(
            "test.package",
            observer.ObserverId,
            "Durable");
        var state = Assert.IsType<AgentLifecycleDeliveryState>(store.GetLifecycleDeliveryState(subscriptionId, 1));
        Assert.Equal("observer_callback_failure (System.InvalidOperationException)", state.LastError);
        Assert.DoesNotContain(secretCanary, state.LastError, StringComparison.Ordinal);
        Assert.NotEmpty(logger.Entries);
        Assert.All(logger.Entries, entry =>
        {
            Assert.True(entry.EventName is "lifecycle.delivery.retry" or "lifecycle.delivery.poisoned");
            Assert.Equal("observer_callback_failure", entry.Attributes["lifecycle.failure_code"]);
            Assert.Equal("System.InvalidOperationException", entry.Attributes["exception.type"]);
            Assert.Null(entry.Exception);
            Assert.DoesNotContain(secretCanary, entry.Message, StringComparison.Ordinal);
            Assert.All(entry.Attributes.Values, value =>
                Assert.DoesNotContain(secretCanary, value?.ToString() ?? string.Empty, StringComparison.Ordinal));
        });
        Assert.DoesNotContain(
            secretCanary,
            Encoding.UTF8.GetString(File.ReadAllBytes(store.DatabasePath)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObserverRpcFailure_LogsSafeKindAndCodeWithoutMessage()
    {
        const string secretCanary = "rpc-secret-canary-never-log";
        const string rpcCode = "observer.domain-failure";
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        CreateStartedRun(store);
        var observer = new RecordingObserver("rpc-failure-observer")
        {
            AlwaysFail = true,
            FailureException = new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.Domain,
                rpcCode,
                secretCanary)),
        };
        var catalog = new RegressionTestExtensionCatalog();
        catalog.AddProvider(AgentRpcServices.DurableLifecycleObservers, observer);
        var logger = new RecordingEventLogger();
        await using var dispatcher = new AgentLifecycleDispatcher(store, catalog, logger);

        await dispatcher.FlushAsync();

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("lifecycle.delivery.retry", entry.EventName);
        Assert.Equal(nameof(SunderRpcErrorKind.Domain), entry.Attributes["rpc.error_kind"]);
        Assert.Equal(rpcCode, entry.Attributes["rpc.error_code"]);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain(secretCanary, entry.Message, StringComparison.Ordinal);
        Assert.All(entry.Attributes.Values, value =>
            Assert.DoesNotContain(secretCanary, value?.ToString() ?? string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void DispatcherProcessingRpcFailure_ProjectsSafeKindAndCode()
    {
        const string secretCanary = "dispatcher-rpc-secret-canary-never-log";
        const string rpcCode = "rpc.endpoint.stale";
        var attributes = AgentLifecycleDispatcher.CreateProcessingFailureAttributes(
            new InvalidOperationException(
                secretCanary,
                new AggregateException(
                    new InvalidOperationException(secretCanary),
                    new SunderRpcException(new SunderRpcError(
                        SunderRpcErrorKind.StaleEndpoint,
                        rpcCode,
                        secretCanary)))));

        Assert.Equal("dispatcher_processing_failure", attributes["lifecycle.failure_code"]);
        Assert.Equal(nameof(SunderRpcErrorKind.StaleEndpoint), attributes["rpc.error_kind"]);
        Assert.Equal(rpcCode, attributes["rpc.error_code"]);
        Assert.All(attributes.Values, value =>
            Assert.DoesNotContain(secretCanary, value?.ToString() ?? string.Empty, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(nameof(AgentTurnItemRecord.TextContent), 8)]
    [InlineData(nameof(AgentTurnItemRecord.CallId), 512)]
    [InlineData(nameof(AgentTurnItemRecord.ToolId), 512)]
    [InlineData(nameof(AgentTurnItemRecord.ArgumentsJson), 8)]
    [InlineData(nameof(AgentTurnItemRecord.ResultSummary), 8)]
    [InlineData(nameof(AgentTurnItemRecord.StructuredPayloadJson), 8)]
    [InlineData(nameof(AgentTurnItemRecord.SourcesJson), 8)]
    [InlineData(nameof(AgentTurnItemRecord.ErrorCode), 256)]
    [InlineData(nameof(AgentTurnItemRecord.BackendId), 256)]
    [InlineData(nameof(AgentTurnItemRecord.PresentationPayloadJson), 8)]
    [InlineData(nameof(AgentTurnItemRecord.ToolOwnerPackageId), 256)]
    [InlineData(nameof(AgentTurnItemRecord.ToolSchemaId), 512)]
    [InlineData(nameof(AgentTurnItemRecord.ToolSchemaVersion), 128)]
    public void BoundLifecycleTurn_MarksEveryTruncatedField(string propertyName, int expectedLength)
    {
        var turnId = Guid.NewGuid();
        var oversized = new string('x', expectedLength + 1);
        var item = new AgentTurnItemRecord(
            Guid.NewGuid(),
            turnId,
            0,
            AgentTurnItemKind.ToolResult,
            "text",
            "call",
            "tool",
            "args",
            "summary",
            "structured",
            "sources",
            WasTruncated: false,
            IsError: false,
            "error",
            "backend",
            "presentation")
        {
            ToolOwnerPackageId = "owner",
            ToolSchemaId = "schema",
            ToolSchemaVersion = "version",
        };
        item = propertyName switch
        {
            nameof(AgentTurnItemRecord.TextContent) => item with { TextContent = oversized },
            nameof(AgentTurnItemRecord.CallId) => item with { CallId = oversized },
            nameof(AgentTurnItemRecord.ToolId) => item with { ToolId = oversized },
            nameof(AgentTurnItemRecord.ArgumentsJson) => item with { ArgumentsJson = oversized },
            nameof(AgentTurnItemRecord.ResultSummary) => item with { ResultSummary = oversized },
            nameof(AgentTurnItemRecord.StructuredPayloadJson) => item with { StructuredPayloadJson = oversized },
            nameof(AgentTurnItemRecord.SourcesJson) => item with { SourcesJson = oversized },
            nameof(AgentTurnItemRecord.ErrorCode) => item with { ErrorCode = oversized },
            nameof(AgentTurnItemRecord.BackendId) => item with { BackendId = oversized },
            nameof(AgentTurnItemRecord.PresentationPayloadJson) => item with { PresentationPayloadJson = oversized },
            nameof(AgentTurnItemRecord.ToolOwnerPackageId) => item with { ToolOwnerPackageId = oversized },
            nameof(AgentTurnItemRecord.ToolSchemaId) => item with { ToolSchemaId = oversized },
            nameof(AgentTurnItemRecord.ToolSchemaVersion) => item with { ToolSchemaVersion = oversized },
            _ => throw new ArgumentOutOfRangeException(nameof(propertyName)),
        };
        var now = DateTimeOffset.UtcNow;
        var turn = new AgentTurnRecord(
            turnId,
            Guid.NewGuid(),
            AgentMessageRole.Tool,
            AgentTurnKind.ToolResult,
            [item],
            now,
            now);

        var boundedItem = Assert.Single(AgentLocalStore.BoundLifecycleTurn(turn, maxItems: 1, maxText: 8).Items);

        Assert.True(boundedItem.WasTruncated);
        var boundedValue = Assert.IsType<string>(
            typeof(AgentTurnItemRecord).GetProperty(propertyName)!.GetValue(boundedItem));
        Assert.Equal(expectedLength, boundedValue.Length);
    }

    private static (AgentSessionRecord Session, AgentDurableRunRecord Run) CreateReservedRun(AgentLocalStore store)
    {
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Lifecycle tests");
        var session = store.CreateSession("Lifecycle session", profileId: "profile.lifecycle", workspaceId: workspace.WorkspaceId);
        return (session, store.ReserveRun(session.SessionId, "profile.lifecycle", "Remember that the project uses cobalt."));
    }

    private static void CreateStartedRun(AgentLocalStore store)
    {
        var (_, run) = CreateReservedRun(store);
        Assert.NotNull(store.TryStartRun(
            run.Key,
            run.Epoch,
            "Remember that the project uses cobalt.",
            [],
            rollbackAnchorTurnId: null,
            "Running."));
    }

    private static AgentLifecycleSubscription CreateSubscription(string observerId)
        => new(
            AgentLocalStore.BuildLifecycleSubscriptionId("test.package", observerId, "Durable"),
            "test.package",
            observerId,
            "Durable",
            observerId);

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private static SensitiveLifecycleFixture InsertSensitiveLifecycleEvent(
        AgentLocalStore store,
        AgentWorkspaceRecord workspace,
        AgentSessionRecord session,
        string prefix,
        DateTimeOffset? createdAtUtc = null)
    {
        var token = Guid.NewGuid().ToString("N");
        string Canary(string field) => $"{prefix}-{field}-{token}";
        var canaries = new[]
        {
            Canary("profile-display"),
            Canary("session-title"),
            Canary("session-summary"),
            Canary("user-message"),
            Canary("working-summary"),
            Canary("result-text"),
            Canary("call-id"),
            Canary("tool-id"),
            Canary("arguments"),
            Canary("result-summary"),
            Canary("structured-payload"),
            Canary("sources"),
            Canary("error-code"),
            Canary("backend-id"),
            Canary("presentation-payload"),
            Canary("owner-package"),
            Canary("schema-id"),
            Canary("schema-version"),
            Canary("checkpoint-summary"),
        };
        var turnId = Guid.NewGuid();
        var item = new AgentTurnItemRecord(
            Guid.NewGuid(),
            turnId,
            0,
            AgentTurnItemKind.ToolResult,
            canaries[5],
            canaries[6],
            canaries[7],
            canaries[8],
            canaries[9],
            canaries[10],
            canaries[11],
            WasTruncated: false,
            IsError: true,
            canaries[12],
            canaries[13],
            canaries[14])
        {
            ToolOwnerPackageId = canaries[15],
            ToolSchemaId = canaries[16],
            ToolSchemaVersion = canaries[17],
        };
        var now = createdAtUtc ?? DateTimeOffset.UtcNow;
        var turn = new AgentTurnRecord(
            turnId,
            session.SessionId,
            AgentMessageRole.Tool,
            AgentTurnKind.ToolResult,
            [item],
            now,
            now);
#pragma warning disable CS0618
        var sessionContext = new AgentSessionContextRecord(
            session.SessionId,
            session.ProfileId ?? "profile.lifecycle",
            canaries[0],
            canaries[1],
            session.State,
            canaries[2]);
#pragma warning restore CS0618
        var payload = new AgentDurableLifecycleEventPayload
        {
            Session = sessionContext,
            Run = new AgentRunContextRecord(Guid.NewGuid(), 1, AgentRunStatus.Running, false, now),
            UserMessage = canaries[3],
            WorkingSummary = canaries[4],
            Turns = [turn],
            RecentLiveBufferTurns = [turn],
            TriggerTurn = turn,
            Checkpoint = new AgentRunCheckpointRecord(
                Guid.NewGuid(),
                session.SessionId,
                1,
                AgentRunStatus.Running,
                canaries[18],
                now),
            SessionId = session.SessionId,
            RootSessionId = session.RootSessionId ?? session.SessionId,
            WorkspaceId = workspace.WorkspaceId,
            WorkspaceIncarnationId = BuildWorkspaceIncarnationId(workspace),
        };
        var payloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        AssertNoMissingCanaries(payloadJson, canaries);
        var payloadHash = ComputeHash(payloadJson);
        var sourceKey = $"test-sensitive:{prefix}:{token}";
        using var connection = Open(store.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AgentLifecycleOutbox (
                EventId, SourceKey, EventType, OrderingKey, WorkspaceId, SessionId,
                PayloadJson, PayloadHash, CreatedAtUtc)
            VALUES (
                $eventId, $sourceKey, 'ToolResultRecorded', $orderingKey, $workspaceId, $sessionId,
                $payloadJson, $payloadHash, $createdAtUtc);
            """;
        command.Parameters.AddWithValue("$eventId", AgentLocalStore.BuildLifecycleEventId(sourceKey));
        command.Parameters.AddWithValue("$sourceKey", sourceKey);
        command.Parameters.AddWithValue(
            "$orderingKey",
            $"workspace:{BuildWorkspaceIncarnationId(workspace)}:root:{(session.RootSessionId ?? session.SessionId):N}");
        command.Parameters.AddWithValue("$workspaceId", workspace.WorkspaceId);
        command.Parameters.AddWithValue("$sessionId", session.SessionId.ToString());
        command.Parameters.AddWithValue("$payloadJson", payloadJson);
        command.Parameters.AddWithValue("$payloadHash", payloadHash);
        command.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
        command.ExecuteNonQuery();
        AssertDatabaseContainsCanaries(store.DatabasePath, canaries);
        return new SensitiveLifecycleFixture(canaries, payloadHash);
    }

    private static void InsertLifecycleEvents(
        AgentLocalStore store,
        AgentWorkspaceRecord workspace,
        AgentSessionRecord session,
        int count)
    {
        var workspaceIncarnationId = BuildWorkspaceIncarnationId(workspace);
        var payloadJson = JsonSerializer.Serialize(
            new AgentDurableLifecycleEventPayload
            {
                SessionId = session.SessionId,
                RootSessionId = session.RootSessionId ?? session.SessionId,
                WorkspaceId = workspace.WorkspaceId,
                WorkspaceIncarnationId = workspaceIncarnationId,
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var payloadHash = ComputeHash(payloadJson);
        using var connection = Open(store.DatabasePath);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AgentLifecycleOutbox (
                EventId, SourceKey, EventType, OrderingKey, WorkspaceId, SessionId,
                PayloadJson, PayloadHash, CreatedAtUtc)
            VALUES (
                $eventId, $sourceKey, 'UserTurnAdded', $orderingKey, $workspaceId, $sessionId,
                $payloadJson, $payloadHash, $createdAtUtc);
            """;
        var eventId = command.Parameters.Add("$eventId", SqliteType.Text);
        var sourceKey = command.Parameters.Add("$sourceKey", SqliteType.Text);
        command.Parameters.AddWithValue(
            "$orderingKey",
            $"workspace:{workspaceIncarnationId}:root:{(session.RootSessionId ?? session.SessionId):N}");
        command.Parameters.AddWithValue("$workspaceId", workspace.WorkspaceId);
        command.Parameters.AddWithValue("$sessionId", session.SessionId.ToString());
        command.Parameters.AddWithValue("$payloadJson", payloadJson);
        command.Parameters.AddWithValue("$payloadHash", payloadHash);
        var createdAtUtc = command.Parameters.Add("$createdAtUtc", SqliteType.Text);
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < count; index++)
        {
            var currentSourceKey = $"bounded-replay:{index:D6}";
            sourceKey.Value = currentSourceKey;
            eventId.Value = AgentLocalStore.BuildLifecycleEventId(currentSourceKey);
            createdAtUtc.Value = now.AddTicks(index).ToString("O");
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static (long ReplayStartSequence, long ReconciledThroughSequence) ReadSubscriptionWatermarks(
        string databasePath,
        string subscriptionId)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ReplayStartSequence, ReconciledThroughSequence
            FROM AgentLifecycleSubscriptions
            WHERE SubscriptionId = $subscriptionId;
            """;
        command.Parameters.AddWithValue("$subscriptionId", subscriptionId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static long ReadSubscriptionGeneration(string databasePath, string subscriptionId)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MembershipGeneration
            FROM AgentLifecycleSubscriptions
            WHERE SubscriptionId = $subscriptionId;
            """;
        command.Parameters.AddWithValue("$subscriptionId", subscriptionId);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void AssertErasedRecord(AgentLifecycleOutboxRecord record, string originalPayloadHash)
    {
        Assert.True(record.PayloadErased);
        Assert.Equal("{\"contentErased\":true}", record.PayloadJson);
        Assert.Equal(originalPayloadHash, record.OriginalPayloadHash);
        Assert.NotEqual(originalPayloadHash, record.PayloadHash);
        Assert.NotNull(record.PayloadErasedAtUtc);
        var envelope = record.ToEnvelope();
        Assert.True(envelope.Payload.ContentErased);
        Assert.Equal(originalPayloadHash, envelope.OriginalPayloadHash);
    }

    private static void AssertDatabaseContainsCanaries(string databasePath, IReadOnlyList<string> canaries)
    {
        var databaseFiles = ReadSqliteDataFiles(databasePath);
        foreach (var canary in canaries)
        {
            var canaryBytes = Encoding.UTF8.GetBytes(canary);
            Assert.Contains(databaseFiles, bytes => bytes.AsSpan().IndexOf(canaryBytes) >= 0);
        }
    }

    private static void AssertDatabaseHasNoCanaries(string databasePath, IReadOnlyList<string> canaries)
    {
        using (var connection = Open(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT group_concat(PayloadJson, '') FROM AgentLifecycleOutbox;";
            AssertNoCanaries(command.ExecuteScalar() as string ?? string.Empty, canaries);
        }

        var databaseFiles = ReadSqliteDataFiles(databasePath);
        foreach (var canary in canaries)
        {
            var canaryBytes = Encoding.UTF8.GetBytes(canary);
            Assert.All(databaseFiles, bytes => Assert.Equal(-1, bytes.AsSpan().IndexOf(canaryBytes)));
        }
    }

    private static IReadOnlyList<byte[]> ReadSqliteDataFiles(string databasePath)
        => new[] { databasePath, databasePath + "-wal", databasePath + "-journal" }
            .Where(File.Exists)
            .Select(File.ReadAllBytes)
            .ToArray();

    private static void AssertNoMissingCanaries(string value, IReadOnlyList<string> canaries)
    {
        foreach (var canary in canaries)
        {
            Assert.Contains(canary, value, StringComparison.Ordinal);
        }
    }

    private static void AssertNoCanaries(string value, IReadOnlyList<string> canaries)
    {
        foreach (var canary in canaries)
        {
            Assert.DoesNotContain(canary, value, StringComparison.Ordinal);
        }
    }

    private static string BuildWorkspaceIncarnationId(AgentWorkspaceRecord workspace)
        => ComputeHash($"agent-workspace-incarnation-v1\n{workspace.WorkspaceId}\n{workspace.CreatedAtUtc:O}");

    private static string ComputeHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void RewriteLifecycleOrderingKey(string databasePath, long sequence, string orderingKey)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TRIGGER TR_AgentLifecycleOutbox_ImmutableUpdate;
            UPDATE AgentLifecycleOutbox
            SET OrderingKey = $orderingKey
            WHERE Sequence = $sequence;
            CREATE TRIGGER TR_AgentLifecycleOutbox_ImmutableUpdate
            BEFORE UPDATE ON AgentLifecycleOutbox
            BEGIN
                SELECT RAISE(ABORT, 'Agent lifecycle outbox events are immutable');
            END;
            """;
        command.Parameters.AddWithValue("$orderingKey", orderingKey);
        command.Parameters.AddWithValue("$sequence", sequence);
        command.ExecuteNonQuery();
    }

    private static void InsertTurns(
        string databasePath,
        Guid sessionId,
        DateTimeOffset anchorCreatedAtUtc,
        int count)
    {
        using var connection = Open(databasePath);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AgentTurns (TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($turnId, $sessionId, 'Assistant', 'Message', $createdAtUtc, $createdAtUtc);
            """;
        var turnId = command.Parameters.Add("$turnId", SqliteType.Text);
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        var createdAtUtc = command.Parameters.Add("$createdAtUtc", SqliteType.Text);
        for (var index = 0; index < count; index++)
        {
            turnId.Value = Guid.NewGuid().ToString();
            createdAtUtc.Value = anchorCreatedAtUtc.AddTicks(index + 1).ToString("O");
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        Assert.True(condition(), "The lifecycle delivery condition was not reached before timeout.");
    }

    private sealed class RecordingObserver(string observerId) : IAgentDurableLifecycleObserver
    {
        public string ObserverId { get; } = observerId;
        public string DisplayName => ObserverId;
        public int FailuresRemaining { get; set; }
        public bool AlwaysFail { get; set; }
        public string FailureMessage { get; set; } = "Observer fixture failure.";
        public Exception? FailureException { get; set; }
        public int DeliveryCount { get; private set; }
        public List<string> EventIds { get; } = [];
        public List<AgentDurableLifecycleEventEnvelope> Events { get; } = [];

        public ValueTask HandleDurableLifecycleEventAsync(
            AgentDurableLifecycleEventEnvelope lifecycleEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeliveryCount++;
            EventIds.Add(lifecycleEvent.EventId);
            Events.Add(lifecycleEvent);
            if (AlwaysFail || FailuresRemaining-- > 0)
            {
                throw FailureException ?? new InvalidOperationException(FailureMessage);
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SelfRemovingDurableObserver(string observerId, Action remove)
        : IAgentDurableLifecycleObserver
    {
        private int _removed;

        public string ObserverId
        {
            get
            {
                if (Interlocked.Exchange(ref _removed, 1) == 0)
                {
                    remove();
                }
                return observerId;
            }
        }

        public string DisplayName => observerId;
        public int DeliveryCount { get; private set; }

        public ValueTask HandleDurableLifecycleEventAsync(
            AgentDurableLifecycleEventEnvelope lifecycleEvent,
            CancellationToken cancellationToken = default)
        {
            DeliveryCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingDurableObserver(string observerId) : IAgentDurableLifecycleObserver
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ObserverId { get; } = observerId;
        public string DisplayName => ObserverId;
        public Task Started => _started.Task;
        public bool RetirementObserved { get; private set; }

        public async ValueTask HandleDurableLifecycleEventAsync(
            AgentDurableLifecycleEventEnvelope lifecycleEvent,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RetirementObserved = true;
                throw;
            }
        }
    }

    private sealed class RecordingEventLogger : IPackageEventLogger
    {
        public List<RecordedLogEntry> Entries { get; } = [];

        public ValueTask WriteAsync(
            PackageLogLevel level,
            string eventName,
            string message,
            IReadOnlyDictionary<string, object?>? attributes = null,
            Exception? exception = null,
            CancellationToken cancellationToken = default)
        {
            Entries.Add(new RecordedLogEntry(
                level,
                eventName,
                message,
                attributes ?? new Dictionary<string, object?>(),
                exception));
            return ValueTask.CompletedTask;
        }
    }

    private sealed record SensitiveLifecycleFixture(
        IReadOnlyList<string> Canaries,
        string OriginalPayloadHash);

    private sealed record RecordedLogEntry(
        PackageLogLevel Level,
        string EventName,
        string Message,
        IReadOnlyDictionary<string, object?> Attributes,
        Exception? Exception);

    private sealed class ThrowingSessionDataCleaner : IAgentSessionDataCleaner
    {
        public string CleanerId => "test.throwing-session-cleaner";

        public List<Guid> SessionIds { get; } = [];

        public int FailuresRemaining { get; set; } = 1;

        public void DeleteSessionData(Guid sessionId)
        {
            SessionIds.Add(sessionId);
            if (FailuresRemaining-- > 0)
            {
                throw new InvalidOperationException("Cleaner fixture failure.");
            }
        }
    }

    private sealed class RecordingSessionDataCleaner(string cleanerId) : IAgentSessionDataCleaner
    {
        public string CleanerId { get; } = cleanerId;

        public List<Guid> SessionIds { get; } = [];

        public void DeleteSessionData(Guid sessionId)
            => SessionIds.Add(sessionId);
    }
}
