extern alias BuilderApp;

using System.Text.Json;
using BuilderApp::Sunder.Package.Agent.Builder;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;
using BuilderServices = BuilderApp::Sunder.Generated.Packages.Younics_Sunder_Package_Maker_Services;

namespace Sunder.Package.Agent.Tests;

public sealed partial class BuilderServiceTests
{
    [Fact]
    public async Task BackendAdapterRejectsInconsistentContentLength()
    {
        var backend = new BuilderBackendGateway(new BuilderRuntimeCatalog(new InconsistentContentRuntime()));
        await Assert.ThrowsAsync<InvalidDataException>(() => backend.DiscoverAsync(Path.GetTempPath()));
    }

    [Fact]
    public async Task UncertainStartReadsExactJobWithoutReplayingItsMutation()
    {
        await using var fixture = new BuilderUiFixture(new() { LoseFirstStartResponse = true });
        var result = await fixture.Application.RunJobAsync(new() { Operation = "ensure-sdk", Kind = BuilderProjectKind.Node });
        var starts = fixture.Backend.Starts.ToArray();
        Assert.Single(starts);
        Assert.False(string.IsNullOrEmpty(starts[0].Id));
        Assert.Single(fixture.Backend.Jobs);
        Assert.Equal("succeeded", result.State);
    }

