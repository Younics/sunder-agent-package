using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentRuntimeStartupServiceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private static readonly SunderRpcContractDescriptor DeferredTestDescriptor =
        AgentRpcContractDescriptors.Get(AgentRpcContractIds.SessionCleaner);

    [Fact]
    public async Task WorkerActivation_DoesNotWaitForPreActivationCatalogDiscovery()
    {
        using var scope = RegressionTestPackageScope.Create();
        var client = new BlockingActivationRpcClient();
        var catalog = AgentRpcCatalog.CreateDormant(client);
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(scope.Context);
        services.AddSingleton(catalog);
        new PackageModule().ConfigureRuntimeServices(services, scope.Context);
        await using var provider = services.BuildServiceProvider();
        var deferred = new DeferredAgentRpcServiceHandler(
            "test.deferred",
            DeferredTestDescriptor,
            _ => NoOpRpcHandler.Instance);
        var lifecycle = new AgentWorkerLifecycle(provider, [deferred]);
        lifecycle.CommitGeneration(new PackageRuntimeGeneration(Guid.NewGuid(), 1));

        await lifecycle.ActivateAsync(
                static (_, _, _) => ValueTask.CompletedTask,
                CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(deferred.IsActive);
        await lifecycle.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WorkerActivation_NewerHostGenerationSupersedesPriorLeaseBeforeDeferredProvidersActivate()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = AgentRpcCatalog.CreateDormant(
            new RegressionTestRpcClient(new RegressionTestRpcState(), "sunder.package.agent"));
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(scope.Context);
        services.AddSingleton(catalog);
        new PackageModule().ConfigureRuntimeServices(services, scope.Context);
        await using var provider = services.BuildServiceProvider();
        var handlerActivated = false;
        var deferred = new DeferredAgentRpcServiceHandler("test.deferred", DeferredTestDescriptor, _ =>
        {
            handlerActivated = true;
            return NoOpRpcHandler.Instance;
        });
        var lifecycle = new AgentWorkerLifecycle(provider, [deferred]);
        var store = provider.GetRequiredService<AgentLocalStore>();
        var oldEpoch = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Assert.True(store.TryCommitRuntimeGeneration(
            new PackageRuntimeGeneration(oldEpoch, 1),
            GetCurrentProcessIdentity(),
            expectedCurrentEpoch: null,
            now,
            now.AddMinutes(5)));
        var candidate = new PackageRuntimeGeneration(Guid.NewGuid(), 2);
        lifecycle.CommitGeneration(candidate);

        var activation = lifecycle.ActivateAsync(
            static (_, _, _) => ValueTask.CompletedTask,
            CancellationToken.None).AsTask();
        await activation.WaitAsync(TestTimeout);

        Assert.True(handlerActivated);
        Assert.True(deferred.IsActive);
        Assert.Equal(candidate.ActivationId, store.OwnedRuntimeGeneration);
        Assert.True(provider.GetRequiredService<AgentBackgroundWorkService>().IsRunning);
        Assert.Equal("Expired", ExecuteString(
            store.DatabasePath,
            $"SELECT Status FROM AgentRuntimeGenerations WHERE Epoch = '{oldEpoch}';"));
        await lifecycle.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WorkerActivation_ProviderFailureRejectsCandidateAndUnwindsStartup()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = AgentRpcCatalog.CreateDormant(
            new RegressionTestRpcClient(new RegressionTestRpcState(), "sunder.package.agent"));
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(scope.Context);
        services.AddSingleton(catalog);
        new PackageModule().ConfigureRuntimeServices(services, scope.Context);
        await using var provider = services.BuildServiceProvider();
        var first = new DeferredAgentRpcServiceHandler(
            "test.first",
            DeferredTestDescriptor,
            _ => NoOpRpcHandler.Instance);
        var failing = new DeferredAgentRpcServiceHandler("test.failure", DeferredTestDescriptor, _ =>
            throw new InvalidOperationException("Injected deferred provider startup failure."));
        var lifecycle = new AgentWorkerLifecycle(provider, [first, failing]);
        lifecycle.CommitGeneration(new PackageRuntimeGeneration(Guid.NewGuid(), 1));
        var faultReported = false;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.ActivateAsync(
            (_, _, _) =>
            {
                faultReported = true;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None).AsTask());

        Assert.Contains("deferred provider startup failure", exception.Message, StringComparison.Ordinal);
        Assert.False(first.IsActive);
        Assert.False(failing.IsActive);
        Assert.False(faultReported);
        Assert.Null(provider.GetRequiredService<AgentLocalStore>().OwnedRuntimeGeneration);
        Assert.False(provider.GetRequiredService<AgentBackgroundWorkService>().IsRunning);
    }

    [Fact]
    public async Task WorkerGenerationFault_ReportsBeforeBoundedCleanupAndPreservesOriginalWhenCleanupHangs()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = AgentRpcCatalog.CreateDormant(
            new RegressionTestRpcClient(new RegressionTestRpcState(), "sunder.package.agent"));
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(scope.Context);
        services.AddSingleton(catalog);
        new PackageModule().ConfigureRuntimeServices(services, scope.Context);
        await using var provider = services.BuildServiceProvider();
        var deferred = new DeferredAgentRpcServiceHandler(
            "test.deferred",
            DeferredTestDescriptor,
            _ => NoOpRpcHandler.Instance);
        var lifecycle = new AgentWorkerLifecycle(provider, [deferred], TimeSpan.FromMilliseconds(50));
        Assert.IsAssignableFrom<ISunderWorkerLifecycleService>(lifecycle);
        lifecycle.CommitGeneration(new PackageRuntimeGeneration(Guid.NewGuid(), 1));
        var reportCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerWasActiveAtReport = false;
        var workersWereActiveAtReport = false;
        await lifecycle.ActivateAsync(
            (_, _, _) =>
            {
                providerWasActiveAtReport = deferred.IsActive;
                workersWereActiveAtReport = provider
                    .GetRequiredService<AgentBackgroundWorkService>()
                    .IsRunning;
                reportCalled.TrySetResult();
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetPrivateField(
            provider.GetRequiredService<HistorySearchIndexingService>(),
            "_worker",
            releaseCleanup.Task);
        var startup = provider.GetRequiredService<AgentRuntimeStartupService>();
        var original = new InvalidOperationException("generation ownership failed");

        FaultGenerationCompletion(startup, original);

        await reportCalled.Task.WaitAsync(TestTimeout);
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifecycle.Completion.WaitAsync(TestTimeout));
        Assert.Same(original, observed);
        Assert.True(providerWasActiveAtReport);
        Assert.True(workersWereActiveAtReport);
        Assert.False(deferred.IsActive);

        releaseCleanup.TrySetResult();
        await startup.StopAsync().WaitAsync(TestTimeout);
        await lifecycle.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WorkerGenerationFault_PreservesOriginalWhenCleanupFails()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = AgentRpcCatalog.CreateDormant(
            new RegressionTestRpcClient(new RegressionTestRpcState(), "sunder.package.agent"));
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(scope.Context);
        services.AddSingleton(catalog);
        new PackageModule().ConfigureRuntimeServices(services, scope.Context);
        var provider = services.BuildServiceProvider();
        var deferred = new DeferredAgentRpcServiceHandler(
            "test.deferred",
            DeferredTestDescriptor,
            _ => NoOpRpcHandler.Instance);
        var lifecycle = new AgentWorkerLifecycle(provider, [deferred], TimeSpan.FromSeconds(1));
        lifecycle.CommitGeneration(new PackageRuntimeGeneration(Guid.NewGuid(), 1));
        var reportCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await lifecycle.ActivateAsync(
                (_, _, _) =>
                {
                    reportCalled.TrySetResult();
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None);
            var cleanupFailure = new IOException("cleanup failed");
            SetPrivateField(
                provider.GetRequiredService<HistorySearchIndexingService>(),
                "_worker",
                Task.FromException(cleanupFailure));
            var startup = provider.GetRequiredService<AgentRuntimeStartupService>();
            var original = new InvalidOperationException("generation ownership failed");

            FaultGenerationCompletion(startup, original);

            await reportCalled.Task.WaitAsync(TestTimeout);
            var observed = await Assert.ThrowsAsync<InvalidOperationException>(
                () => lifecycle.Completion.WaitAsync(TestTimeout));
            Assert.Same(original, observed);
            Assert.False(deferred.IsActive);
            var failedWorker = GetPrivateField<Task>(
                provider.GetRequiredService<HistorySearchIndexingService>(),
                "_worker");
            Assert.Same(cleanupFailure, Assert.Single(failedWorker.Exception!.InnerExceptions));
        }
        finally
        {
            try
            {
                await provider.DisposeAsync();
            }
            catch
            {
                // The injected cleanup failure is expected during provider disposal too.
            }
        }
    }

    [Fact]
    public async Task FailedPartialStartup_UnwindsWorkersAndCanRetry()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        catalog.AddProvider(
            AgentRpcServices.DurableLifecycleObservers,
            new NoOpDurableObserver());
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(scope.Context);
        services.AddSingleton<Sunder.Package.Agent.Protocol.AgentRpcCatalog>(catalog);
        new PackageModule().ConfigureRuntimeServices(services, scope.Context);
        await using var provider = services.BuildServiceProvider();
        var projection = provider.GetRequiredService<HistorySearchStore>();
        var semanticConfiguration = projection.ChangeConfiguration(
            true,
            "startup-test.embedding",
            "startup-test-provider",
            "startup-test-model",
            new string('a', 64));
        var abandonedGeneration = projection.BeginTextGeneration();
        var startup = provider.GetRequiredService<AgentRuntimeStartupService>();
        var store = provider.GetRequiredService<AgentLocalStore>();
        using (var connection = Open(projection.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER RejectStartupHistoryConfiguration
                BEFORE UPDATE ON HistoryConfiguration
                BEGIN
                    SELECT RAISE(ABORT, 'startup history configuration rejected');
                END;
                """;
            command.ExecuteNonQuery();
        }

        await startup.StartAsync();
        var generation = new PackageRuntimeGeneration(Guid.NewGuid(), 1);
        await Assert.ThrowsAsync<SqliteException>(() => startup.CommitGenerationAsync(generation));

        Assert.Equal(semanticConfiguration.Revision, projection.GetConfiguration().Revision);
        Assert.True(projection.GetConfiguration().SemanticEnabled);
        Assert.Equal(1, ExecuteInt64(
            projection.DatabasePath,
            $"SELECT COUNT(*) FROM HistoryProjectionGenerations WHERE GenerationId = {abandonedGeneration};"));

        using (var connection = Open(projection.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER RejectStartupHistoryConfiguration;";
            command.ExecuteNonQuery();
        }
        var workspace = provider.GetRequiredService<AgentWorkspaceService>().CreateWorkspace("Retry history");
        var sessions = provider.GetRequiredService<AgentSessionService>();
        var session = sessions.CreateSession("Retry session", workspaceId: workspace.WorkspaceId);
        sessions.AppendTextTurn(session.SessionId, AgentMessageRole.User, "startup retry history");
        await startup.CommitGenerationAsync(generation);
        Assert.False(projection.GetConfiguration().SemanticEnabled);
        Assert.Equal(0, ExecuteInt64(
            projection.DatabasePath,
            $"SELECT COUNT(*) FROM HistoryProjectionGenerations WHERE GenerationId = {abandonedGeneration};"));
        await WaitUntilAsync(() => provider.GetRequiredService<HistorySearchStore>()
            .GetSnapshot().DocumentCount == 1);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(provider.GetRequiredService<AgentBackgroundWorkService>().TryQueue(_ =>
        {
            completed.TrySetResult();
            return Task.CompletedTask;
        }));
        await completed.Task.WaitAsync(TestTimeout);
        await startup.StopAsync();
    }

    [Fact]
    public async Task CanceledStartup_DoesNotLatchStartedState()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        var cleaner = new BlockingSessionDataCleaner();
        var observer = new BlockingLifecycleObserver();
        catalog.AddProvider(AgentRpcServices.SessionCleaners, cleaner);
        catalog.AddProvider(AgentRpcServices.DurableLifecycleObservers, observer);
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(scope.Context);
        services.AddSingleton<Sunder.Package.Agent.Protocol.AgentRpcCatalog>(catalog);
        new PackageModule().ConfigureRuntimeServices(services, scope.Context);
        await using var provider = services.BuildServiceProvider();
        var startup = provider.GetRequiredService<AgentRuntimeStartupService>();
        var backgroundWork = provider.GetRequiredService<AgentBackgroundWorkService>();
        var store = provider.GetRequiredService<AgentLocalStore>();
        var generation = new PackageRuntimeGeneration(Guid.NewGuid(), 1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            startup.StartAsync(cancellation.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            startup.CommitGenerationAsync(generation));
        Assert.Null(store.OwnedRuntimeGeneration);
        Assert.False(backgroundWork.IsRunning);

        await startup.StartAsync();
        Assert.False(backgroundWork.IsRunning);
        var commit = startup.CommitGenerationAsync(generation);
        try
        {
            await commit.WaitAsync(TimeSpan.FromSeconds(2));
            await cleaner.DescriptorReadStarted.WaitAsync(TimeSpan.FromSeconds(2));
            await observer.DescriptorReadStarted.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            cleaner.ReleaseDescriptor();
            observer.ReleaseDescriptor();
            await commit.WaitAsync(TestTimeout);
        }

        Assert.Equal(generation.ActivationId, store.OwnedRuntimeGeneration);
        Assert.True(backgroundWork.IsRunning);
        await startup.StopAsync();
        Assert.Null(store.OwnedRuntimeGeneration);
        Assert.False(backgroundWork.IsRunning);
        var stopped = Assert.IsType<AgentRuntimeGenerationOwnership>(store.GetCurrentRuntimeGeneration());
        Assert.Equal(generation.ActivationId, stopped.Epoch);
        Assert.Equal("Stopped", stopped.Status);
    }

    [Fact]
    public async Task CandidateStart_NewerHostGenerationSupersedesLivePriorGenerationAndRecovers()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var provider = CreateProvider(scope.Context);
        var startup = provider.GetRequiredService<AgentRuntimeStartupService>();
        var store = provider.GetRequiredService<AgentLocalStore>();
        var projection = provider.GetRequiredService<HistorySearchStore>();
        var abandonedGeneration = projection.BeginTextGeneration();
        var oldEpoch = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Assert.True(store.TryCommitRuntimeGeneration(
            new PackageRuntimeGeneration(oldEpoch, 1),
            GetCurrentProcessIdentity(),
            expectedCurrentEpoch: null,
            now,
            now.AddMinutes(5)));

        await startup.StartAsync();
        var candidate = new PackageRuntimeGeneration(Guid.NewGuid(), 2);
        await startup.CommitGenerationAsync(candidate).WaitAsync(TestTimeout);

        Assert.Equal(candidate.ActivationId, store.GetCurrentRuntimeGeneration()?.Epoch);
        Assert.Equal(candidate.ActivationId, store.OwnedRuntimeGeneration);
        Assert.Equal("Expired", ExecuteString(
            store.DatabasePath,
            $"SELECT Status FROM AgentRuntimeGenerations WHERE Epoch = '{oldEpoch}';"));
        Assert.Equal(0, ExecuteInt64(
            projection.DatabasePath,
            $"SELECT COUNT(*) FROM HistoryProjectionGenerations WHERE GenerationId = {abandonedGeneration};"));
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(provider.GetRequiredService<AgentBackgroundWorkService>().TryQueue(_ =>
        {
            completed.TrySetResult();
            return Task.CompletedTask;
        }));
        await completed.Task.WaitAsync(TestTimeout);
        await startup.StopAsync();
    }

    [Fact]
    public async Task CandidateFailureBeforeCommit_LeavesPriorGenerationAndWorkUntouched()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var provider = CreateProvider(scope.Context);
        var startup = provider.GetRequiredService<AgentRuntimeStartupService>();
        var store = provider.GetRequiredService<AgentLocalStore>();
        var projection = provider.GetRequiredService<HistorySearchStore>();
        var abandonedGeneration = projection.BeginTextGeneration();
        var oldEpoch = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Assert.True(store.TryCommitRuntimeGeneration(
            new PackageRuntimeGeneration(oldEpoch, 1),
            GetCurrentProcessIdentity(),
            expectedCurrentEpoch: null,
            now,
            now.AddMinutes(5)));

        await startup.StartAsync();
        await startup.StopAsync();

        var current = Assert.IsType<AgentRuntimeGenerationOwnership>(store.GetCurrentRuntimeGeneration());
        Assert.Null(store.OwnedRuntimeGeneration);
        Assert.Equal(oldEpoch, current.Epoch);
        Assert.Equal("Committed", current.Status);
        Assert.Equal(1, ExecuteInt64(
            projection.DatabasePath,
            $"SELECT COUNT(*) FROM HistoryProjectionGenerations WHERE GenerationId = {abandonedGeneration};"));
    }

    [Fact]
    public async Task FirstCommittedStartup_RecoversGenerationOwnedByDeadProcess()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var provider = CreateProvider(scope.Context);
        var startup = provider.GetRequiredService<AgentRuntimeStartupService>();
        var store = provider.GetRequiredService<AgentLocalStore>();
        var projection = provider.GetRequiredService<HistorySearchStore>();
        var abandonedGeneration = projection.BeginTextGeneration();
        var deadEpoch = Guid.NewGuid();
        var currentProcess = GetCurrentProcessIdentity();
        var now = DateTimeOffset.UtcNow;
        Assert.True(store.TryCommitRuntimeGeneration(
            new PackageRuntimeGeneration(deadEpoch, 1),
            currentProcess with { StartedAtUtc = currentProcess.StartedAtUtc.AddDays(-1) },
            expectedCurrentEpoch: null,
            now,
            now.AddMinutes(5)));
        var candidate = new PackageRuntimeGeneration(Guid.NewGuid(), 1);

        await startup.StartAsync();
        await startup.CommitGenerationAsync(candidate).WaitAsync(TestTimeout);

        Assert.Equal(candidate.ActivationId, store.GetCurrentRuntimeGeneration()?.Epoch);
        Assert.Equal(0, ExecuteInt64(
            projection.DatabasePath,
            $"SELECT COUNT(*) FROM HistoryProjectionGenerations WHERE GenerationId = {abandonedGeneration};"));
        Assert.Equal("Dead", ExecuteString(
            store.DatabasePath,
            $"SELECT Status FROM AgentRuntimeGenerations WHERE Epoch = '{deadEpoch}';"));
        await startup.StopAsync();
    }

    [Fact]
    public async Task CommittedStartup_TakesOverExpiredPriorEpoch()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var provider = CreateProvider(scope.Context);
        var startup = provider.GetRequiredService<AgentRuntimeStartupService>();
        var store = provider.GetRequiredService<AgentLocalStore>();
        var expiredEpoch = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Assert.True(store.TryCommitRuntimeGeneration(
            new PackageRuntimeGeneration(expiredEpoch, 1),
            GetCurrentProcessIdentity(),
            expectedCurrentEpoch: null,
            now,
            now.AddSeconds(-1)));
        var candidate = new PackageRuntimeGeneration(Guid.NewGuid(), 2);

        await startup.StartAsync();
        await startup.CommitGenerationAsync(candidate).WaitAsync(TestTimeout);

        Assert.Equal(candidate.ActivationId, store.GetCurrentRuntimeGeneration()?.Epoch);
        Assert.Equal("Expired", ExecuteString(
            store.DatabasePath,
            $"SELECT Status FROM AgentRuntimeGenerations WHERE Epoch = '{expiredEpoch}';"));
        await startup.StopAsync();
    }

    [Fact]
    public async Task HostTimeout_KeepsLeaseUntilOwnedWorkDrainsAndBlocksPeerGeneration()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var provider = CreateProvider(scope.Context);
        await using var replacementProvider = CreateProvider(scope.Context);
        var startup = provider.GetRequiredService<AgentRuntimeStartupService>();
        var background = provider.GetRequiredService<AgentBackgroundWorkService>();
        var store = provider.GetRequiredService<AgentLocalStore>();
        var generation = new PackageRuntimeGeneration(Guid.NewGuid(), 1);
        await startup.StartAsync();
        await startup.CommitGenerationAsync(generation);
        var workStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = background.RunOwnedAsync(async cancellationToken =>
        {
            using var registration = cancellationToken.Register(cancellationObserved.SetResult);
            workStarted.SetResult();
            await releaseWork.Task.ConfigureAwait(false);
            return true;
        });
        await workStarted.Task.WaitAsync(TestTimeout);

        using var callerCancellation = new CancellationTokenSource();
        var stop = startup.StopAsync(callerCancellation.Token);
        await cancellationObserved.Task.WaitAsync(TestTimeout);
        var hostWait = stop.WaitAsync(callerCancellation.Token);
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hostWait);

        Assert.False(stop.IsCompleted);
        Assert.Equal(generation.ActivationId, store.OwnedRuntimeGeneration);
        Assert.Equal("Committed", store.GetCurrentRuntimeGeneration()?.Status);
        var replacement = replacementProvider.GetRequiredService<AgentRuntimeStartupService>();
        var replacementGeneration = new PackageRuntimeGeneration(Guid.NewGuid(), 1);
        await replacement.StartAsync();
        var replacementCommit = replacement.CommitGenerationAsync(replacementGeneration);
        Assert.False(replacementCommit.IsCompleted);
        Assert.Equal(generation.ActivationId, store.GetCurrentRuntimeGeneration()?.Epoch);

        releaseWork.SetResult();
        await stop.WaitAsync(TestTimeout);
        Assert.True(await work);
        await replacementCommit.WaitAsync(TestTimeout);

        Assert.Equal(replacementGeneration.ActivationId, store.GetCurrentRuntimeGeneration()?.Epoch);
        Assert.Equal(replacementGeneration.ActivationId,
            replacementProvider.GetRequiredService<AgentLocalStore>().OwnedRuntimeGeneration);
        await replacement.StopAsync();
    }

    [Fact]
    public async Task HeartbeatSqliteFailures_RetryUntilSafetyDeadlineThenFaultAndDrain()
    {
        using var scope = RegressionTestPackageScope.Create();
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var delays = new ManualDelayQueue();
        var options = CreateFastLeaseOptions(clock, delays);
        await using var provider = CreateProvider(scope.Context, options);
        var startup = provider.GetRequiredService<AgentRuntimeStartupService>();
        var store = provider.GetRequiredService<AgentLocalStore>();
        var background = provider.GetRequiredService<AgentBackgroundWorkService>();
        var generation = new PackageRuntimeGeneration(Guid.NewGuid(), 1);
        await startup.StartAsync();
        await startup.CommitGenerationAsync(generation);
        using (var connection = Open(store.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $$"""
                CREATE TRIGGER RejectGenerationHeartbeat
                BEFORE UPDATE OF LastHeartbeatAtUtc ON AgentRuntimeGenerations
                WHEN OLD.Epoch = '{{generation.ActivationId}}'
                BEGIN
                    SELECT RAISE(ABORT, 'heartbeat storage unavailable');
                END;
                """;
            command.ExecuteNonQuery();
        }

        var completion = startup.GenerationCompletion;
        var delayCount = 0;
        while (!completion.IsCompleted)
        {
            var nextDelay = delays.NextAsync();
            var winner = await Task.WhenAny(nextDelay, completion).WaitAsync(TestTimeout);
            if (ReferenceEquals(winner, completion))
            {
                break;
            }

            var pending = await nextDelay;
            delayCount++;
            Assert.True(delayCount < 10, "Heartbeat retry exceeded its bounded lease-safe window.");
            clock.Advance(pending.Delay);
            pending.Release();
        }

        await Assert.ThrowsAsync<AgentRuntimeGenerationHeartbeatException>(async () => await completion);
        Assert.True(delayCount >= 3);
        await startup.StopAsync().WaitAsync(TestTimeout);
        Assert.False(background.IsRunning);
        Assert.Null(store.OwnedRuntimeGeneration);
        Assert.Equal("Stopped", store.GetCurrentRuntimeGeneration()?.Status);
    }

    [Fact]
    public async Task HeartbeatTransientSqliteFailure_RetriesAndRenewsBeforeDeadline()
    {
        using var scope = RegressionTestPackageScope.Create();
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var delays = new ManualDelayQueue();
        await using var provider = CreateProvider(
            scope.Context,
            CreateFastLeaseOptions(clock, delays));
        var startup = provider.GetRequiredService<AgentRuntimeStartupService>();
        var store = provider.GetRequiredService<AgentLocalStore>();
        var generation = new PackageRuntimeGeneration(Guid.NewGuid(), 1);
        await startup.StartAsync();
        await startup.CommitGenerationAsync(generation);
        var initialExpiration = Assert.IsType<AgentRuntimeGenerationOwnership>(
            store.GetCurrentRuntimeGeneration()).LeaseExpiresAtUtc;
        using (var connection = Open(store.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $$"""
                CREATE TRIGGER RejectGenerationHeartbeat
                BEFORE UPDATE OF LastHeartbeatAtUtc ON AgentRuntimeGenerations
                WHEN OLD.Epoch = '{{generation.ActivationId}}'
                BEGIN
                    SELECT RAISE(ABORT, 'transient heartbeat storage failure');
                END;
                """;
            command.ExecuteNonQuery();
        }

        var heartbeat = await delays.NextAsync().WaitAsync(TestTimeout);
        clock.Advance(heartbeat.Delay);
        heartbeat.Release();
        var retry = await delays.NextAsync().WaitAsync(TestTimeout);
        using (var connection = Open(store.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER RejectGenerationHeartbeat;";
            command.ExecuteNonQuery();
        }
        clock.Advance(retry.Delay);
        retry.Release();
        _ = await delays.NextAsync().WaitAsync(TestTimeout);

        Assert.False(startup.GenerationCompletion.IsCompleted);
        Assert.True(store.GetCurrentRuntimeGeneration()?.LeaseExpiresAtUtc > initialExpiration);
        await startup.StopAsync().WaitAsync(TestTimeout);
        Assert.Equal("Stopped", store.GetCurrentRuntimeGeneration()?.Status);
    }

    [Fact]
    public async Task HeartbeatWriteLock_FencesAllWorkersBeforeExpiryAndRejectsLateRenewal()
    {
        using var scope = RegressionTestPackageScope.Create();
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var heartbeatDelays = new ManualDelayQueue();
        var deadlineDelays = new ManualDelayQueue();
        var replacementDelays = new ManualDelayQueue();
        var renewalCommandStarting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = CreateProvider(
            scope.Context,
            CreateWriteLockLeaseOptions(
                clock,
                heartbeatDelays,
                deadlineDelays,
                () => renewalCommandStarting.TrySetResult()));
        await using var replacementProvider = CreateProvider(
            scope.Context,
            CreateWriteLockLeaseOptions(clock, replacementDelays));
        var startup = provider.GetRequiredService<AgentRuntimeStartupService>();
        var store = provider.GetRequiredService<AgentLocalStore>();
        var generation = new PackageRuntimeGeneration(Guid.NewGuid(), 1);
        await startup.StartAsync();
        await startup.CommitGenerationAsync(generation);
        var replacement = replacementProvider.GetRequiredService<AgentRuntimeStartupService>();
        var replacementGeneration = new PackageRuntimeGeneration(Guid.NewGuid(), 1);
        await replacement.StartAsync();
        var initialOwnership = Assert.IsType<AgentRuntimeGenerationOwnership>(
            store.GetCurrentRuntimeGeneration());
        var initialHeartbeat = ExecuteString(
            store.DatabasePath,
            $"SELECT LastHeartbeatAtUtc FROM AgentRuntimeGenerations WHERE Epoch = '{generation.ActivationId}';");
        var workerLifetimes = CaptureWorkerLifetimes(provider);
        PendingDelay replacementPoll;

        using (var lockConnection = Open(store.DatabasePath))
        using (var writeLock = lockConnection.BeginTransaction(deferred: false))
        {
            var heartbeat = await heartbeatDelays.NextAsync().WaitAsync(TestTimeout);
            clock.Advance(heartbeat.Delay);
            heartbeat.Release();
            await renewalCommandStarting.Task.WaitAsync(TestTimeout);

            var safetyDeadline = await deadlineDelays.NextAsync().WaitAsync(TestTimeout);
            clock.Advance(safetyDeadline.Delay);
            safetyDeadline.Release();

            await Assert.ThrowsAsync<AgentRuntimeGenerationHeartbeatException>(
                async () => await startup.GenerationCompletion);
            try
            {
                await WaitUntilAsync(() => workerLifetimes.All(lifetime => lifetime.IsCancellationRequested));
            }
            catch (TimeoutException)
            {
                var workerNames = new[] { "History", "Run", "Lifecycle", "Cleanup", "Background" };
                Assert.Fail($"Workers were not cancellation-fenced: {string.Join(", ", workerLifetimes
                    .Select((lifetime, index) => (lifetime, index))
                    .Where(item => !item.lifetime.IsCancellationRequested)
                    .Select(item => workerNames[item.index]))}");
            }
            Assert.True(store.IsRuntimeGenerationFenced(generation.ActivationId));
            Assert.True(clock.GetUtcNow() < initialOwnership.LeaseExpiresAtUtc);

            var replacementCommit = replacement.CommitGenerationAsync(replacementGeneration);
            replacementPoll = await replacementDelays.NextAsync().WaitAsync(TestTimeout);
            Assert.False(replacementCommit.IsCompleted);
            Assert.Equal(generation.ActivationId, store.GetCurrentRuntimeGeneration()?.Epoch);

            writeLock.Rollback();
            var untilExpiry = initialOwnership.LeaseExpiresAtUtc - clock.GetUtcNow();
            clock.Advance(untilExpiry + TimeSpan.FromMilliseconds(1));
            replacementPoll.Release();
            await replacementCommit.WaitAsync(TestTimeout);
            await startup.StopAsync().WaitAsync(TestTimeout);

            Assert.Equal(replacementGeneration.ActivationId, store.GetCurrentRuntimeGeneration()?.Epoch);
            Assert.Equal(initialHeartbeat, ExecuteString(
                store.DatabasePath,
                $"SELECT LastHeartbeatAtUtc FROM AgentRuntimeGenerations WHERE Epoch = '{generation.ActivationId}';"));
            Assert.Contains(
                ExecuteString(
                    store.DatabasePath,
                    $"SELECT Status FROM AgentRuntimeGenerations WHERE Epoch = '{generation.ActivationId}';"),
                new[] { "Expired", "Stopped" });
            await replacement.StopAsync();
        }
    }

    [Fact]
    public async Task Shutdown_FansOutWorkerCancellationBeforeBlockedHistoryDrain()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var provider = CreateProvider(scope.Context);
        var startup = provider.GetRequiredService<AgentRuntimeStartupService>();
        var generation = new PackageRuntimeGeneration(Guid.NewGuid(), 1);
        await startup.StartAsync();
        await startup.CommitGenerationAsync(generation);
        var workerLifetimes = CaptureWorkerLifetimes(provider);
        var releaseHistoryDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetPrivateField(
            provider.GetRequiredService<HistorySearchIndexingService>(),
            "_worker",
            releaseHistoryDrain.Task);

        var stop = startup.StopAsync();

        await WaitUntilAsync(() => workerLifetimes.All(lifetime => lifetime.IsCancellationRequested));
        Assert.False(stop.IsCompleted);
        Assert.Equal(generation.ActivationId,
            provider.GetRequiredService<AgentLocalStore>().OwnedRuntimeGeneration);

        releaseHistoryDrain.SetResult();
        await stop.WaitAsync(TestTimeout);
        Assert.Null(provider.GetRequiredService<AgentLocalStore>().OwnedRuntimeGeneration);
    }

    [Fact]
    public async Task StolenEpoch_FencesAdmissionFaultsCompletionAndPreservesReplacementOwner()
    {
        using var scope = RegressionTestPackageScope.Create();
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var delays = new ManualDelayQueue();
        await using var provider = CreateProvider(scope.Context, CreateFastLeaseOptions(clock, delays));
        var startup = provider.GetRequiredService<AgentRuntimeStartupService>();
        var store = provider.GetRequiredService<AgentLocalStore>();
        var sessions = provider.GetRequiredService<AgentSessionService>();
        var workspace = provider.GetRequiredService<AgentWorkspaceService>().CreateWorkspace("Epoch fence");
        var session = sessions.CreateSession("Epoch fence", workspaceId: workspace.WorkspaceId);
        var generation = new PackageRuntimeGeneration(Guid.NewGuid(), 1);
        await startup.StartAsync();
        await startup.CommitGenerationAsync(generation);
        var replacementEpoch = Guid.NewGuid();
        StealGeneration(store.DatabasePath, generation.ActivationId, replacementEpoch, clock.GetUtcNow());
        var userTurnId = Guid.NewGuid();
        var fingerprint = AgentUserTurnRequestFingerprint.Compute(
            session.SessionId,
            "profile.test",
            workspace.WorkspaceId,
            "must be fenced",
            AgentRunAdmissionKind.Normal,
            rollbackAnchorTurnId: null,
            []);
        var request = new AgentUserTurnAdmissionRequest(
            userTurnId,
            session.SessionId,
            "profile.test",
            workspace.WorkspaceId,
            "must be fenced",
            AgentRunAdmissionKind.Normal,
            RollbackAnchorTurnId: null,
            fingerprint,
            []);

        Assert.Throws<AgentRuntimeGenerationOwnershipLostException>(() => store.AdmitUserTurn(request));
        Assert.Null(store.GetRunByUserTurnId(userTurnId));

        var heartbeat = await delays.NextAsync().WaitAsync(TestTimeout);
        clock.Advance(heartbeat.Delay);
        heartbeat.Release();
        await Assert.ThrowsAsync<AgentRuntimeGenerationOwnershipLostException>(
            async () => await startup.GenerationCompletion);
        await startup.StopAsync().WaitAsync(TestTimeout);

        var current = Assert.IsType<AgentRuntimeGenerationOwnership>(store.GetCurrentRuntimeGeneration());
        Assert.Equal(replacementEpoch, current.Epoch);
        Assert.Equal("Committed", current.Status);
        Assert.Null(store.OwnedRuntimeGeneration);
        Assert.Equal("Expired", ExecuteString(
            store.DatabasePath,
            $"SELECT Status FROM AgentRuntimeGenerations WHERE Epoch = '{generation.ActivationId}';"));
    }

    [Fact]
    public async Task StolenEpoch_FencesPausedHistoryActivationAndPreservesReplacementProjectionOwner()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var originalProvider = CreateProvider(scope.Context);
        var originalStartup = originalProvider.GetRequiredService<AgentRuntimeStartupService>();
        var originalStore = originalProvider.GetRequiredService<AgentLocalStore>();
        var originalProjection = originalProvider.GetRequiredService<HistorySearchStore>();
        var originalIndexer = originalProvider.GetRequiredService<HistorySearchIndexingService>();
        var originalEpoch = Guid.NewGuid();
        await originalStartup.StartAsync();
        await originalStartup.CommitGenerationAsync(new PackageRuntimeGeneration(originalEpoch, 1));
        var workspace = originalProvider.GetRequiredService<AgentWorkspaceService>()
            .CreateWorkspace("History epoch fence");
        var sessions = originalProvider.GetRequiredService<AgentSessionService>();
        var session = sessions.CreateSession("History epoch fence", workspaceId: workspace.WorkspaceId);
        sessions.AppendTextTurn(session.SessionId, AgentMessageRole.User, "owned history baseline");
        await WaitUntilAsync(() => originalProjection.GetSnapshot().DocumentCount == 1);
        var activeBeforeTakeover = originalProjection.GetSnapshot().ActiveTextGenerationId;
        await using var replacementProvider = CreateProvider(scope.Context);
        var replacementStartup = replacementProvider.GetRequiredService<AgentRuntimeStartupService>();
        var activationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseActivation = new ManualResetEventSlim();
        originalProjection.BeforeMutation = mutation =>
        {
            if (mutation == "activate-text-generation")
            {
                activationEntered.TrySetResult();
                Assert.True(releaseActivation.Wait(TimeSpan.FromSeconds(10)));
            }
        };
        originalIndexer.RequestRebuild();
        await activationEntered.Task.WaitAsync(TestTimeout);
        var staleStagingGeneration = ExecuteInt64(
            originalProjection.DatabasePath,
            "SELECT GenerationId FROM HistoryProjectionGenerations WHERE State = 'Staging' ORDER BY GenerationId DESC LIMIT 1;");
        var replacementEpoch = Guid.NewGuid();
        StealGeneration(originalStore.DatabasePath, originalEpoch, replacementEpoch, DateTimeOffset.UtcNow);

        await replacementStartup.StartAsync();
        var replacementCommit = Task.Run(() => replacementStartup.CommitGenerationAsync(
            new PackageRuntimeGeneration(replacementEpoch, 2)));
        await Task.Delay(100);
        Assert.False(replacementCommit.IsCompleted);

        releaseActivation.Set();
        originalProjection.BeforeMutation = null;
        await replacementCommit.WaitAsync(TimeSpan.FromSeconds(10));
        var replacementProjection = replacementProvider.GetRequiredService<HistorySearchStore>();
        var replacementState = replacementProvider.GetRequiredService<HistorySearchRuntimeState>();
        await WaitUntilAsync(() => replacementProjection.GetSnapshot().ActiveTextGenerationId is not null
                                   || replacementState.Current.FailureCode is not null);
        Assert.True(
            replacementProjection.GetSnapshot().ActiveTextGenerationId is not null,
            $"Replacement History startup failed: {replacementState.Current.FailureCode} {replacementState.Current.FailureMessage}");

        Assert.Equal(replacementEpoch, replacementProjection.GetSnapshot().RuntimeEpoch);
        Assert.Equal(replacementEpoch, replacementProvider
            .GetRequiredService<HistorySearchIndexingService>().RuntimeEpoch);
        Assert.Equal(
            replacementEpoch,
            Assert.IsType<HistoryProjectionGeneration>(replacementProjection.GetActiveTextGeneration()).RuntimeEpoch);
        Assert.Equal(activeBeforeTakeover, replacementProjection.GetSnapshot().ActiveTextGenerationId);
        Assert.Equal(0, ExecuteInt64(
            replacementProjection.DatabasePath,
            $"SELECT COUNT(*) FROM HistoryProjectionGenerations WHERE GenerationId = {staleStagingGeneration};"));

        await replacementStartup.StopAsync();
        await originalStartup.StopAsync();
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static long ExecuteInt64(string databasePath, string sql)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string ExecuteString(string databasePath, string sql)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
    }

    private static ServiceProvider CreateProvider(
        IPackageContext context,
        AgentRuntimeGenerationOptions? generationOptions = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(context);
        services.AddSingleton<Sunder.Package.Agent.Protocol.AgentRpcCatalog>(new RegressionTestExtensionCatalog());
        new PackageModule().ConfigureRuntimeServices(services, context);
        if (generationOptions is not null)
        {
            services.AddSingleton(generationOptions);
        }
        return services.BuildServiceProvider();
    }

    private static AgentRuntimeGenerationOptions CreateFastLeaseOptions(
        TimeProvider timeProvider,
        ManualDelayQueue delays,
        Action? renewalCommandStarting = null)
    {
        var deadlineDelays = new ManualDelayQueue();
        return new AgentRuntimeGenerationOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(10),
            HeartbeatInterval = TimeSpan.FromSeconds(2),
            ClaimPollInterval = TimeSpan.FromMilliseconds(10),
            RetryInitialDelay = TimeSpan.FromSeconds(1),
            RetryMaximumDelay = TimeSpan.FromSeconds(2),
            LeaseSafetyMargin = TimeSpan.FromSeconds(2),
            TimeProvider = timeProvider,
            DelayAsync = delays.DelayAsync,
            DeadlineDelayAsync = deadlineDelays.DelayAsync,
            RenewalCommandStarting = renewalCommandStarting,
        };
    }

    private static AgentRuntimeGenerationOptions CreateWriteLockLeaseOptions(
        TimeProvider timeProvider,
        ManualDelayQueue delays,
        ManualDelayQueue? deadlineDelays = null,
        Action? renewalCommandStarting = null)
    {
        deadlineDelays ??= new ManualDelayQueue();
        return new AgentRuntimeGenerationOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(20),
            HeartbeatInterval = TimeSpan.FromSeconds(2),
            ClaimPollInterval = TimeSpan.FromMilliseconds(10),
            RetryInitialDelay = TimeSpan.FromSeconds(1),
            RetryMaximumDelay = TimeSpan.FromSeconds(2),
            LeaseSafetyMargin = TimeSpan.FromSeconds(5),
            RenewalCommandTimeout = TimeSpan.FromSeconds(4),
            TimeProvider = timeProvider,
            DelayAsync = delays.DelayAsync,
            DeadlineDelayAsync = deadlineDelays.DelayAsync,
            RenewalCommandStarting = renewalCommandStarting,
        };
    }

    private static void StealGeneration(
        string databasePath,
        Guid previousEpoch,
        Guid replacementEpoch,
        DateTimeOffset now)
    {
        using var connection = Open(databasePath);
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var expire = connection.CreateCommand())
        {
            expire.Transaction = transaction;
            expire.CommandText = "UPDATE AgentRuntimeGenerations SET Status = 'Expired', StoppedAtUtc = $now WHERE Epoch = $epoch;";
            expire.Parameters.AddWithValue("$now", now.ToString("O"));
            expire.Parameters.AddWithValue("$epoch", previousEpoch.ToString());
            Assert.Equal(1, expire.ExecuteNonQuery());
        }
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO AgentRuntimeGenerations (
                    Epoch, RuntimeSessionGeneration, ProcessId, ProcessStartedAtUtc,
                    Status, CreatedAtUtc, CommittedAtUtc, LastHeartbeatAtUtc,
                    LeaseExpiresAtUtc, StoppedAtUtc)
                VALUES ($epoch, 2, 1, $now, 'Committed', $now, $now, $now, $expires, NULL);
                """;
            insert.Parameters.AddWithValue("$epoch", replacementEpoch.ToString());
            insert.Parameters.AddWithValue("$now", now.ToString("O"));
            insert.Parameters.AddWithValue("$expires", now.AddMinutes(1).ToString("O"));
            insert.ExecuteNonQuery();
        }
        using (var publish = connection.CreateCommand())
        {
            publish.Transaction = transaction;
            publish.CommandText = "UPDATE AgentRuntimeGenerationState SET CurrentEpoch = $epoch WHERE SingletonId = 1;";
            publish.Parameters.AddWithValue("$epoch", replacementEpoch.ToString());
            Assert.Equal(1, publish.ExecuteNonQuery());
        }
        transaction.Commit();
    }

    private static AgentRuntimeProcessIdentity GetCurrentProcessIdentity()
    {
        using var process = Process.GetCurrentProcess();
        return new AgentRuntimeProcessIdentity(
            process.Id,
            new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TestTimeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for startup retry state.");
            }
            await Task.Delay(20);
        }
    }

    private static CancellationTokenSource[] CaptureWorkerLifetimes(IServiceProvider provider)
        =>
        [
            GetPrivateField<CancellationTokenSource>(
                provider.GetRequiredService<HistorySearchIndexingService>(),
                "_lifetime"),
            GetPrivateField<CancellationTokenSource>(
                provider.GetRequiredService<AgentRunDispatcher>(),
                "_lifetime"),
            GetPrivateField<CancellationTokenSource>(
                provider.GetRequiredService<AgentLifecycleDispatcher>(),
                "_lifetime"),
            GetPrivateField<CancellationTokenSource>(
                provider.GetRequiredService<AgentSessionCleanupDispatcher>(),
                "_lifetime"),
            GetPrivateField<CancellationTokenSource>(
                provider.GetRequiredService<AgentBackgroundWorkService>(),
                "_lifetime"),
        ];

    private static T GetPrivateField<T>(object instance, string name)
        => (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
               ?? throw new InvalidOperationException($"Field '{name}' was not found on '{instance.GetType().Name}'."));

    private static void SetPrivateField<T>(object instance, string name, T value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException($"Field '{name}' was not found on '{instance.GetType().Name}'.");
        field.SetValue(instance, value);
    }

    private static void FaultGenerationCompletion(
        AgentRuntimeStartupService startup,
        Exception exception)
    {
        var completion = GetPrivateField<TaskCompletionSource>(startup, "_generationCompletion");
        Assert.True(completion.TrySetException(exception));
    }

    private sealed class NoOpDurableObserver : IAgentDurableLifecycleObserver
    {
        public string ObserverId => "test.startup-observer";

        public string DisplayName => "Startup observer";

        public ValueTask HandleDurableLifecycleEventAsync(
            AgentDurableLifecycleEventEnvelope lifecycleEvent,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private sealed class NoOpRpcHandler : ISunderRpcServiceHandler, ISunderRpcDeclaredErrorMetadata
    {
        internal static NoOpRpcHandler Instance { get; } = new();

        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> DeclaredErrors
            => AgentRpcServiceHandler.CreateDeclaredErrorMap(DeferredTestDescriptor);

        public ValueTask<JsonElement> InvokeUnaryAsync(
            SunderRpcInvocationContext context,
            string serviceId,
            string methodId,
            JsonElement request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(default(JsonElement));

        public async IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(
            SunderRpcInvocationContext context,
            string serviceId,
            string methodId,
            JsonElement request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class BlockingActivationRpcClient : ISunderRpcClient
    {
        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
            SunderRpcEndpointReference endpoint,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<SunderRpcProviderSnapshot?>(null);

        public ValueTask<bool> TryReportInvariantViolationAsync(
            SunderRpcEndpointReference endpoint,
            Exception exception,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public async ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
            string contractId,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }

        public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
            long afterRevision,
            long afterSequence,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask<JsonElement> InvokeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<JsonElement>(new NotSupportedException());

        public async IAsyncEnumerable<JsonElement> SubscribeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class BlockingSessionDataCleaner : IAgentSessionDataCleaner
    {
        private readonly TaskCompletionSource _descriptorReadStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseDescriptor = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string CleanerId
        {
            get
            {
                _descriptorReadStarted.TrySetResult();
                _releaseDescriptor.Task.GetAwaiter().GetResult();
                return "test.blocking-cleaner";
            }
        }

        internal Task DescriptorReadStarted => _descriptorReadStarted.Task;

        public void DeleteSessionData(Guid sessionId)
        {
        }

        internal void ReleaseDescriptor() => _releaseDescriptor.TrySetResult();
    }

    private sealed class BlockingLifecycleObserver : IAgentDurableLifecycleObserver
    {
        private readonly TaskCompletionSource _descriptorReadStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseDescriptor = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string ObserverId
        {
            get
            {
                _descriptorReadStarted.TrySetResult();
                _releaseDescriptor.Task.GetAwaiter().GetResult();
                return "test.blocking-observer";
            }
        }

        public string DisplayName => "Blocking startup observer";

        internal Task DescriptorReadStarted => _descriptorReadStarted.Task;

        public ValueTask HandleDurableLifecycleEventAsync(
            AgentDurableLifecycleEventEnvelope lifecycleEvent,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        internal void ReleaseDescriptor() => _releaseDescriptor.TrySetResult();
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private long _utcTicks = now.UtcTicks;

        public override DateTimeOffset GetUtcNow()
            => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        internal void Advance(TimeSpan duration)
            => Interlocked.Add(ref _utcTicks, duration.Ticks);
    }

    private sealed class ManualDelayQueue
    {
        private readonly Channel<PendingDelay> _delays = Channel.CreateUnbounded<PendingDelay>();

        internal Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var pending = new PendingDelay(delay, cancellationToken);
            if (!_delays.Writer.TryWrite(pending))
            {
                throw new InvalidOperationException("Manual delay queue rejected a delay.");
            }
            return pending.Completion;
        }

        internal async Task<PendingDelay> NextAsync()
        {
            while (true)
            {
                var pending = await _delays.Reader.ReadAsync();
                if (!pending.Completion.IsCanceled)
                {
                    return pending;
                }
            }
        }
    }

    private sealed class PendingDelay
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;

        internal PendingDelay(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delay = delay;
            _registration = cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken));
            _ = DisposeRegistrationAsync();
        }

        internal TimeSpan Delay { get; }

        internal Task Completion => _completion.Task;

        internal void Release() => _completion.TrySetResult();

        private async Task DisposeRegistrationAsync()
        {
            try
            {
                await _completion.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _registration.Dispose();
            }
        }
    }
}
