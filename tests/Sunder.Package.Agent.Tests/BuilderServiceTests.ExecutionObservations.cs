extern alias BuilderApp;

using System.Collections.Concurrent;
using BuilderApp::Sunder.Package.Agent.Builder;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;
using BuilderServices = BuilderApp::Sunder.Generated.Packages.Younics_Sunder_Package_Maker_Services;

namespace Sunder.Package.Agent.Tests;

public sealed partial class BuilderServiceTests
{
    [Fact]
    public async Task JobCapturesBackendThenHostReceiptFromOneExactProviderBeforeItsOnlyStart()
    {
        await using var fixture = new ObservedBuilderFixture();
        List<string> order = [];
        fixture.Backend.BeforeGetBackendId = _ => { order.Add("backend"); return Task.CompletedTask; };
        fixture.Observations.BeforeCapture = provider =>
        {
            Assert.Same(fixture.Catalog.Current, provider);
            Assert.Empty(fixture.Backend.Starts);
            order.Add("capture");
        };
        fixture.Backend.OnStart = _ => order.Add("start");
        fixture.Backend.LoseFirstStartResponse = true;

        var result = await fixture.Application.RunJobAsync(new() { Operation = "ensure-sdk", Kind = BuilderProjectKind.Node });

        Assert.Equal(["backend", "capture", "start"], order);
        Assert.Equal("succeeded", result.State);
        Assert.Single(fixture.Backend.Starts);
        Assert.Single(fixture.Observations.Captured);
        Assert.Equal(1, fixture.Catalog.Lists);
        Assert.Empty(fixture.Observations.Observed);
    }

    [Fact]
    public async Task ProviderReplacementDuringCaptureCannotRetargetTheHostCaptureOrStart()
    {
        await using var fixture = new ObservedBuilderFixture();
        var original = fixture.Catalog.Current;
        var replacement = new BuilderUiBackend { BackendId = fixture.Backend.BackendId };
        var failure = new SunderRpcException(new(SunderRpcErrorKind.StaleEndpoint, "rpc.stale-endpoint", "The selected provider retired before capture."));
        fixture.Backend.BeforeGetBackendId = _ =>
        {
            fixture.Catalog.Replace(new BuilderRuntimeFake(replacement));
            return Task.CompletedTask;
        };
        fixture.Observations.BeforeCapture = provider =>
        {
            Assert.Same(original, provider);
            Assert.NotSame(fixture.Catalog.Current, provider);
            throw failure;
        };

        Assert.Same(failure, await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Application.RunJobAsync(new()
        { Operation = "watch", Kind = BuilderProjectKind.Dotnet })));

