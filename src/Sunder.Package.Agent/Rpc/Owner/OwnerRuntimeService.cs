using System.Runtime.CompilerServices;
using Sunder.Sdk.Rpc;
using Runtime = Sunder.Package.Agent.Runtime;
using History = Sunder.Package.Agent.HistorySearch;
using Domain = Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Rpc.Owner;

[SunderRpcService(SunderRpcExposure.Package, ContractId = "sunder.agent.owner.runtime", ServiceId = "agent-runtime", IncludeSchemaDialect = true)]
[SunderRpcProvider(ProviderId = "agent.owner.runtime", Audience = SunderRpcProviderAudience.Owner)]
[SunderRpcDomainError(OperationFailed, Description = "The requested Agent operation could not be completed.", DetailsType = typeof(JSONObject))]
internal sealed partial class OwnerRuntimeService(Runtime.IAgentOwnerRuntimeProvider provider)
{
    private const string OperationFailed = "agent.owner.operation-failed";

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = [OperationFailed])]
    public ValueTask<AgentTransportProfile> TransportProfileAsync(AgentTransportProfileRequest request, CancellationToken cancellationToken = default)
        => Invoke<Runtime.AgentTransportProfileRequest, Runtime.AgentTransportProfile, AgentTransportProfile>(request, provider.NegotiateTransportProfileAsync, cancellationToken);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = [OperationFailed])]
    public ValueTask<AgentChatSnapshotProjection> ChatSnapshotAsync(AgentChatSnapshotRequest request, CancellationToken cancellationToken = default)
        => Invoke<Runtime.AgentChatSnapshotRequest, Runtime.AgentChatSnapshotProjection, AgentChatSnapshotProjection>(request, provider.GetChatSnapshotAsync, cancellationToken);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = [OperationFailed])]
    public ValueTask<AgentDashboardProjection> DashboardAsync(AgentDashboardRequest request, CancellationToken cancellationToken = default)
        => Invoke<Runtime.AgentDashboardRequest, Runtime.AgentDashboardProjection, AgentDashboardProjection>(request, provider.GetDashboardAsync, cancellationToken);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = [OperationFailed])]
    public ValueTask<AgentTranscriptPage> TranscriptPageAsync(AgentTranscriptPageRequest request, CancellationToken cancellationToken = default)
        => Invoke<Runtime.AgentTranscriptPageRequest, Runtime.AgentTranscriptPage, AgentTranscriptPage>(request, provider.GetTranscriptPageAsync, cancellationToken);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = [OperationFailed])]
    public ValueTask<AgentCatalogProjection> CatalogAsync(AgentCatalogRequest request, CancellationToken cancellationToken = default)
        => Invoke<Runtime.AgentCatalogRequest, Runtime.AgentCatalogProjection, AgentCatalogProjection>(request, provider.GetCatalogAsync, cancellationToken);

    [SunderRpcMethod(Errors = [OperationFailed])]
    public ValueTask<AgentProfileCommandResult> ProfileCommandAsync(AgentProfileCommand request, CancellationToken cancellationToken = default)
        => Invoke<Runtime.AgentProfileCommand, Runtime.AgentProfileCommandResult, AgentProfileCommandResult>(request, provider.ExecuteProfileCommandAsync, cancellationToken);

    [SunderRpcMethod(Errors = [OperationFailed])]
    public ValueTask<AgentWorkspaceCommandResult> WorkspaceCommandAsync(AgentWorkspaceCommand request, CancellationToken cancellationToken = default)
        => Invoke<Runtime.AgentWorkspaceCommand, Runtime.AgentWorkspaceCommandResult, AgentWorkspaceCommandResult>(request, provider.ExecuteWorkspaceCommandAsync, cancellationToken);

    [SunderRpcMethod(Errors = [OperationFailed])]
    public ValueTask<AgentSessionCommandResult> SessionCommandAsync(AgentSessionCommand request, CancellationToken cancellationToken = default)
        => Invoke<Runtime.AgentSessionCommand, Runtime.AgentSessionCommandResult, AgentSessionCommandResult>(request, provider.ExecuteSessionCommandAsync, cancellationToken);

    [SunderRpcMethod(Errors = [OperationFailed])]
    public ValueTask<AgentRunCommandResult> RunCommandAsync(AgentRunCommand request, CancellationToken cancellationToken = default)
        => Invoke<Runtime.AgentRunCommand, Runtime.AgentRunCommandResult, AgentRunCommandResult>(request, provider.ExecuteRunCommandAsync, cancellationToken);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = [OperationFailed])]
    public ValueTask<AgentRunCommandStatusResult> RunStatusAsync(AgentRunCommandStatusRequest request, CancellationToken cancellationToken = default)
        => Invoke<Runtime.AgentRunCommandStatusRequest, Runtime.AgentRunCommandStatusResult, AgentRunCommandStatusResult>(request, provider.GetRunStatusAsync, cancellationToken);

    [SunderRpcMethod(Errors = [OperationFailed])]
    public ValueTask<AgentPermissionProjection> PermissionCommandAsync(AgentPermissionCommand request, CancellationToken cancellationToken = default)
        => Invoke<Runtime.AgentPermissionCommand, Runtime.AgentPermissionProjection, AgentPermissionProjection>(request, provider.ExecutePermissionCommandAsync, cancellationToken);

    [SunderRpcMethod(Errors = [OperationFailed])]
    public ValueTask<AgentAttachmentContentUploadResult> AttachmentUploadAsync(AgentAttachmentContentUploadRequest request, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => Invoke<Runtime.AgentAttachmentContentUploadRequest, Runtime.AgentAttachmentContentUploadResult, AgentAttachmentContentUploadResult>(request,
            (value, token) => provider.UploadAttachmentAsync(context, value, token), cancellationToken);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = [OperationFailed])]
    public ValueTask<AgentAttachmentContentDownloadResult> AttachmentDownloadAsync(AgentAttachmentContentDownloadRequest request, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => Invoke<Runtime.AgentAttachmentContentDownloadRequest, Runtime.AgentAttachmentContentDownloadResult, AgentAttachmentContentDownloadResult>(request,
            (value, token) => provider.DownloadAttachmentAsync(context, value, token), cancellationToken);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = [OperationFailed])]
    public ValueTask<AgentAttachmentContentDiscardResult> AttachmentDiscardAsync(AgentAttachmentContentDiscardRequest request, CancellationToken cancellationToken = default)
        => Invoke<Runtime.AgentAttachmentContentDiscardRequest, Runtime.AgentAttachmentContentDiscardResult, AgentAttachmentContentDiscardResult>(request, provider.DiscardAttachmentAsync, cancellationToken);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = [OperationFailed])]
    public ValueTask<HistorySearchResponse> HistorySearchAsync(HistorySearchRequest request, CancellationToken cancellationToken = default)
        => Invoke<History.HistorySearchRequest, History.HistorySearchResponse, HistorySearchResponse>(request, provider.SearchHistoryAsync, cancellationToken);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = [OperationFailed])]
    public ValueTask<HistorySearchState> HistoryStateAsync(HistorySearchStateRequest request, CancellationToken cancellationToken = default)
        => Invoke<History.HistorySearchStateRequest, History.HistorySearchState, HistorySearchState>(request, provider.GetHistoryStateAsync, cancellationToken);

    [SunderRpcMethod(Errors = [OperationFailed])]
    public ValueTask<HistorySearchCommandResult> HistoryCommandAsync(HistorySearchCommand request, CancellationToken cancellationToken = default)
        => Invoke<History.HistorySearchCommand, History.HistorySearchCommandResult, HistorySearchCommandResult>(request, provider.ExecuteHistoryCommandAsync, cancellationToken);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = [OperationFailed])]
    public ValueTask<AgentTranscriptAroundTurnPage> TranscriptAroundAsync(AgentTranscriptAroundTurnRequest request, CancellationToken cancellationToken = default)
        => Invoke<Runtime.AgentTranscriptAroundTurnRequest, Runtime.AgentTranscriptAroundTurnPage, AgentTranscriptAroundTurnPage>(request, provider.GetTranscriptAroundAsync, cancellationToken);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = [OperationFailed])]
    public ValueTask<AgentTranscriptToolDetailResult> TranscriptToolDetailAsync(AgentTranscriptToolDetailRequest request, CancellationToken cancellationToken = default)
        => Invoke<Domain.AgentTranscriptToolDetailRequest, Runtime.AgentTranscriptToolDetailResult, AgentTranscriptToolDetailResult>(request, provider.GetTranscriptToolDetailAsync, cancellationToken);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = [OperationFailed])]
    public IAsyncEnumerable<AgentRuntimeChange> ChangesAsync(AgentChangeSubscription request, CancellationToken cancellationToken = default)
        => Stream<Runtime.AgentChangeSubscription, Runtime.AgentRuntimeChange, AgentRuntimeChange>(request, provider.WatchChangesAsync, cancellationToken);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = [OperationFailed])]
    public IAsyncEnumerable<HistorySearchStatus> HistoryStatusAsync(HistorySearchStatusSubscription request, CancellationToken cancellationToken = default)
        => Stream<History.HistorySearchStatusSubscription, History.HistorySearchStatus, HistorySearchStatus>(request, provider.WatchHistoryStatusAsync, cancellationToken);

    private static async ValueTask<TValue> Invoke<TRequest, TResponse, TValue>(object request,
        Func<TRequest, CancellationToken, ValueTask<TResponse>> invoke, CancellationToken token)
        where TRequest : class where TResponse : class where TValue : class
    {
        try { return OwnerValueMapper.Convert<TValue>(await invoke(OwnerValueMapper.Convert<TRequest>(request), token).ConfigureAwait(false)); }
        catch (Runtime.AgentOwnerOperationException exception) { throw Map(exception); }
    }

    private static async IAsyncEnumerable<TValue> Stream<TRequest, TResponse, TValue>(object request,
        Func<TRequest, CancellationToken, IAsyncEnumerable<TResponse>> subscribe, [EnumeratorCancellation] CancellationToken token)
        where TRequest : class where TResponse : class where TValue : class
    {
        await using var events = subscribe(OwnerValueMapper.Convert<TRequest>(request), token).GetAsyncEnumerator(token);
        while (await MoveNext(events).ConfigureAwait(false)) yield return OwnerValueMapper.Convert<TValue>(events.Current);
    }

    private static async ValueTask<bool> MoveNext<T>(IAsyncEnumerator<T> events)
    {
        try { return await events.MoveNextAsync().ConfigureAwait(false); }
        catch (Runtime.AgentOwnerOperationException exception) { throw Map(exception); }
    }

    private static SunderRpcException Map(Runtime.AgentOwnerOperationException exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message) ? "The requested Agent operation failed." : exception.Message;
        return new(new(SunderRpcErrorKind.Domain, OperationFailed, message.Length > 2048 ? message[..2048] : message));
    }
}
