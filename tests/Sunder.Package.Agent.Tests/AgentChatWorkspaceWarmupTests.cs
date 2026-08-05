using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentChatWorkspaceWarmupTests
{
    [Fact]
    public async Task Composer_RemainsAvailableWhileWorkspaceTargetWarmupRuns()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var store = new AgentLocalStore(scope.Context);
        var sessions = new AgentSessionService(store);
        var workspaces = new AgentWorkspaceService(store);
        var targets = new AgentExecutionTargetService(catalog);
        var tools = new AgentToolService(sessions, workspaces, targets, catalog);
        var profiles = new AgentProfileService(store, tools, catalog, catalog.BehaviorLoops);
        var permissions = new AgentPermissionService(store, catalog);
        var warmup = new BlockingExecutionGateway();
        var profile = await profiles.CreateProfileAsync("Profile");
        var workspace = workspaces.CreateWorkspace("Workspace");
        var session = sessions.CreateSession(
            "Session",
            profileId: profile.ProfileId,
            workspaceId: workspace.WorkspaceId);
        using var viewModel = new AgentChatViewModel(
            profiles,
            workspaces,
            sessions,
            permissions,
            new NoopRunGateway(),
            warmupService: warmup);
        viewModel.SelectedProfile = profile;
        viewModel.SelectedSession = new AgentSessionListItemViewModel(session);
        viewModel.SelectedWorkspace = workspace;

        await warmup.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(viewModel.CanUseChat);

        warmup.Complete(AgentExecutionTargetWarmupResult.Ready("Workspace tools are ready."));
        await warmup.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.CanUseChat);
    }

    private sealed class BlockingExecutionGateway : IAgentExecutionGateway
    {
        private readonly TaskCompletionSource<AgentExecutionTargetWarmupResult> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<AgentExecutionTargetDescriptor> ListTargets() => [];

        public async Task<AgentExecutionTargetWarmupResult> WarmWorkspaceAsync(
            AgentWorkspaceRecord workspace,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                return await _result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Completed.TrySetResult();
            }
        }

        public void Complete(AgentExecutionTargetWarmupResult result) => _result.TrySetResult(result);
    }

    private sealed class NoopRunGateway : IAgentRunGateway
    {
        public Task<AgentRunCheckpointRecord> QueueUserMessageAsync(
            Guid sessionId,
            string profileId,
            string userMessage,
            string workspaceId,
            IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            CancellationToken cancellationToken = default)
            => Task.FromException<AgentRunCheckpointRecord>(new NotSupportedException());

        public Task<AgentRunCheckpointRecord> RollbackAndQueueUserMessageAsync(
            Guid sessionId,
            Guid rollbackAnchorTurnId,
            string profileId,
            string userMessage,
            string workspaceId,
            IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            CancellationToken cancellationToken = default)
            => Task.FromException<AgentRunCheckpointRecord>(new NotSupportedException());

        public Task<AgentRunCheckpointRecord?> StopAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AgentRunCheckpointRecord?>(null);

        public Task<AgentRunCheckpointRecord?> ApprovePendingPermissionAsync(
            Guid sessionId,
            string requestId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AgentRunCheckpointRecord?>(null);

        public Task<AgentRunCheckpointRecord?> DenyPendingPermissionAsync(
            Guid sessionId,
            string requestId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AgentRunCheckpointRecord?>(null);
    }
}