        Assert.Equal(1, fixture.Observations.CaptureAttempts);
        Assert.Empty(fixture.Backend.Starts);
        Assert.Empty(replacement.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedExecutionCaptureFailsBeforeStartButSourceReadsStayCold(bool servicePresent)
    {
        await using var fixture = new ObservedBuilderFixture();
        var project = BuilderUiFixture.Project("capture-unavailable");
        fixture.Backend.Projects[project.EntryPoint] = project;
        fixture.Observations.CaptureFailure = new NotSupportedException("This Host predates worker execution observations.");
        var gateway = new BuilderBackendGateway(fixture.Catalog, servicePresent ? fixture.Observations : null);
        await using var application = new BuilderProjectApplicationService(gateway, new BuilderUiStore(), new(new BuilderUiBackgroundQueue()), BuilderTestJournal.Create());

        Assert.Equal(project, await application.InspectAsync(project.EntryPoint));
        Assert.Equal(project, Assert.Single(await application.DiscoverAsync(project.ProjectFolder)));
        Assert.Empty(fixture.Observations.Captured);
        Assert.Empty(fixture.Observations.Observed);
        var request = new BuilderJobRequest { Id = Guid.NewGuid().ToString("N"), Operation = "watch", Kind = project.Kind };
        await Assert.ThrowsAsync<NotSupportedException>(() => application.RunJobAsync(request));

        Assert.Empty(fixture.Backend.Starts);
        var stopped = await application.StopJobAsync(request.Id);
        Assert.Equal(BuilderJobStopOutcome.NotStarted, stopped.Outcome);
        Assert.Null(stopped.TerminalSnapshot);
    }

    [Fact]
    public async Task CancellationDuringHostCaptureCannotCrossTheStartBoundary()
    {
        await using var fixture = new ObservedBuilderFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Observations.BeforeCapture = _ => cancellation.Cancel();
        var request = new BuilderJobRequest { Id = Guid.NewGuid().ToString("N"), Operation = "watch", Kind = BuilderProjectKind.Dotnet };

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Application.RunJobAsync(request,
            cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Empty(fixture.Backend.Starts);
        Assert.Equal(BuilderJobStopOutcome.NotStarted, (await fixture.Application.StopJobAsync(request.Id)).Outcome);
    }

    [Fact]
    public async Task ASecondStartOnTheSameCaptureIsRejectedRatherThanReplayed()
    {
        await using var fixture = new ObservedBuilderFixture();
        var execution = await fixture.Gateway.CaptureExecutionAsync();
        var request = new BuilderJobRequest { Id = Guid.NewGuid().ToString("N"), BackendId = execution.BackendId,
            Operation = "ensure-sdk", Kind = BuilderProjectKind.Node };
        await execution.StartJobAsync(request);
        await Assert.ThrowsAsync<InvalidOperationException>(() => execution.StartJobAsync(request));
        await Assert.ThrowsAsync<InvalidOperationException>(() => execution.GetJobAsync("another-job"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => execution.CancelJobAsync("another-job"));
        Assert.Single(fixture.Backend.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactHostReceiptReleasesNestedCrossTechnologyExclusionWithoutInventingAJobOutcome(bool oldIsChild)
    {
        await using var fixture = new ObservedBuilderFixture();
        var source = BuilderUiFixture.Project("historical-cleanup");
        var root = source.ProjectFolder;
        if (oldIsChild) source = source with { ProjectFolder = Path.Combine(root, "child"), EntryPoint = Path.Combine(root, "child", "Package.csproj") };
        var nextFolder = oldIsChild ? root : Path.Combine(root, "child");
        var owned = await fixture.MakeUncertainAsync(source);
        var reference = Assert.Single(fixture.Observations.Captured).Reference;
        var replacement = fixture.ReplaceWithSameBackendId(owned);

        await Assert.ThrowsAsync<IOException>(() => fixture.Application.RunJobAsync(new()
        { Operation = "build", Kind = BuilderProjectKind.Node, ProjectFolder = nextFolder }));
        Assert.True(fixture.Application.HasUnconfirmedJob(nextFolder));
        Assert.Empty(replacement.Starts); // A same-ID replacement cannot acknowledge the old job.
        Assert.Equal(0, fixture.ReplacementRuntime!.Reads);
        Assert.Equal(0, fixture.ReplacementRuntime.Cancels);

        fixture.Observations.State = PackageWorkerExecutionState.CleanupConfirmed;
        // Deliberately no confirmation timestamp: the exact positive state is
        // authority, not elapsed lease age or a timestamp interpreted by Builder.
        var stopped = await fixture.Application.StopJobAsync(owned.Id!);

        Assert.Equal(BuilderJobStopOutcome.CleanupConfirmedUnknownOutcome, stopped.Outcome);
        Assert.Null(stopped.TerminalSnapshot);
        Assert.Equal("running", stopped.LastSnapshot?.State);
        Assert.Equal(source.EntryPoint, stopped.LastSnapshot?.Project?.EntryPoint);
        Assert.Equal(reference, stopped.ExecutionObservation?.Reference);
        Assert.Null(stopped.ExecutionObservation?.ConfirmedAtUtc);
        Assert.False(fixture.Application.HasUnconfirmedJob(nextFolder));
        Assert.All(fixture.Observations.Observed, observed => Assert.Equal(reference, observed));
        Assert.Single(fixture.Backend.Starts);
        Assert.Equal("running", fixture.Backend.Jobs[owned.Id!].State);
        Assert.Equal(0, fixture.ReplacementRuntime.Reads);
        Assert.Equal(0, fixture.ReplacementRuntime.Cancels);

        var result = await fixture.Application.RunJobAsync(new()
        { Operation = "build", Kind = BuilderProjectKind.Node, ProjectFolder = nextFolder });
        Assert.Equal("succeeded", result.State);
        Assert.Single(replacement.Starts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Application.RunJobAsync(owned));
    }

    [Fact]
    public async Task CleanupPendingDoesNotReleaseOwnershipWhileWaitingForTheExactPositiveReceipt()
    {
        await using var fixture = new ObservedBuilderFixture();
        var source = BuilderUiFixture.Project("pending-cleanup");
        var request = await fixture.MakeUncertainAsync(source);
        var pending = BuilderUiFixture.Signal();
        var release = BuilderUiFixture.Signal();
        var observations = 0;
        fixture.Observations.Observe = async (reference, token) =>
        {
            if (Interlocked.Increment(ref observations) == 1)
            {
                pending.TrySetResult();
                await release.Task.WaitAsync(token);
                return new(reference, PackageWorkerExecutionState.CleanupPending);
            }
            return new(reference, PackageWorkerExecutionState.CleanupConfirmed);
        };
        var stopping = fixture.Application.StopJobAsync(request.Id!);
        try
        {
            await pending.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(stopping.IsCompleted);
            Assert.True(fixture.Application.HasUnconfirmedJob(source.ProjectFolder));
        }
        finally { release.TrySetResult(); }
        var stopped = await stopping.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, observations);
        Assert.Equal(BuilderJobStopOutcome.CleanupConfirmedUnknownOutcome, stopped.Outcome);
    }

    [Fact]
    public async Task MismatchedPositiveReceiptCannotReleaseTheOriginalJob()
    {
        await using var fixture = new ObservedBuilderFixture();
        var source = BuilderUiFixture.Project("wrong-receipt");
        var request = await fixture.MakeUncertainAsync(source);
        var before = fixture.Observations.Observed.Count;
        fixture.Observations.Observe = (_, _) => Task.FromResult(new PackageWorkerExecutionObservation(
            new(Guid.NewGuid(), "another-execution"), PackageWorkerExecutionState.CleanupConfirmed));

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Application.StopJobAsync(request.Id!));

        Assert.Equal(before + 1, fixture.Observations.Observed.Count);
        Assert.True(fixture.Application.HasUnconfirmedJob(source.ProjectFolder));
        Assert.False(fixture.Application.IsJobStopConfirmed(request.Id));
    }

    [Theory]
    [InlineData(SunderRpcErrorKind.Domain, "agent.builder.job-not-found")]
    [InlineData(SunderRpcErrorKind.StaleEndpoint, "rpc.provider-retired")]
    public async Task NotFoundOrProviderDisappearanceCannotSubstituteForCleanupEvidence(SunderRpcErrorKind kind, string code)
    {
        await using var fixture = new ObservedBuilderFixture();
        var source = BuilderUiFixture.Project("missing-is-not-proof");
        var request = await fixture.MakeUncertainAsync(source);
        fixture.ReplaceWithSameBackendId(request);
        fixture.Backend.BeforeGet = (_, _) => Task.FromException(new SunderRpcException(new(kind, code, "The old job cannot be read.")));

        await Assert.ThrowsAsync<IOException>(() => fixture.Application.StopJobAsync(request.Id!));

        Assert.True(fixture.Application.HasUnconfirmedJob(source.ProjectFolder));
        Assert.False(fixture.Application.IsJobStopConfirmed(request.Id));
        Assert.Equal(0, fixture.ReplacementRuntime!.Reads);
        Assert.Equal(0, fixture.ReplacementRuntime.Cancels);
    }

    [Theory]
    [InlineData(SunderRpcErrorKind.PermissionDenied)]
    [InlineData(SunderRpcErrorKind.Protocol)]
    public async Task ReceiptSecurityAndProtocolFailuresAreNotRetriedOrHidden(SunderRpcErrorKind kind)
    {
        await using var fixture = new ObservedBuilderFixture();
        var source = BuilderUiFixture.Project("denied-receipt");
        var request = await fixture.MakeUncertainAsync(source);
        var before = fixture.Observations.Observed.Count;
        var failure = new SunderRpcException(new(kind, "test.denied", "Not a reconnect."));
        fixture.Observations.Observe = (_, _) => Task.FromException<PackageWorkerExecutionObservation>(failure);

        Assert.Same(failure, await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Application.StopJobAsync(request.Id!)));

        Assert.Equal(before + 1, fixture.Observations.Observed.Count);
        Assert.True(fixture.Application.HasUnconfirmedJob(source.ProjectFolder));
    }

    [Fact]
    public async Task CaptureSecurityFailureIsNotRetriedAndCannotCreateAnUncertainStart()
    {
        await using var fixture = new ObservedBuilderFixture();
        var failure = new SunderRpcException(new(SunderRpcErrorKind.PermissionDenied, "test.denied", "Capture was denied."));
        fixture.Observations.CaptureFailure = failure;
        var request = new BuilderJobRequest { Id = Guid.NewGuid().ToString("N"), Operation = "watch", Kind = BuilderProjectKind.Dotnet };

        Assert.Same(failure, await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Application.RunJobAsync(request)));

        Assert.Equal(1, fixture.Observations.CaptureAttempts);
        Assert.Empty(fixture.Backend.Starts);
        Assert.Equal(BuilderJobStopOutcome.NotStarted, (await fixture.Application.StopJobAsync(request.Id)).Outcome);
    }

    [Fact]
    public async Task ExplicitUnsupportedHostFeatureIsNotTreatedAsAReconnect()
    {
        await using var fixture = new ObservedBuilderFixture();
        var failure = new SunderRpcException(new(SunderRpcErrorKind.Unavailable, "rpc.worker-execution.unsupported", "This Host does not support capture."));
        fixture.Observations.CaptureFailure = failure;

        Assert.Same(failure, await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Application.RunJobAsync(new()
        { Operation = "watch", Kind = BuilderProjectKind.Dotnet })));

        Assert.Equal(1, fixture.Observations.CaptureAttempts);
        Assert.Empty(fixture.Backend.Starts);
    }