    [Fact]
    public async Task CancellationCompletedDuringBackendLookupDoesNotAttemptStartOrCreateUnconfirmedOwnership()
    {
        await using var fixture = new BuilderUiFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Backend.BeforeGetBackendId = _ =>
        {
            cancellation.Cancel();
            // A completed lookup task can win WaitAsync despite an already
            // cancelled token. The mutation boundary must recheck cancellation.
            return Task.CompletedTask;
        };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Application.RunJobAsync(
            new() { Operation = "build", Kind = BuilderProjectKind.Dotnet }, cancellationToken: cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(fixture.Backend.Starts);
        fixture.Backend.BeforeGetBackendId = null;
        var next = await fixture.Application.RunJobAsync(new() { Operation = "ensure-sdk", Kind = BuilderProjectKind.Dotnet });
        Assert.Equal("succeeded", next.State);
        Assert.Single(fixture.Backend.Starts);
    }

    [Fact]
    public async Task BackendIdentityLookupCanReconnectBeforeTheOnlyStart()
    {
        await using var fixture = new BuilderUiFixture();
        var reads = 0;
        fixture.Backend.BeforeGetBackendId = _ =>
        {
            Assert.Empty(fixture.Backend.Starts);
            return Interlocked.Increment(ref reads) <= 4
                ? Task.FromException(new SunderRpcException(new(SunderRpcErrorKind.StaleEndpoint, "rpc.caller-retired", "Caller handoff is pending.")))
                : Task.CompletedTask;
        };
        var result = await fixture.Application.RunJobAsync(new() { Operation = "ensure-sdk", Kind = BuilderProjectKind.Dotnet });
        Assert.Equal("succeeded", result.State);
        Assert.Equal(5, reads);
        Assert.Single(fixture.Backend.Starts);
    }

    [Fact]
    public async Task CallerHandoffReconcilesAcceptedFiniteBuildWithoutCancellingOrReplayingStart()
    {
        await using var fixture = new BuilderUiFixture(new() { HoldBuilds = true });
        var reads = 0;
        var cancels = 0;
        List<BuilderJobSnapshot> observations = [];
        fixture.Backend.BeforeCancel = (_, _) => { Interlocked.Increment(ref cancels); return Task.CompletedTask; };
        fixture.Backend.BeforeGet = (request, _) =>
        {
            var read = Interlocked.Increment(ref reads);
            if (read is > 1 and <= 5)
                throw new SunderRpcException(new(SunderRpcErrorKind.StaleEndpoint, "rpc.caller-retired", "Caller handoff is pending."));
            if (read > 5) fixture.Backend.Jobs[request.Id!] = fixture.Backend.Jobs[request.Id!] with { State = "succeeded" };
            return Task.CompletedTask;
        };
        try
        {
            var result = await fixture.Application.RunJobAsync(new() { Operation = "build", Kind = BuilderProjectKind.Dotnet },
                snapshot => { observations.Add(snapshot); return Task.CompletedTask; })
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("succeeded", result.State);
            Assert.Equal(6, reads);
            Assert.Equal(0, cancels);
            Assert.Single(fixture.Backend.Starts);
            var reconnecting = Assert.Single(observations, value => value.ConnectionState == BuilderJobConnectionState.Reconnecting);
            Assert.Equal("running", reconnecting.State);
            Assert.Equal(result.BackendId, reconnecting.BackendId);
            Assert.Equal(BuilderJobConnectionState.Connected, observations.Last().ConnectionState);
        }
        finally
        {
            fixture.Backend.BeforeGet = null;
            if (fixture.Backend.Starts.TryPeek(out var request)) await fixture.Application.StopJobAsync(request.Id!);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackendReplacementCannotReplayOrAcknowledgeOldJobAndKeepsProjectExcluded(bool nestedOtherTechnology)
    {
        await using var fixture = new BuilderUiFixture(new() { HoldBuilds = true });
        var original = fixture.Backend.BackendId;
        var reads = 0;
        var cancels = 0;
        var project = BuilderUiFixture.Project("backend-replaced");
        fixture.Backend.BeforeCancel = (_, _) => { Interlocked.Increment(ref cancels); return Task.CompletedTask; };
        try
        {
            await Assert.ThrowsAsync<BuilderJobCleanupException>(() => fixture.Application.RunJobAsync(new()
            {
                Operation = "build", Kind = project.Kind, ProjectFolder = project.ProjectFolder,
            }, _ =>
            {
                if (Interlocked.Increment(ref reads) == 1) fixture.Backend.BackendId = new('b', 32);
                return Task.CompletedTask;
            }));
            var request = Assert.Single(fixture.Backend.Starts);
            Assert.Equal(original, request.BackendId);
            Assert.Equal("running", fixture.Backend.Jobs[request.Id!].State);
            await Assert.ThrowsAsync<IOException>(() => fixture.Application.RunJobAsync(new()
            {
                Operation = "build", Kind = nestedOtherTechnology ? BuilderProjectKind.Node : project.Kind,
                ProjectFolder = nestedOtherTechnology ? Path.Combine(project.ProjectFolder, "nested") : project.ProjectFolder,
            }));
            Assert.Single(fixture.Backend.Starts);
            Assert.Equal(0, cancels); // The replacement rejected before job mutation.
        }
        finally
        {
            fixture.Backend.BackendId = original;
            if (fixture.Backend.Starts.TryPeek(out var request)) await fixture.Application.StopJobAsync(request.Id!);
        }
    }

    [Fact]
    public async Task ExplicitStartRejectionOnBackendReplacementDoesNotInventUnconfirmedOwnership()
    {
        await using var fixture = new BuilderUiFixture();
        fixture.Backend.OnStart = _ => fixture.Backend.BackendId = new('b', 32);
        var failure = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Application.RunJobAsync(new()
        {
            Operation = "build", Kind = BuilderProjectKind.Dotnet,
        }));
        Assert.Equal("agent.builder.backend-changed", failure.Error.Code);
        Assert.Empty(fixture.Backend.Jobs);
        fixture.Backend.OnStart = null;
        var result = await fixture.Application.RunJobAsync(new() { Operation = "ensure-sdk", Kind = BuilderProjectKind.Dotnet });
        Assert.Equal("succeeded", result.State);
        Assert.Equal(fixture.Backend.BackendId, result.BackendId);
    }

    [Fact]
    public async Task ApplicationCannotReuseACompletedJobIdentityOnAnotherBackend()
    {
        await using var fixture = new BuilderUiFixture();
        var request = new BuilderJobRequest { Id = Guid.NewGuid().ToString("N"), Operation = "ensure-sdk", Kind = BuilderProjectKind.Dotnet };
        Assert.Equal("succeeded", (await fixture.Application.RunJobAsync(request)).State);
        fixture.Backend.BackendId = new('b', 32);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Application.RunJobAsync(request));
        Assert.Single(fixture.Backend.Starts);
        Assert.Equal("succeeded", (await fixture.Application.StopJobAsync(request.Id)).TerminalSnapshot?.State); // Previously proven, not a new-backend acknowledgement.
    }

    [Theory]
    [InlineData(SunderRpcErrorKind.PermissionDenied)]
    [InlineData(SunderRpcErrorKind.Protocol)]
    public async Task JobReadDoesNotRetryOrHideSecurityAndProtocolFailures(SunderRpcErrorKind kind)
    {
        await using var fixture = new BuilderUiFixture(new() { HoldBuilds = true });
        var reads = 0;
        var failure = new SunderRpcException(new(kind, "test.denied", "This is not a caller reconnect."));
        fixture.Backend.BeforeGet = (_, _) => Interlocked.Increment(ref reads) == 1 ? Task.FromException(failure) : Task.CompletedTask;
        var actual = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Application.RunJobAsync(new()
        {
            Operation = "build", Kind = BuilderProjectKind.Dotnet,
        }));
        Assert.Same(failure, actual);
        Assert.Equal(2, reads); // One rejected read, then exact stop acknowledgement, no read retry.
        Assert.Equal("cancelled", Assert.Single(fixture.Backend.Jobs.Values).State);
        Assert.Single(fixture.Backend.Starts);
    }

