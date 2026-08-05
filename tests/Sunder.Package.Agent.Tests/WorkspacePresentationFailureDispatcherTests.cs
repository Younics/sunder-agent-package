extern alias AgentCore;

using System.Collections.Concurrent;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Xunit;
using AgentPresentationDispatcher = AgentCore::Sunder.Package.Agent.Shared.Presentation.IPresentationDispatcher;

namespace Sunder.Package.Agent.Tests;

public sealed class WorkspacePresentationFailureDispatcherTests
{
    [Fact]
    public async Task ViewContext_WorkerFailurePublishesOnPresentationDispatcher()
    {
        using var dispatcher = new WorkspaceTestPresentationDispatcher();
        using var context = new AgentWorkspacesViewContext(dispatcher);
        var published = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Failed += _ => published.TrySetResult(Environment.CurrentManagedThreadId);

        var workerThread = await Task.Run(() =>
        {
            var threadId = Environment.CurrentManagedThreadId;
            context.Run(_ => Task.FromException(new InvalidOperationException("context failure")));
            return threadId;
        });
        var publicationThread = await published.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotEqual(workerThread, publicationThread);
        Assert.Equal(dispatcher.ThreadId, publicationThread);
    }

    [Fact]
    public async Task ViewModel_BackgroundTaskFailurePublishesOnPresentationDispatcher()
    {
        using var dispatcher = new WorkspaceTestPresentationDispatcher();
        var workspaceGateway = new RuntimeWorkspaceGateway();
        var executionGateway = new FailingReloadExecutionGateway();
        using var catalog = new RegressionTestExtensionCatalog();
        using var viewModel = new AgentWorkspacesViewModel(
            workspaceGateway,
            executionGateway,
            catalog,
            settingsNavigationService: null,
            uiDispatcher: dispatcher);
        await viewModel.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(2));
        var published = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(AgentWorkspacesViewModel.StatusText)
                && viewModel.StatusText == FailingReloadExecutionGateway.FailureMessage)
            {
                published.TrySetResult(Environment.CurrentManagedThreadId);
            }
        };

        var workerThread = await workspaceGateway.RaiseConnectedFromWorkerAsync();
        await executionGateway.ReloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Run(executionGateway.FailReload);
        var publicationThread = await published.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotEqual(workerThread, publicationThread);
        Assert.Equal(dispatcher.ThreadId, publicationThread);
    }

    private sealed class RuntimeWorkspaceGateway : IAgentWorkspaceGateway, IAgentRuntimeAvailability
    {
        public event Action? WorkspacesChanged
        {
            add { }
            remove { }
        }

        public event Action<AgentRuntimeConnectionState>? ConnectionStateChanged;

        public AgentRuntimeConnectionState ConnectionState { get; private set; }

        public bool IsRuntimeAvailable => ConnectionState == AgentRuntimeConnectionState.Connected;

        public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => [];

        public AgentWorkspaceRecord? GetWorkspace(string workspaceId) => null;

        public AgentWorkspaceRecord CreateWorkspace(string displayName) => throw new NotSupportedException();

        public void SaveWorkspace(string workspaceId, string displayName, string? description)
            => throw new NotSupportedException();

        public void SaveWorkspaceAggregate(
            string workspaceId,
            string displayName,
            string? description,
            IReadOnlyList<AgentWorkspacePathRecord> paths,
            IReadOnlyList<AgentWorkspaceDocumentRecord> documents,
            string? executionTargetId)
            => throw new NotSupportedException();

        public void DeleteWorkspace(string workspaceId) => throw new NotSupportedException();

        public IReadOnlyList<AgentWorkspaceBindingRecord> ListBindings(string workspaceId) => [];

        public AgentWorkspaceBindingRecord SavePrimaryExecutionBinding(
            string workspaceId,
            string contributionId,
            string displayRole = AgentWorkspaceBindingRoles.PrimaryExecutionTarget)
            => throw new NotSupportedException();

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> RaiseConnectedFromWorkerAsync()
            => Task.Run(() =>
            {
                var threadId = Environment.CurrentManagedThreadId;
                ConnectionState = AgentRuntimeConnectionState.Connected;
                ConnectionStateChanged?.Invoke(ConnectionState);
                return threadId;
            });
    }

    private sealed class FailingReloadExecutionGateway : IAgentExecutionGateway, IAgentExecutionTargetLoader
    {
        public const string FailureMessage = "execution target reload failed";
        private readonly TaskCompletionSource _failReload =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _loadCount;

        public TaskCompletionSource ReloadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<AgentExecutionTargetDescriptor> ListTargets() => [];

        public Task<AgentExecutionTargetWarmupResult> WarmWorkspaceAsync(
            AgentWorkspaceRecord workspace,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async Task<IReadOnlyList<AgentExecutionTargetDescriptor>> ListTargetsAsync(
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _loadCount) == 1)
            {
                return [];
            }

            ReloadStarted.TrySetResult();
            await _failReload.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(FailureMessage);
        }

        public void FailReload() => _failReload.TrySetResult();
    }

    private sealed class WorkspaceTestPresentationDispatcher : AgentPresentationDispatcher, IDisposable
    {
        private readonly BlockingCollection<WorkItem> _queue = new();
        private readonly Thread _thread;
        private readonly TaskCompletionSource<int> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkspaceTestPresentationDispatcher()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Workspace test presentation dispatcher",
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