    [Fact]
    public async Task ExactTerminalRecoveryRemainsAvailableAfterCatalogReplacement()
    {
        await using var fixture = new ObservedBuilderFixture();
        var source = BuilderUiFixture.Project("terminal-recovery");
        var request = await fixture.MakeUncertainAsync(source);
        fixture.ReplaceWithSameBackendId(request);
        fixture.Backend.BeforeGet = null;
        fixture.Backend.BeforeCancel = null;
        var observed = fixture.Observations.Observed.Count;

        var stopped = await fixture.Application.StopJobAsync(request.Id!);

        Assert.Equal(BuilderJobStopOutcome.KnownTerminal, stopped.Outcome);
        Assert.Equal("cancelled", stopped.TerminalSnapshot?.State);
        Assert.Equal(source, stopped.TerminalSnapshot?.Project);
        Assert.Null(stopped.ExecutionObservation);
        Assert.Equal(observed, fixture.Observations.Observed.Count);
        Assert.Equal(0, fixture.ReplacementRuntime!.Reads);
        Assert.Equal(0, fixture.ReplacementRuntime.Cancels);
    }

    [Fact]
    public async Task ConfirmedExecutionCleanupDuringCancellationSurfacesUnknownOutcomeWithoutTerminalCallback()
    {
        await using var fixture = new ObservedBuilderFixture();
        var source = BuilderUiFixture.Project("cancel-cleaned");
        fixture.Backend.Projects[source.EntryPoint] = source;
        using var cancellation = new CancellationTokenSource();
        List<BuilderJobSnapshot> snapshots = [];
        fixture.Observations.State = PackageWorkerExecutionState.CleanupConfirmed;
        var error = await Assert.ThrowsAsync<BuilderJobOutcomeUnknownException>(() => fixture.Application.RunJobAsync(new()
        { Operation = "watch", Kind = source.Kind, EntryPoint = source.EntryPoint, ProjectFolder = source.ProjectFolder }, snapshot =>
        {
            snapshots.Add(snapshot);
            fixture.DisconnectOriginal();
            cancellation.Cancel();
            return Task.CompletedTask;
        }, cancellation.Token));

        Assert.Equal(BuilderJobStopOutcome.CleanupConfirmedUnknownOutcome, error.StopResult.Outcome);
        Assert.Null(error.StopResult.TerminalSnapshot);
        Assert.All(snapshots, snapshot => Assert.Equal("running", snapshot.State));
        Assert.Equal(source, error.StopResult.LastSnapshot?.Project);
        Assert.False(fixture.Application.HasUnconfirmedJob(source.ProjectFolder));
        Assert.Single(fixture.Backend.Starts);
    }

