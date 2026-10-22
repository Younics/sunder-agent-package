extern alias BuilderApp;

using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using BuilderApp::Sunder.Package.Agent.Builder;
using Sunder.App.Services;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class BuilderJobCompositionTests
{
    [Fact]
    public async Task RealQueueCancellationWhileWaitingForBuilderGateReturnsConfirmedNoDispatch()
    {
        var token = TestContext.Current.CancellationToken;
        await using var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var operations = new BuilderOperationQueue(queue);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = operations.RunAsync(async () => { entered.SetResult(); await release.Task.WaitAsync(token); }, token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), token);
        var invoked = false;
        var pending = operations.EnqueueAsync("Waiting for Builder", BackgroundTaskIndicator.Main,
            new Dictionary<string, string>(), _ =>
            {
                invoked = true;
                throw new InvalidOperationException("The cancelled delegate must never dispatch a backend job.");
            }, token);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!queue.ListTasks().Any(task => task.State == BackgroundTaskState.Running)) await Task.Delay(10, deadline.Token);
            var task = Assert.Single(queue.ListTasks());
            Assert.True(queue.Cancel(task.TaskId));
            await Assert.ThrowsAsync<BuilderQueueCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3), token));
            Assert.False(invoked);
            Assert.Equal(BackgroundTaskState.Cancelled, Assert.Single(queue.ListTasks()).State);
        }
        finally { release.TrySetResult(); await gate; }
    }

    [AvaloniaFact]
    public async Task FailedJobThenQueuedSuccessorSettlesWithItsOwnJournalAndDispatcherAuthority()
    {
        using var files = RegressionTestPackageScope.Create();
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        var updates = new AppPackageStateUpdates(publication);
        var journal = new BuilderJobJournal(new GuardedStorage(new BuilderJobJournalFiles(files.Context), publication), updates);
        await using var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var backend = new Backend();
        await using var application = new BuilderProjectApplicationService(backend, new Store(), new(queue), journal);
        var dispatcher = new AvaloniaBuilderUiDispatcher();
        var presented = new List<BuilderJobSnapshot>();
        Task Changed(BuilderJobSnapshot snapshot) => dispatcher.InvokeAsync(() => presented.Add(snapshot));
        BuilderJobRequest Request(string id) => new()
        {
            Id = id, Operation = "setup", Kind = BuilderProjectKind.Dotnet,
            ProjectFolder = Path.Combine(files.RootPath, id), EntryPoint = Path.Combine(files.RootPath, id, "Package.csproj"),
        };

        var first = application.RunJobAsync(Request("first-job"), Changed);
        await backend.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = application.RunJobAsync(Request("second-job"), Changed);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (queue.ListTasks().Count != 2) await Task.Delay(10, deadline.Token);
            backend.ReleaseFirst.SetResult();
            Assert.Equal("failed", (await first.WaitAsync(TimeSpan.FromSeconds(5))).State);
            await backend.SecondRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var handoff = publication.BeginDataHandoff();
            backend.ReleaseSecond.SetResult();
            // The successor was already accepted. Its exact terminal read/cleanup must
            // persist under its own authority even though new App admission is closed.
            Assert.Equal("succeeded", (await second.WaitAsync(TimeSpan.FromSeconds(5))).State);
            await handoff.Drained.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "first-job", "second-job" }, backend.Starts.ToArray());
            Assert.Contains(presented, snapshot => snapshot.Id == "second-job" && snapshot.State == "succeeded");
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(files.RootPath, "builder-admission/journal.v1.json")));
            Assert.Empty(document.RootElement.GetProperty("pending").EnumerateArray());
            Assert.Equal(2, document.RootElement.GetProperty("completed").GetArrayLength());
        }
        finally
        {
            backend.ReleaseFirst.TrySetResult();
            backend.ReleaseSecond.TrySetResult();
            await Task.WhenAll(first, second);
        }
    }

    private sealed class GuardedStorage(BuilderJobJournalFiles files, AppPackageGenerationPublication publication) : IBuilderJobJournalStorage
    {
        public Task<IAsyncDisposable> EnterAsync(CancellationToken token) => files.EnterAsync(token);
        public Task<byte[]?> ReadAsync(CancellationToken token) => files.ReadAsync(token);
        public Task WriteAsync(byte[] bytes, CancellationToken token)
            => publication.RunDataMutationAsync(() => files.WriteAsync(bytes, token), token);
    }

    private sealed class Backend : IBuilderBackend
    {
        internal TaskCompletionSource FirstRead { get; } = Signal();
        internal TaskCompletionSource SecondRead { get; } = Signal();
        internal TaskCompletionSource ReleaseFirst { get; } = Signal();
        internal TaskCompletionSource ReleaseSecond { get; } = Signal();
        internal ConcurrentQueue<string> Starts { get; } = new();
        private int _secondReads;
        public Task<IReadOnlyList<BuilderProjectInspection>> DiscoverAsync(string folder, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BuilderProjectInspection> InspectAsync(string entryPoint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BuilderToolchainStatus> GetToolchainsAsync(BuilderProjectKind kind, bool checkUpdates, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BuilderRecordedJobRecovery?> RecoverRecordedJobAsync(BuilderJobReservation reservation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IBuilderJobExecution> CaptureExecutionAsync(CancellationToken cancellationToken = default) => Task.FromResult<IBuilderJobExecution>(new Execution(this));
        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class Execution(Backend backend) : IBuilderJobExecution
        {
            public string BackendId { get; } = new('a', 32);
            public BuilderJobProviderIdentity ProviderIdentity { get; } = new(BuilderViewModel.BuilderPackageId, "2.0.0", "builder.runtime",
                "younics.sunder.package.maker.runtime", "2.0.0", new('a', 64), new('b', 64), Guid.NewGuid(), 1, 1);
            public PackageWorkerExecutionReference Reference { get; } = new(Guid.NewGuid(), Guid.NewGuid().ToString("N"));
            public Task<string> StartJobAsync(BuilderJobRequest request, CancellationToken cancellationToken = default)
            { backend.Starts.Enqueue(request.Id!); return Task.FromResult(request.Id!); }
            public async Task<BuilderJobSnapshot> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
            {
                if (jobId == "first-job") { backend.FirstRead.TrySetResult(); await backend.ReleaseFirst.Task; }
                else if (Interlocked.Increment(ref backend._secondReads) == 1)
                { backend.SecondRead.TrySetResult(); await backend.ReleaseSecond.Task; }
                return new() { Id = jobId, BackendId = BackendId, Operation = "setup", State = jobId == "first-job" ? "failed" : "succeeded",
                    Message = "Exact terminal result", Output = "build output" };
            }
            public Task CancelJobAsync(string jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task<PackageWorkerExecutionObservation> ObserveCleanupAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public bool IsKnownNotStarted(Exception error) => false;
        }
    }

    private sealed class Store : IBuilderProjectStore
    {
        public Task<IReadOnlyList<BuilderProjectRecord>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<BuilderProjectRecord>>([]);
        public Task SaveAsync(IReadOnlyList<BuilderProjectRecord> projects, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
