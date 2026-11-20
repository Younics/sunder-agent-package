using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;

namespace Sunder.Package.Agent.Presentation.Tests;

internal static class RenderedWorkspaceFixture
{
    internal sealed class FailOnceWorkspaceGateway(IAgentWorkspaceGateway inner)
        : IAgentWorkspaceGateway
    {
        private int _initializeCount;

        public int InitializeCount => Volatile.Read(ref _initializeCount);
        public TaskCompletionSource FirstInitializationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FailFirstInitialization { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action? WorkspacesChanged
        {
            add => inner.WorkspacesChanged += value;
            remove => inner.WorkspacesChanged -= value;
        }
        public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => inner.ListWorkspaces();
        public AgentWorkspaceRecord? GetWorkspace(string workspaceId) => inner.GetWorkspace(workspaceId);
        public AgentWorkspaceRecord CreateWorkspace(string displayName) => inner.CreateWorkspace(displayName);
        public void SaveWorkspace(string workspaceId, string displayName, string? description)
            => inner.SaveWorkspace(workspaceId, displayName, description);
        public void SaveWorkspaceAggregate(
            string workspaceId,
            string displayName,
            string? description,
            IReadOnlyList<AgentWorkspacePathRecord> paths,
            IReadOnlyList<AgentWorkspaceDocumentRecord> documents,
            string? executionTargetId)
            => inner.SaveWorkspaceAggregate(
                workspaceId,
                displayName,
                description,
                paths,
                documents,
                executionTargetId);
        public Task DeleteWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
            => inner.DeleteWorkspaceAsync(workspaceId, cancellationToken);
        public IReadOnlyList<AgentWorkspaceBindingRecord> ListBindings(string workspaceId)
            => inner.ListBindings(workspaceId);
        public AgentWorkspaceBindingRecord SavePrimaryExecutionBinding(
            string workspaceId,
            string contributionId,
            string displayRole = AgentWorkspaceBindingRoles.PrimaryExecutionTarget)
            => inner.SavePrimaryExecutionBinding(workspaceId, contributionId, displayRole);
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _initializeCount) == 1)
            {
                FirstInitializationStarted.TrySetResult();
                await FailFirstInitialization.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("Injected startup failure.");
            }

            await inner.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