    [Fact]
    public async Task RetainedDevelopmentOwnerWaitsForExactWatchCleanupRatherThanReplacementReadiness()
    {
        await using var fixture = new ObservedBuilderFixture();
        var source = BuilderUiFixture.Project("retained-exact-cleanup");
        fixture.Backend.Projects[source.EntryPoint] = source;
        var development = new BuilderUiDevelopment();
        var store = new BuilderUiStore();
        await using var model = new BuilderViewModel(fixture.Application,
            new(store, new BuilderUiStateUpdates(), TimeSpan.FromMinutes(1)), new(), new BuilderUiDispatcher(),
            development, development, NullPackageSettingsNavigationService.Instance);
        await model.OpenFolderAsync(source.ProjectFolder);
        var row = model.SelectedProject!;
        var watchReads = 0;
        fixture.Backend.BeforeGet = (request, _) => request.Operation == "watch" && Interlocked.Increment(ref watchReads) > 1
            ? Task.FromException(new IOException("The original watch cannot be observed.")) : Task.CompletedTask;
        fixture.Backend.BeforeCancel = (_, _) => Task.FromException(new IOException("The original stop cannot be acknowledged."));
        await model.LoadSelectedProjectAsync();
        await BuilderUiFixture.WaitAsync(() => fixture.Application.HasUnconfirmedJob(source.ProjectFolder));
        var watch = Assert.Single(fixture.Backend.Starts, request => request.Operation == "watch");
        var handle = Assert.Single(development.Sessions);
        fixture.ReplaceWithSameBackendId(watch);

        await model.UnloadSelectedProjectAsync();

        Assert.False(handle.Disposed);
        Assert.True(row.HasSession);
        Assert.True(fixture.Application.HasUnconfirmedJob(source.ProjectFolder));
        Assert.Equal(0, fixture.ReplacementRuntime!.Reads);
        Assert.Equal(0, fixture.ReplacementRuntime.Cancels);

        fixture.Observations.State = PackageWorkerExecutionState.CleanupConfirmed;
        await model.UnloadSelectedProjectAsync();

        Assert.True(handle.Disposed);
        Assert.False(row.HasSession);
        Assert.False(fixture.Application.HasUnconfirmedJob(source.ProjectFolder));
        var stopped = await fixture.Application.StopJobAsync(watch.Id!);
        Assert.Equal(BuilderJobStopOutcome.CleanupConfirmedUnknownOutcome, stopped.Outcome);
        Assert.Null(stopped.TerminalSnapshot);
        Assert.Equal("running", fixture.Backend.Jobs[watch.Id!].State);
    }

