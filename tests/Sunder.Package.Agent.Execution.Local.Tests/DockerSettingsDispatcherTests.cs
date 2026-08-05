extern alias DockerPackage;

using System.Collections.Concurrent;
using System.ComponentModel;
using Sunder.Package.Agent.Execution.Docker;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;
using Xunit;
using DockerPresentationDispatcher = DockerPackage::Sunder.Package.Agent.Shared.Presentation.IPresentationDispatcher;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class DockerSettingsDispatcherTests
{
    [Fact]
    public async Task WorkerProcessCallback_AppliesImageRefreshOnPresentationDispatcher()
    {
        using var dispatcher = new DockerTestPresentationDispatcher();
        var runtime = new ControlledDockerRuntimeClient();
        var processes = new RaisingBackgroundProcessQueue();
        using var viewModel = new DockerExecutionSettingsViewModel(
            runtime,
            processes,
            logger: null,
            uiDispatcher: dispatcher);
        await InitializeAsync(viewModel, runtime);
        var row = Assert.Single(viewModel.Images);
        var published = ObservePropertyThread(row, nameof(DockerImageRowViewModel.Status));

        var workerThread = await processes.RaiseDockerPullChangedFromWorkerAsync();
        await runtime.Requests[1].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runtime.Requests[1].Response.TrySetResult(SettingsResponse(
            new DockerImageDefinition("example:latest", DockerImageStatus.Failed, null, "pull failed"),
            catalogRevision: 2));
        var publicationThread = await published.WaitAsync(TimeSpan.FromSeconds(2));
        await dispatcher.WaitForIdleAsync();

        Assert.NotEqual(workerThread, publicationThread);
        Assert.Equal(dispatcher.ThreadId, publicationThread);
        Assert.Equal(DockerImageStatus.Failed, row.Status);
    }

    [Fact]
    public async Task WorkerProcessCallback_ReportsProtocolFailureOnPresentationDispatcher()
    {
        using var dispatcher = new DockerTestPresentationDispatcher();
        var runtime = new ControlledDockerRuntimeClient();
        var processes = new RaisingBackgroundProcessQueue();
        using var viewModel = new DockerExecutionSettingsViewModel(
            runtime,
            processes,
            logger: null,
            uiDispatcher: dispatcher);
        await InitializeAsync(viewModel, runtime);
        var published = ObservePropertyThread(viewModel, nameof(DockerExecutionSettingsViewModel.StatusText));

        var workerThread = await processes.RaiseDockerPullChangedFromWorkerAsync();
        await runtime.Requests[1].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runtime.Requests[1].Response.TrySetResult(new DockerExecutionOperationResponse(
            Images: [Image(DockerImageStatus.Ready)],
            CatalogRevision: null));
        var publicationThread = await published.WaitAsync(TimeSpan.FromSeconds(2));
        await dispatcher.WaitForIdleAsync();

        Assert.NotEqual(workerThread, publicationThread);
        Assert.Equal(dispatcher.ThreadId, publicationThread);
        Assert.Equal("docker.protocol.invalid-response", viewModel.RuntimeErrorCode);
    }

    [Fact]
    public async Task WorkerProcessCallback_ReportsRefreshFailureOnPresentationDispatcher()
    {
        using var dispatcher = new DockerTestPresentationDispatcher();
        var runtime = new ControlledDockerRuntimeClient();
        var processes = new RaisingBackgroundProcessQueue();
        using var viewModel = new DockerExecutionSettingsViewModel(
            runtime,
            processes,
            logger: null,
            uiDispatcher: dispatcher);
        await InitializeAsync(viewModel, runtime);
        var published = ObservePropertyThread(viewModel, nameof(DockerExecutionSettingsViewModel.StatusText));
        processes.FailNextListProcesses();

        var workerThread = await processes.RaiseDockerPullChangedFromWorkerAsync();
        await runtime.Requests[1].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runtime.Requests[1].Response.TrySetResult(SettingsResponse(
            Image(DockerImageStatus.Ready),
            catalogRevision: 2));
        var publicationThread = await published.WaitAsync(TimeSpan.FromSeconds(2));
        await dispatcher.WaitForIdleAsync();

        Assert.NotEqual(workerThread, publicationThread);
        Assert.Equal(dispatcher.ThreadId, publicationThread);
        Assert.Equal("docker.presentation.refresh-failed", viewModel.RuntimeErrorCode);
    }

    [Fact]
    public async Task WorkerStartedCommand_PublishesBusyTransitionsOnPresentationDispatcher()
    {
        using var dispatcher = new DockerTestPresentationDispatcher();
        var runtime = new ControlledDockerRuntimeClient();
        var processes = new RaisingBackgroundProcessQueue();
        using var viewModel = new DockerExecutionSettingsViewModel(
            runtime,
            processes,
            logger: null,
            uiDispatcher: dispatcher);
        await InitializeAsync(viewModel, runtime);
        await dispatcher.InvokeAsync(() => viewModel.NewImageReference = "added:latest");
        var transitions = new ConcurrentQueue<(bool IsBusy, int ThreadId)>();
        var busyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busyCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName != nameof(DockerExecutionSettingsViewModel.IsBusy))
            {
                return;
            }

            transitions.Enqueue((viewModel.IsBusy, Environment.CurrentManagedThreadId));
            if (viewModel.IsBusy)
            {
                busyStarted.TrySetResult();
            }
            else
            {
                busyCompleted.TrySetResult();
            }
        };

        var workerThread = 0;
        var command = Task.Run(async () =>
        {
            workerThread = Environment.CurrentManagedThreadId;
            await viewModel.AddImageCommand.ExecuteAsync(null);
        });
        await busyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await runtime.Requests[1].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runtime.Requests[1].Response.TrySetResult(SettingsResponse(
            new DockerImageDefinition("added:latest", DockerImageStatus.Ready, null, null),
            catalogRevision: 2));
        await command.WaitAsync(TimeSpan.FromSeconds(2));
        await busyCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotEqual(dispatcher.ThreadId, workerThread);
        Assert.Equal(new[] { true, false }, transitions.Select(transition => transition.IsBusy));
        Assert.All(transitions, transition => Assert.Equal(dispatcher.ThreadId, transition.ThreadId));
    }

    private static async Task InitializeAsync(
        DockerExecutionSettingsViewModel viewModel,
        ControlledDockerRuntimeClient runtime)
    {
        var initialization = viewModel.InitializeAsync();
        await runtime.Requests[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runtime.Requests[0].Response.TrySetResult(SettingsResponse(
            Image(DockerImageStatus.Ready),
            catalogRevision: 1));
        await initialization.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static Task<int> ObservePropertyThread(INotifyPropertyChanged source, string propertyName)
    {
        var published = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == propertyName)
            {
                published.TrySetResult(Environment.CurrentManagedThreadId);
            }
        };
        return published.Task;
    }

    private static DockerExecutionOperationResponse SettingsResponse(
        DockerImageDefinition image,
        long catalogRevision)
        => new(
            TimeoutSeconds: "300",
            DockerCliPath: string.Empty,
            Images: [image],
            CatalogRevision: catalogRevision);

    private static DockerImageDefinition Image(DockerImageStatus status)
        => new("example:latest", status, null, null);

    private sealed class ControlledDockerRuntimeClient : IPackageRuntimeClient
    {
        private int _requestIndex;

        public bool IsAvailable => true;

        public PendingRequest[] Requests { get; } =
            Enumerable.Range(0, 8).Select(_ => new PendingRequest()).ToArray();

        public async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            var pending = Requests[Interlocked.Increment(ref _requestIndex) - 1];
            pending.Started.TrySetResult();
            return (TResponse)(object)await pending.Response.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class PendingRequest
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<DockerExecutionOperationResponse> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RaisingBackgroundProcessQueue : IBackgroundProcessQueue
    {
        private int _failNextList;

        public event EventHandler<BackgroundProcessChangedEventArgs>? ProcessChanged;

        public BackgroundProcessSnapshot Enqueue(BackgroundProcessRequest request)
            => throw new NotSupportedException();

        public IReadOnlyList<BackgroundProcessSnapshot> ListProcesses(string? groupKey = null)
        {
            if (Interlocked.Exchange(ref _failNextList, 0) != 0)
            {
                throw new InvalidOperationException("background process snapshot failed");
            }

            return [];
        }

        public bool Cancel(Guid processId) => false;

        public void FailNextListProcesses() => Interlocked.Exchange(ref _failNextList, 1);

        public Task<int> RaiseDockerPullChangedFromWorkerAsync()
            => Task.Run(() =>
            {
                var threadId = Environment.CurrentManagedThreadId;
                var now = DateTimeOffset.UtcNow;
                var snapshot = new BackgroundProcessSnapshot(
                    Guid.NewGuid(),
                    "Docker pull",
                    DockerExecutionSettingsViewModel.ImagePullGroupKey,
                    BackgroundProcessIndicator.Settings,
                    BackgroundProcessConcurrencyMode.SequentialWithinGroup,
                    BackgroundProcessState.Completed,
                    "Completed",
                    100,
                    false,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [DockerExecutionSettingsViewModel.ImageReferenceMetadataKey] = "example:latest",
                    },
                    null,
                    now,
                    now,
                    now);
                ProcessChanged?.Invoke(this, new BackgroundProcessChangedEventArgs(snapshot));
                return threadId;
            });
    }

    private sealed class DockerTestPresentationDispatcher : DockerPresentationDispatcher, IDisposable
    {
        private readonly BlockingCollection<WorkItem> _queue = new();
        private readonly Thread _thread;
        private readonly TaskCompletionSource<int> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DockerTestPresentationDispatcher()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Docker test presentation dispatcher",
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
            return completion.Task;
        }

        public Task WaitForIdleAsync() => InvokeAsync(() => { });

        private void Run()
        {
            _started.SetResult(Environment.CurrentManagedThreadId);
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                try
                {
                    item.Action();
                    item.Completion.SetResult();
                }
                catch (Exception exception)
                {
                    item.Completion.SetException(exception);
                }
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join();
            _queue.Dispose();
        }

        private sealed record WorkItem(Action Action, TaskCompletionSource Completion);
    }
}