    [Fact]
    public async Task CancellationUsesIndependentCleanupTokenAndReturnsPartialSourceSnapshot()
    {
        await using var fixture = new BuilderUiFixture();
        var started = BuilderUiFixture.Signal();
        var polls = 0;
        fixture.Backend.Result = request => fixture.Backend.DefaultResult(request) with { State = "running" };
        fixture.Backend.BeforeGet = async (_, token) =>
        {
            if (Interlocked.Increment(ref polls) == 1)
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        using var cancellation = new CancellationTokenSource();
        var job = fixture.Application.RunJobAsync(new()
        {
            Operation = "create", Kind = BuilderProjectKind.Dotnet, ProjectFolder = Path.Combine(Path.GetTempPath(), "partial"),
            DisplayName = "Partial", PackageId = "local.partial",
        }, cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        var result = await job.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(fixture.Backend.CleanupUsedIndependentToken);
        Assert.Equal("cancelled", result.State);
        Assert.Equal("local.partial", result.Project?.PackageId);
        Assert.True(polls > 1);
    }

    [Fact]
    public async Task UnconfirmedShutdownPreservesPartialSourceButRequiresReconciliationBeforeMutation()
    {
        await using var fixture = new BuilderUiFixture(new() { HoldBuilds = true });
        var source = BuilderUiFixture.Project("uncertain");
        fixture.Backend.Projects[source.EntryPoint] = source;
        var observed = BuilderUiFixture.Signal();
        var polls = 0;
        fixture.Backend.BeforeGet = (_, _) => Interlocked.Increment(ref polls) > 1
            ? Task.FromException(new IOException("Transport disconnected")) : Task.CompletedTask;
        using var cancellation = new CancellationTokenSource();
        BuilderJobSnapshot? partial = null;
        var running = fixture.Application.RunJobAsync(new()
        {
            Operation = "build", Kind = source.Kind, EntryPoint = source.EntryPoint, ProjectFolder = source.ProjectFolder,
        }, snapshot => { partial = snapshot; observed.TrySetResult(); return Task.CompletedTask; }, cancellation.Token);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAsync<BuilderJobCleanupException>(() => running);
        Assert.Equal(source, partial?.Project);
        Assert.Equal("running", partial?.State); // Missing stop proof is not a backend failure outcome.
        Assert.Equal(BuilderJobConnectionState.StopUnconfirmed, partial?.ConnectionState);
        await Assert.ThrowsAsync<IOException>(() => fixture.Application.RunJobAsync(new()
        {
            Operation = "build", Kind = source.Kind, EntryPoint = source.EntryPoint, ProjectFolder = source.ProjectFolder,
        }));
        Assert.Single(fixture.Backend.Starts);
        fixture.Backend.BeforeGet = null;
        var reconciled = await fixture.Application.StopJobAsync(fixture.Backend.Starts.Single().Id!);
        Assert.Equal(BuilderJobStopOutcome.KnownTerminal, reconciled.Outcome);
        Assert.Equal("cancelled", reconciled.TerminalSnapshot?.State);
    }

    [Fact]
    public async Task FiniteOperationQueueIsMutuallyExclusive()
    {
        var queue = new BuilderOperationQueue(new BuilderUiBackgroundQueue());
        var entered = BuilderUiFixture.Signal();
        var release = BuilderUiFixture.Signal();
        var secondEntered = false;
        var first = queue.RunAsync(async () => { entered.TrySetResult(); await release.Task; });
        await entered.Task;
        var second = queue.RunAsync(() => { secondEntered = true; return Task.CompletedTask; });
        Assert.False(secondEntered);
        release.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.True(secondEntered);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QueuedCancellationBeforeEnqueueReturnsCompletesAndSealsStaleDelegate(bool publishEvents)
    {
        var queue = new BuilderUiBackgroundQueue { AutoStart = false, PublishEvents = publishEvents };
        queue.BeforeEnqueueReturns = snapshot => queue.Cancel(snapshot.TaskId);
        await using var fixture = new BuilderUiFixture(queue: queue);
        List<BuilderJobSnapshot> notifications = [];
        var result = await fixture.Application.RunJobAsync(new()
        { Operation = "ensure-sdk", Kind = BuilderProjectKind.Dotnet }, snapshot =>
        {
            notifications.Add(snapshot);
            return Task.CompletedTask;
        }).WaitAsync(TimeSpan.FromSeconds(3));
        var activity = Assert.Single(queue.ListTasks());
        Assert.Equal(BackgroundTaskState.Cancelled, activity.State);
        AssertCancellationBeforeBackend(result, activity, notifications);
        Assert.Empty(fixture.Backend.Starts);
        Assert.Equal(0, queue.SubscriberCount);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.InvokeStaleDelegateAsync(activity.TaskId));
        Assert.Empty(fixture.Backend.Starts);
    }

    [Fact]
    public async Task HostCancellationAfterDelegateAdmissionDrainsWithoutStartingBackendOrWaitingForFiniteGate()
    {
        var queue = new BuilderUiBackgroundQueue();
        var operations = new BuilderOperationQueue(queue);
        var backend = new BuilderUiBackend();
        await using var application = new BuilderProjectApplicationService(backend, new BuilderUiStore(), operations, BuilderTestJournal.Create());
        var entered = BuilderUiFixture.Signal();
        var release = BuilderUiFixture.Signal();
        var blocking = operations.RunAsync(async () => { entered.TrySetResult(); await release.Task; });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            List<BuilderJobSnapshot> notifications = [];
            var pending = application.RunJobAsync(new() { Operation = "create", Kind = BuilderProjectKind.Node }, snapshot =>
            {
                notifications.Add(snapshot);
                return Task.CompletedTask;
            });
            await BuilderUiFixture.WaitAsync(() => Volatile.Read(ref queue.DelegateInvocations) == 1);
            var activity = Assert.Single(queue.ListTasks());
            Assert.Equal(BackgroundTaskState.Running, activity.State);
            queue.Cancel(activity.TaskId);

            var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.False(blocking.IsCompleted);
            activity = Assert.Single(queue.ListTasks());
            Assert.Equal(BackgroundTaskState.Cancelled, activity.State);
            AssertCancellationBeforeBackend(result, activity, notifications);
            Assert.Empty(backend.Starts);
            Assert.Equal(0, queue.SubscriberCount);
        }
        finally { release.TrySetResult(); await blocking.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    private static void AssertCancellationBeforeBackend(BuilderJobSnapshot result, BackgroundTaskSnapshot activity,
        IReadOnlyList<BuilderJobSnapshot> notifications)
    {
        Assert.Same(result, Assert.Single(notifications));
        Assert.Equal(activity.Metadata["builder.jobId"], result.Id);
        Assert.Equal(activity.Metadata["builder.operation"], result.Operation);
        Assert.Equal("cancelled", result.State);
        Assert.Contains("cancelled", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Output);
        Assert.Null(result.Project);
        Assert.Null(result.Installation);
        Assert.Null(result.DevelopmentFolder);
        Assert.Null(result.ArchivePath);
    }

    [Fact]
    public async Task CallerAndActivationCancellationCompleteJobsThatNeverStart()
    {
        var queue = new BuilderUiBackgroundQueue { AutoStart = false, PublishEvents = false };
        await using var fixture = new BuilderUiFixture(queue: queue);
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Application.RunJobAsync(new() { Operation = "ensure-sdk", Kind = BuilderProjectKind.Node }, cancellationToken: cancellation.Token);
        await BuilderUiFixture.WaitAsync(() => queue.ListTasks().Count == 1);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending).WaitAsync(TimeSpan.FromSeconds(3));
        var owned = fixture.Application.RunJobAsync(new() { Operation = "ensure-sdk", Kind = BuilderProjectKind.Dotnet });
        await BuilderUiFixture.WaitAsync(() => queue.ListTasks().Count == 2);
        await fixture.Application.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owned);
        Assert.All(queue.ListTasks(), item => Assert.Equal(BackgroundTaskState.Cancelled, item.State));
        Assert.Empty(fixture.Backend.Starts);
        Assert.Equal(0, queue.SubscriberCount);
    }

    [Theory]
    [InlineData("succeeded", BackgroundTaskState.Completed)]
    [InlineData("failed", BackgroundTaskState.Failed)]
    [InlineData("cancelled", BackgroundTaskState.Cancelled)]
    public async Task ShellTracksBackendOutcomeProgressAndProjectMetadata(string state, BackgroundTaskState shellState)
    {
        await using var fixture = new BuilderUiFixture();
        var project = BuilderUiFixture.Project("activity");
        fixture.Backend.Projects[project.EntryPoint] = project;
        fixture.Backend.Result = request => fixture.Backend.DefaultResult(request) with { State = "running", Message = "Compiling source" };
        var job = fixture.Application.RunJobAsync(new()
        {
            Operation = "build", Kind = project.Kind, EntryPoint = project.EntryPoint, ProjectFolder = project.ProjectFolder,
            DisplayName = project.DisplayName, PackageId = project.PackageId,
        }, projectId: "bookmark-activity");
        await BuilderUiFixture.WaitAsync(() => fixture.Queue.ListTasks().Any(item => item.StatusText == "Compiling source"));
        var activity = Assert.Single(fixture.Queue.ListTasks());
        Assert.True(activity.IsIndeterminate);
        Assert.Equal(BackgroundTaskIndicator.Main, activity.Indicator);
        Assert.Equal("bookmark-activity", activity.Metadata["builder.projectId"]);
        Assert.Equal(project.PackageId, activity.Metadata["builder.packageId"]);
        Assert.Equal("build", activity.Metadata["builder.operation"]);
        var id = activity.Metadata["builder.jobId"];
        fixture.Backend.Jobs[id] = fixture.Backend.Jobs[id] with { State = state, Message = "Build " + state, Error = state == "failed" ? "Compilation failed" : null };
        var result = await job.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(state, result.State);
        Assert.Equal(project, result.Project);
        activity = Assert.Single(fixture.Queue.ListTasks());
        Assert.Equal(shellState, activity.State);
        Assert.Equal(state == "succeeded" ? 100d : (double?)null, activity.ProgressPercent);
        Assert.Equal(0, fixture.Queue.SubscriberCount);

        fixture.Backend.Result = null;
        await fixture.Application.RunJobAsync(new() { Operation = "ensure-sdk", Kind = BuilderProjectKind.Node });
        Assert.Equal(BackgroundTaskIndicator.Settings, fixture.Queue.ListTasks().Single(item => item.Metadata["builder.operation"] == "ensure-sdk").Indicator);
    }

    [Fact]
    public async Task EnqueueAcknowledgementFailureCancelsAcceptedWorkAndUnsubscribes()
    {
        var queue = new BuilderUiBackgroundQueue { AutoStart = false, PublishEvents = false,
            BeforeEnqueueReturns = _ => throw new IOException("Lost queue acknowledgement") };
        await using var fixture = new BuilderUiFixture(queue: queue);
        await Assert.ThrowsAsync<IOException>(() => fixture.Application.RunJobAsync(new() { Operation = "ensure-sdk", Kind = BuilderProjectKind.Node }));
        Assert.Equal(BackgroundTaskState.Cancelled, Assert.Single(queue.ListTasks()).State);
        Assert.Equal(0, queue.SubscriberCount);
        Assert.Empty(fixture.Backend.Starts);
    }

    [Fact]
    public async Task RunningCancellationDrainsBackendCleanupBeforeReleasingFiniteQueue()
    {
        await using var fixture = new BuilderUiFixture();
        var polled = BuilderUiFixture.Signal();
        var cleanup = BuilderUiFixture.Signal();
        var release = BuilderUiFixture.Signal();
        var polls = 0;
        fixture.Backend.Result = request => fixture.Backend.DefaultResult(request) with { State = request.Kind == BuilderProjectKind.Dotnet ? "running" : "succeeded" };
        fixture.Backend.BeforeGet = async (request, token) =>
        {
            if (request.Kind == BuilderProjectKind.Dotnet && Interlocked.Increment(ref polls) == 1)
            { polled.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        };
        fixture.Backend.BeforeCancel = async (_, _) => { cleanup.TrySetResult(); await release.Task; };
        var first = fixture.Application.RunJobAsync(new() { Operation = "ensure-sdk", Kind = BuilderProjectKind.Dotnet });
        await polled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = fixture.Application.RunJobAsync(new() { Operation = "ensure-sdk", Kind = BuilderProjectKind.Node });
        fixture.Queue.Cancel(fixture.Queue.ListTasks().Single(item => item.Metadata["builder.kind"] == "dotnet").TaskId);
        await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Single(fixture.Backend.Starts);
        release.TrySetResult();
        Assert.Equal("cancelled", (await first).State);
        Assert.Equal("succeeded", (await second).State);
        Assert.True(fixture.Backend.CleanupUsedIndependentToken);
    }

    [Fact]
    public async Task PersistenceOverlappingAutosavesDebouncesToLatestGeneration()
    {
        var store = new BuilderUiStore();
        await using var persistence = new BuilderProjectPersistence(store, new BuilderUiStateUpdates(), TimeSpan.FromMilliseconds(25));
        persistence.RequestSave([Record("one")]);
        persistence.RequestSave([Record("two")]);
        await BuilderUiFixture.WaitAsync(() => store.Saves.Count > 0);
        Assert.Equal("two", Assert.Single(Assert.Single(store.Saves)).Id);
    }

    [Fact]
    public async Task PersistenceStaleCompletionDoesNotSuppressNewerGeneration()
    {
        var started = BuilderUiFixture.Signal();
        var release = BuilderUiFixture.Signal();
        var store = new BuilderUiStore { Save = async (records, _) => { if (records[0].Id == "one") { started.TrySetResult(); await release.Task; } } };
        await using var persistence = new BuilderProjectPersistence(store, new BuilderUiStateUpdates(), TimeSpan.FromMilliseconds(5));
        persistence.RequestSave([Record("one")]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        persistence.RequestSave([Record("two")]);
        release.TrySetResult();
        await BuilderUiFixture.WaitAsync(() => store.Saves.Count == 2);
        Assert.Equal(["one", "two"], store.Saves.Select(records => records[0].Id).ToArray());
    }

    [Fact]
    public async Task PersistenceConcurrentDisposalSharesFinalFlush()
    {
        var started = BuilderUiFixture.Signal();
        var release = BuilderUiFixture.Signal();
        var store = new BuilderUiStore { Save = async (_, _) => { started.TrySetResult(); await release.Task; } };
        var persistence = new BuilderProjectPersistence(store, new BuilderUiStateUpdates(), TimeSpan.FromMinutes(1));
        persistence.RequestSave([Record("pending")]);
        var first = persistence.DisposeAsync().AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = persistence.DisposeAsync().AsTask();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        release.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.Equal("pending", Assert.Single(Assert.Single(store.Saves)).Id);
    }

    [Fact]
    public async Task PersistenceDisposalWaitsForSaveAlreadyInProgress()
    {
        var started = BuilderUiFixture.Signal();
        var release = BuilderUiFixture.Signal();
        var store = new BuilderUiStore { Save = async (_, _) => { started.TrySetResult(); await release.Task; } };
        var persistence = new BuilderProjectPersistence(store, new BuilderUiStateUpdates(), TimeSpan.FromMinutes(1));
        var save = persistence.SaveNowAsync([Record("saving")]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var disposal = persistence.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        release.TrySetResult();
        await Task.WhenAll(save, disposal);
        Assert.Single(store.Saves);
    }

    [Fact]
    public async Task AppSettlementExpeditesDebounceWithoutDisposingOrStartingAnUnregisteredFinalSave()
    {
        using var settlement = new CancellationTokenSource();
        var updates = new BuilderUiStateUpdates { SettlementRequested = settlement.Token };
        var store = new BuilderUiStore();
        await using var persistence = new BuilderProjectPersistence(store, updates, TimeSpan.FromMinutes(1));
        persistence.RequestSave([Record("accepted")]);
        updates.Accepting = false;
        settlement.Cancel();
        await BuilderUiFixture.WaitAsync(() => store.Saves.Count == 1);
        await persistence.DisposeAsync();
        Assert.Equal("accepted", Assert.Single(Assert.Single(store.Saves)).Id);
    }

    [Fact]
    public async Task RejectedAdmissionDoesNotReplaceAnAlreadyAcceptedBookmarkSnapshot()
    {
        var updates = new BuilderUiStateUpdates();
        var store = new BuilderUiStore();
        await using var persistence = new BuilderProjectPersistence(store, updates, TimeSpan.FromMinutes(1));
        persistence.RequestSave([Record("accepted")]);
        updates.Accepting = false;
        Assert.Throws<InvalidOperationException>(() => persistence.RequestSave([Record("not-accepted")]));
        await persistence.DisposeAsync();
        Assert.Equal("accepted", Assert.Single(Assert.Single(store.Saves)).Id);
    }

    [Fact]
    public async Task LatestSaveFailureRemainsVisibleAtDisposalWithoutAnUnregisteredRetry()
    {
        var error = new IOException("Save did not acknowledge persistence.");
        var attempts = 0;
        var store = new BuilderUiStore { Save = (_, _) => { attempts++; return Task.FromException(error); } };
        var persistence = new BuilderProjectPersistence(store, new BuilderUiStateUpdates(), TimeSpan.Zero);
        var failed = BuilderUiFixture.Signal();
        persistence.SaveFailed += (_, args) => { Assert.Same(error, args.Exception); failed.TrySetResult(); };
        persistence.RequestSave([Record("accepted")]);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => persistence.DisposeAsync().AsTask()));
        Assert.Equal(1, attempts);
        Assert.Empty(store.Saves);
    }

    [Fact]
    public async Task CancelledSaveWaitLeavesItsAcceptedWorkOwnedUntilActualCompletion()
    {
        var started = BuilderUiFixture.Signal();
        var release = BuilderUiFixture.Signal();
        var store = new BuilderUiStore { Save = async (_, token) => { started.SetResult(); await release.Task.WaitAsync(token); } };
        var persistence = new BuilderProjectPersistence(store, new BuilderUiStateUpdates(), TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        var save = persistence.SaveNowAsync([Record("accepted")], cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        var disposal = persistence.DisposeAsync().AsTask();
        try { Assert.False(disposal.IsCompleted); }
        finally { release.TrySetResult(); }
        await disposal.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Single(store.Saves);
    }

    [Fact]
    public async Task DisposalDuringCanceledLoadDrainsCleanupAndNeverFlushesEmptyState()
    {
        var started = BuilderUiFixture.Signal();
        var canceled = BuilderUiFixture.Signal();
        var cleanup = BuilderUiFixture.Signal();
        var store = new BuilderUiStore { Load = async token =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { canceled.TrySetResult(); await cleanup.Task; throw; }
            return [];
        } };
        var fixture = new BuilderUiFixture(store: store);
        var initialization = fixture.Model.InitializeAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var disposal = fixture.DisposeAsync().AsTask();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(disposal.IsCompleted);
        cleanup.TrySetResult();
        await Task.WhenAll(disposal, initialization);
        Assert.Empty(store.Saves);
    }

    [Fact]
    public async Task UnchangedLoadAndDisposalDoNotRewriteBookmarks()
    {
        var fixture = new BuilderUiFixture();
        await fixture.Model.InitializeAsync();
        await fixture.DisposeAsync();
        Assert.Empty(fixture.Store.Saves);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("{\"version\":1,\"projects\":[]}")]
    [InlineData("{\"version\":3,\"projects\":[]}")]
    [InlineData("{\"version\":2,\"projects\":[null]}")]
    public async Task StoreCorruptionAndUnsupportedDocumentsRemainUntouchedEvenAfterSaveAttempt(string persisted)
    {
        var context = new BuilderStateContext();
        context.Value = persisted;
        var store = new BuilderProjectStore(context);
        await Assert.ThrowsAnyAsync<Exception>(() => store.LoadAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync([]));
        Assert.Equal(persisted, context.Value);
    }

    [Fact]
    public async Task CurrentBookmarksRoundTripWithoutConversion()
    {
        var context = new BuilderStateContext();
        var store = new BuilderProjectStore(context);
        var records = new[] { Record("local") };
        Assert.Empty(await store.LoadAsync());
        await store.SaveAsync(records);
        using var document = JsonDocument.Parse(context.Value!);
        Assert.Equal(2, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(records, await store.LoadAsync());
        Assert.DoesNotContain("workspaceId", context.Value, StringComparison.Ordinal);
    }

    [FileSystemFact(TestFileSystemCapability.DirectorySymbolicLinks, "Builder creation path containment requires directory symbolic links.")]
    public void CreationFolderRejectsSymlinkEscapeAndCanonicalEntryPointDeduplicatesAliases()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-builder-containment", Guid.NewGuid().ToString("N"));
        var parent = Path.Combine(root, "parent");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(parent, "escape"), outside);
        try
        {
            var paths = new BuilderPathService();
            Assert.Throws<InvalidOperationException>(() => paths.ResolveCreationFolder(parent, "escape"));
            Assert.True(paths.SameEntryPoint(Path.Combine(parent, "escape", "package.json"), Path.Combine(outside, "package.json")));
            Assert.Throws<ArgumentException>(() => paths.ResolveCreationFolder(parent, "../outside"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static BuilderProjectRecord Record(string id) => BuilderUiFixture.Record(BuilderUiFixture.Project(id)) with { Id = id };

    private sealed class InconsistentContentRuntime : BuilderServices.IBuilderRuntimeService
    {
        public ValueTask<string> ConnectDevelopmentAppAsync(string appId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SunderContent> ReadDevelopmentCommandAsync(string appId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask CompleteDevelopmentCommandAsync(string appId, string requestId, string responseJson, string? error, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<string> GetBackendIdAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SunderContent> DiscoverAsync(string folder, CancellationToken cancellationToken = default) => ValueTask.FromResult(new SunderContent(
            "application/json", "builder-result.json", 1, new string('0', 64), null, true,
            _ => ValueTask.FromResult<Stream>(new MemoryStream([0, 1]))));
        public ValueTask<SunderContent> InspectAsync(string entryPoint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SunderContent> GetToolchainsAsync(string kind, bool checkUpdates, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<string> StartJobAsync(string requestJson, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SunderContent> GetJobAsync(string jobId, string expectedBackendId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask CancelJobAsync(string jobId, string expectedBackendId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class BuilderStateContext : IPackageContext, IPackageStorageContext, IPackageKeyValueStore
    {
        public string? Value { get; set; }
        public string PackageId => "younics.sunder.package.maker";
        public string Version => "2.0.0";
        public string ContentRootPath => Path.GetTempPath();
        public IPackageStorageContext Storage => this;
        public IPackageKeyValueStore State => this;
        public IPackageSettings Settings => throw new NotSupportedException();
        public IPackageSecrets Secrets => throw new NotSupportedException();
        public IPackageLogging Logging => throw new NotSupportedException();
        public IPackageFileStore Files => throw new NotSupportedException();
        public IPackageRoleLocalWorkspace RoleLocalWorkspace => throw new NotSupportedException();
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default) { Value = value; return Task.CompletedTask; }
        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Value is not null);
        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default) { Value = null; return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