    private sealed class ObservedBuilderFixture : IAsyncDisposable
    {
        internal BuilderUiBackend Backend { get; } = new();
        internal BuilderRuntimeCatalog Catalog { get; }
        internal BuilderReceiptObservations Observations { get; } = new();
        internal BuilderBackendGateway Gateway { get; }
        internal BuilderProjectApplicationService Application { get; }
        internal BuilderRuntimeFake? ReplacementRuntime { get; private set; }

        internal ObservedBuilderFixture(IBuilderJobJournal? journal = null)
        {
            Catalog = new(new BuilderRuntimeFake(Backend));
            Gateway = new(Catalog, Observations);
            Application = new(Gateway, new BuilderUiStore(), new(new BuilderUiBackgroundQueue()), journal ?? BuilderTestJournal.Create());
        }

        internal void DisconnectOriginal()
        {
            Backend.BeforeGet = (_, _) => Task.FromException(new IOException("Original transport disconnected."));
            Backend.BeforeCancel = (_, _) => Task.FromException(new IOException("Original transport disconnected."));
        }

        internal async Task<BuilderJobRequest> MakeUncertainAsync(BuilderProjectInspection source)
        {
            Backend.Projects[source.EntryPoint] = source;
            await Assert.ThrowsAsync<BuilderJobCleanupException>(() => Application.RunJobAsync(new()
            { Operation = "watch", Kind = source.Kind, EntryPoint = source.EntryPoint, ProjectFolder = source.ProjectFolder }, _ =>
            { DisconnectOriginal(); return Task.CompletedTask; }));
            return Assert.Single(Backend.Starts);
        }

