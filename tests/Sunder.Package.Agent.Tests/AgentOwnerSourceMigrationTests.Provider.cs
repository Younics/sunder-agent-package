using System.Runtime.CompilerServices;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentOwnerSourceMigrationTests
{
    private sealed class OwnerProviderProbe(IAgentOwnerRuntimeProvider inner) : IAgentOwnerRuntimeProvider
    {
        public List<object> Requests { get; } = [];
        public Exception? Failure { get; set; }
        public Exception? StreamFailure { get; set; }
        public bool Block { get; set; }
        public TaskCompletionSource Started { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AgentTransportProfile? OverrideProfile { get; set; }
        private async ValueTask<T> Forward<T>(object request, Func<CancellationToken, ValueTask<T>> invoke, CancellationToken token)
        {
            Requests.Add(request);
            token.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            if (Block) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return await invoke(token);
        }
        private async IAsyncEnumerable<T> Stream<T>(object request, IAsyncEnumerable<T> events, [EnumeratorCancellation] CancellationToken token)
        {
            Requests.Add(request);
            await foreach (var item in events.WithCancellation(token))
            {
                yield return item;
                if (StreamFailure is not null) throw StreamFailure;
            }
        }
        public ValueTask<AgentTransportProfile> NegotiateTransportProfileAsync(AgentTransportProfileRequest request, CancellationToken token)
            => Forward(request, t => OverrideProfile is { } profile ? ValueTask.FromResult(profile) : inner.NegotiateTransportProfileAsync(request, t), token);
        public ValueTask<AgentChatSnapshotProjection> GetChatSnapshotAsync(AgentChatSnapshotRequest request, CancellationToken token)
            => Forward(request, t => inner.GetChatSnapshotAsync(request, t), token);
        public ValueTask<AgentDashboardProjection> GetDashboardAsync(AgentDashboardRequest request, CancellationToken token)
            => Forward(request, t => inner.GetDashboardAsync(request, t), token);
        public ValueTask<AgentTranscriptPage> GetTranscriptPageAsync(AgentTranscriptPageRequest request, CancellationToken token)
            => Forward(request, t => inner.GetTranscriptPageAsync(request, t), token);
        public ValueTask<AgentCatalogProjection> GetCatalogAsync(AgentCatalogRequest request, CancellationToken token)
            => Forward(request, t => inner.GetCatalogAsync(request, t), token);
        public ValueTask<AgentProfileCommandResult> ExecuteProfileCommandAsync(AgentProfileCommand request, CancellationToken token)
            => Forward(request, t => inner.ExecuteProfileCommandAsync(request, t), token);
        public ValueTask<AgentWorkspaceCommandResult> ExecuteWorkspaceCommandAsync(AgentWorkspaceCommand request, CancellationToken token)
            => Forward(request, t => inner.ExecuteWorkspaceCommandAsync(request, t), token);
        public ValueTask<AgentSessionCommandResult> ExecuteSessionCommandAsync(AgentSessionCommand request, CancellationToken token)
            => Forward(request, t => inner.ExecuteSessionCommandAsync(request, t), token);
        public ValueTask<AgentRunCommandResult> ExecuteRunCommandAsync(AgentRunCommand request, CancellationToken token)
            => Forward(request, t => inner.ExecuteRunCommandAsync(request, t), token);
        public ValueTask<AgentRunCommandStatusResult> GetRunStatusAsync(AgentRunCommandStatusRequest request, CancellationToken token)
            => Forward(request, t => inner.GetRunStatusAsync(request, t), token);
        public ValueTask<AgentPermissionProjection> ExecutePermissionCommandAsync(AgentPermissionCommand request, CancellationToken token)
            => Forward(request, t => inner.ExecutePermissionCommandAsync(request, t), token);
        public ValueTask<AgentAttachmentContentUploadResult> UploadAttachmentAsync(SunderRpcInvocationContext context, AgentAttachmentContentUploadRequest request, CancellationToken token)
            => Forward(request, t => inner.UploadAttachmentAsync(context, request, t), token);
        public ValueTask<AgentAttachmentContentDownloadResult> DownloadAttachmentAsync(SunderRpcInvocationContext context, AgentAttachmentContentDownloadRequest request, CancellationToken token)
            => Forward(request, t => inner.DownloadAttachmentAsync(context, request, t), token);
        public ValueTask<AgentAttachmentContentDiscardResult> DiscardAttachmentAsync(AgentAttachmentContentDiscardRequest request, CancellationToken token)
            => Forward(request, t => inner.DiscardAttachmentAsync(request, t), token);
        public ValueTask<HistorySearchResponse> SearchHistoryAsync(HistorySearchRequest request, CancellationToken token)
            => Forward(request, t => inner.SearchHistoryAsync(request, t), token);
        public ValueTask<HistorySearchState> GetHistoryStateAsync(HistorySearchStateRequest request, CancellationToken token)
            => Forward(request, t => inner.GetHistoryStateAsync(request, t), token);
        public ValueTask<HistorySearchCommandResult> ExecuteHistoryCommandAsync(HistorySearchCommand request, CancellationToken token)
            => Forward(request, t => inner.ExecuteHistoryCommandAsync(request, t), token);
        public ValueTask<AgentTranscriptAroundTurnPage> GetTranscriptAroundAsync(AgentTranscriptAroundTurnRequest request, CancellationToken token)
            => Forward(request, t => inner.GetTranscriptAroundAsync(request, t), token);
        public ValueTask<AgentTranscriptToolDetailResult> GetTranscriptToolDetailAsync(AgentTranscriptToolDetailRequest request, CancellationToken token)
            => Forward(request, t => inner.GetTranscriptToolDetailAsync(request, t), token);
        public IAsyncEnumerable<AgentRuntimeChange> WatchChangesAsync(AgentChangeSubscription request, CancellationToken token)
            => Stream(request, inner.WatchChangesAsync(request, token), token);
        public IAsyncEnumerable<HistorySearchStatus> WatchHistoryStatusAsync(HistorySearchStatusSubscription request, CancellationToken token)
            => Stream(request, inner.WatchHistoryStatusAsync(request, token), token);
    }
}