        internal BuilderUiBackend ReplaceWithSameBackendId(BuilderJobRequest old)
        {
            var replacement = new BuilderUiBackend { BackendId = Backend.BackendId };
            // Even a matching backend/job ID and terminal-looking replacement
            // snapshot cannot acknowledge an execution selected by another capture.
            replacement.Requests[old.Id!] = old;
            replacement.Jobs[old.Id!] = BuilderUiBackend.Snapshot(old, "succeeded");
            ReplacementRuntime = new(replacement);
            Catalog.Replace(ReplacementRuntime);
            return replacement;
        }

        public async ValueTask DisposeAsync()
        {
            // Test-only independent cleanup of the original exact fake, never a replacement acknowledgement.
            Backend.BeforeGet = null;
            Backend.BeforeCancel = null;
            Observations.Observe = null;
            Observations.State = PackageWorkerExecutionState.CleanupConfirmed;
            await Application.DisposeAsync();
        }
    }

    private sealed class BuilderRuntimeCatalog : IServiceProviders<BuilderServices.IBuilderRuntimeService>
    {
        internal ServiceProvider<BuilderServices.IBuilderRuntimeService> Current { get; private set; }
        internal int Lists;
        internal BuilderRuntimeCatalog(BuilderServices.IBuilderRuntimeService runtime) => Current = Entry(runtime);
        internal void Replace(BuilderServices.IBuilderRuntimeService runtime) => Current = Entry(runtime);
        internal void Rebind(bool refreshSessionGeneration)
        {
            var old = Current;
            Current = new(new SunderRpcProviderSnapshot(old.PackageId, old.PackageVersion, old.ProviderId,
                old.ContractId, old.ContractVersion, old.DescriptorSha256, old.AbiSha256, old.ActivationId, old.ActivationEpoch,
                old.SessionGeneration + (refreshSessionGeneration ? 1 : 0), new(Guid.NewGuid().ToString("N")),
                old.CatalogRevision + 1, SunderRpcProviderState.Active), old.Service);
        }
        private static ServiceProvider<BuilderServices.IBuilderRuntimeService> Entry(BuilderServices.IBuilderRuntimeService runtime)
        {
            var activation = Guid.NewGuid();
            return new(new SunderRpcProviderSnapshot(BuilderViewModel.BuilderPackageId, "2.0.0", "builder.runtime",
                "younics.sunder.package.maker.runtime", "1.0.0", new('a', 64), new('b', 64), activation, 1, 1,
                new(activation.ToString("N")), 1, SunderRpcProviderState.Active), runtime);
        }
        public ValueTask<ServiceProviderCatalog<BuilderServices.IBuilderRuntimeService>> ListAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Lists);
            return ValueTask.FromResult(new ServiceProviderCatalog<BuilderServices.IBuilderRuntimeService>(1, 1, [Current]));
        }
        public IAsyncEnumerable<ServiceProviderCatalogEvent<BuilderServices.IBuilderRuntimeService>> WatchAsync(long afterRevision,
            long afterSequence, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class BuilderReceiptObservations : IPackageWorkerExecutionObservations
    {
        private readonly Guid _originalHost;
        internal BuilderReceiptObservations(Guid? originalHost = null) => _originalHost = originalHost ?? Guid.NewGuid();
        internal ConcurrentQueue<(object Provider, PackageWorkerExecutionReference Reference)> Captured { get; } = new();
        internal ConcurrentQueue<PackageWorkerExecutionReference> Observed { get; } = new();
        internal Action<object>? BeforeCapture;
        internal Func<object, PackageWorkerExecutionReference>? CaptureReference;
        internal Exception? CaptureFailure;
        internal int CaptureAttempts;
        internal PackageWorkerExecutionState State;
        internal Func<PackageWorkerExecutionReference, CancellationToken, Task<PackageWorkerExecutionObservation>>? Observe;
        public Task<PackageWorkerExecutionReference> CaptureAsync<TService>(ServiceProvider<TService> provider,
            CancellationToken cancellationToken = default) where TService : class
        {
            Interlocked.Increment(ref CaptureAttempts);
            if (CaptureFailure is { } failure) return Task.FromException<PackageWorkerExecutionReference>(failure);
            BeforeCapture?.Invoke(provider);
            var reference = CaptureReference?.Invoke(provider) ?? new PackageWorkerExecutionReference(_originalHost, provider.ActivationId.ToString("N"));
            Captured.Enqueue((provider, reference));
            return Task.FromResult(reference);
        }
        public Task<PackageWorkerExecutionObservation> ObserveAsync(PackageWorkerExecutionReference reference,
            CancellationToken cancellationToken = default)
        {
            Observed.Enqueue(reference);
            return Observe?.Invoke(reference, cancellationToken) ?? Task.FromResult(new PackageWorkerExecutionObservation(reference, State));
        }
    }

    private sealed class BuilderRuntimeFake(BuilderUiBackend backend) : BuilderServices.IBuilderRuntimeService
    {
        internal int Reads;
        internal int Cancels;
        internal int BackendIds;
        public async ValueTask<string> GetBackendIdAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref BackendIds);
            return await backend.GetBackendIdAsync(cancellationToken);
        }
        public async ValueTask<SunderContent> DiscoverAsync(string folder, CancellationToken cancellationToken = default)
            => BuilderContent.Create(await backend.DiscoverAsync(folder, cancellationToken));
        public async ValueTask<SunderContent> InspectAsync(string entryPoint, CancellationToken cancellationToken = default)
            => BuilderContent.Create(await backend.InspectAsync(entryPoint, cancellationToken));
        public async ValueTask<SunderContent> GetToolchainsAsync(string kind, bool checkUpdates, CancellationToken cancellationToken = default)
            => BuilderContent.Create(await backend.GetToolchainsAsync(kind == "dotnet" ? BuilderProjectKind.Dotnet : BuilderProjectKind.Node, checkUpdates, cancellationToken));
        public async ValueTask<string> StartJobAsync(string requestJson, CancellationToken cancellationToken = default)
            => await backend.StartJobAsync(BuilderContent.ReadJobRequest(requestJson), cancellationToken);
        public async ValueTask<SunderContent> GetJobAsync(string jobId, string expectedBackendId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Reads);
            return BuilderContent.Create(await backend.GetJobAsync(jobId, expectedBackendId, cancellationToken));
        }
        public async ValueTask CancelJobAsync(string jobId, string expectedBackendId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Cancels);
            await backend.CancelJobAsync(jobId, expectedBackendId, cancellationToken);
        }
    }
}

// The shared fake is parent-owned in BuilderViewModelTests.cs. Its capture adapter
// delegates here; explicit journal/recovery constructor wiring is listed in README.
// This domain-only adapter never emits positive Host evidence; the gateway tests
// above independently exercise the real exact-provider/observation consumer.
internal sealed class BuilderTestExecution(BuilderUiBackend backend, string backendId) : IBuilderJobExecution
{
    public string BackendId { get; } = backendId;
    public BuilderJobProviderIdentity ProviderIdentity { get; } = new(BuilderViewModel.BuilderPackageId, "2.0.0", "builder.runtime",
        "younics.sunder.package.maker.runtime", "1.0.0", new('a', 64), new('b', 64), Guid.NewGuid(), 1, 1);
    public PackageWorkerExecutionReference Reference { get; } = new(Guid.NewGuid(), Guid.NewGuid().ToString("N"));
    internal static async Task<IBuilderJobExecution> CaptureAsync(BuilderUiBackend backend, CancellationToken token)
        => new BuilderTestExecution(backend, await backend.GetBackendIdAsync(token));
    public Task<string> StartJobAsync(BuilderJobRequest request, CancellationToken cancellationToken = default)
        => backend.StartJobAsync(request, cancellationToken);
    public Task<BuilderJobSnapshot> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
        => backend.GetJobAsync(jobId, BackendId, cancellationToken);
    public Task CancelJobAsync(string jobId, CancellationToken cancellationToken = default)
        => backend.CancelJobAsync(jobId, BackendId, cancellationToken);
    public Task<PackageWorkerExecutionObservation> ObserveCleanupAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new PackageWorkerExecutionObservation(Reference, PackageWorkerExecutionState.Unknown));
    public bool IsKnownNotStarted(Exception error) => BuilderStartFailureAuthority.IsReviewedPreAcceptance(error);
}
