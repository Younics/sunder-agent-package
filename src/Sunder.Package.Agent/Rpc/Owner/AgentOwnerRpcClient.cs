using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Rpc.OwnerApp;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Generated.Rpc.OwnerRuntime;

namespace Sunder.Package.Agent.Runtime;

// Role-local semantic adapter: presentation operations keep their domain values;
// the generated owner facade supplies all wire identity, selection and dispatch.
internal sealed class AgentOwnerRpcClient(ISunderRpcClient client) : IAgentOwnerClient
{
    private readonly OwnerRuntimeGateway _gateway = new(client);
    private readonly AgentTransportProfile _proposal = client.ResourceProfile is { } profile
        ? new(profile.MaximumRpcValueBytes, profile.MaximumRpcValueBytes, profile.MaximumRpcValueBytes, profile.MaximumContentReferenceBytes, true)
        : AgentTransportProfile.StandardFallback;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new SunderRpcJsonConverterFactory(), new UtcConverter() },
    };

    public AgentTransportProfile TransportProfile => _gateway.NegotiatedProfile is { } profile ? Convert<AgentTransportProfile>(profile) : _proposal;
    public async ValueTask<AgentTransportProfile> GetTransportProfileAsync(CancellationToken token)
        => Convert<AgentTransportProfile>(await _gateway.GetTransportProfileAsync(token).ConfigureAwait(false));

    public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(AgentOwnerRpcOperation<TRequest, TResponse> operation, TRequest request, CancellationToken token)
        where TRequest : class where TResponse : class
    {
        if (ReferenceEquals(operation, AgentRuntimeOperations.TransportProfile)) return Invoke<Wire.AgentTransportProfileRequest, Wire.AgentTransportProfile, TResponse>(request, static (c, r, t) => c.TransportProfileAsync(r, t), token);
        if (ReferenceEquals(operation, AgentRuntimeOperations.ChatSnapshot)) return Invoke<Wire.AgentChatSnapshotRequest, Wire.AgentChatSnapshotProjection, TResponse>(request, static (c, r, t) => c.ChatSnapshotAsync(r, t), token);
        if (ReferenceEquals(operation, AgentRuntimeOperations.Dashboard)) return Invoke<Wire.AgentDashboardRequest, Wire.AgentDashboardProjection, TResponse>(request, static (c, r, t) => c.DashboardAsync(r, t), token);
        if (ReferenceEquals(operation, AgentRuntimeOperations.Transcript)) return Invoke<Wire.AgentTranscriptPageRequest, Wire.AgentTranscriptPage, TResponse>(request, static (c, r, t) => c.TranscriptPageAsync(r, t), token);
        if (ReferenceEquals(operation, AgentRuntimeOperations.Catalog)) return Invoke<Wire.AgentCatalogRequest, Wire.AgentCatalogProjection, TResponse>(request, static (c, r, t) => c.CatalogAsync(r, t), token);
        if (ReferenceEquals(operation, AgentRuntimeOperations.Profiles)) return Invoke<Wire.AgentProfileCommand, Wire.AgentProfileCommandResult, TResponse>(request, static (c, r, t) => c.ProfileCommandAsync(r, t), token);
        if (ReferenceEquals(operation, AgentRuntimeOperations.Workspaces)) return Invoke<Wire.AgentWorkspaceCommand, Wire.AgentWorkspaceCommandResult, TResponse>(request, static (c, r, t) => c.WorkspaceCommandAsync(r, t), token);
        if (ReferenceEquals(operation, AgentRuntimeOperations.SessionCommands)) return Invoke<Wire.AgentSessionCommand, Wire.AgentSessionCommandResult, TResponse>(request, static (c, r, t) => c.SessionCommandAsync(r, t), token);
        if (ReferenceEquals(operation, AgentRuntimeOperations.Runs)) return Invoke<Wire.AgentRunCommand, Wire.AgentRunCommandResult, TResponse>(request, static (c, r, t) => c.RunCommandAsync(r, t), token);
        if (ReferenceEquals(operation, AgentRuntimeOperations.RunStatus)) return Invoke<Wire.AgentRunCommandStatusRequest, Wire.AgentRunCommandStatusResult, TResponse>(request, static (c, r, t) => c.RunStatusAsync(r, t), token);
        if (ReferenceEquals(operation, AgentRuntimeOperations.Permissions)) return Invoke<Wire.AgentPermissionCommand, Wire.AgentPermissionProjection, TResponse>(request, static (c, r, t) => c.PermissionCommandAsync(r, t), token);
        if (ReferenceEquals(operation, AgentRuntimeOperations.AttachmentUploads)) return Invoke<Wire.AgentAttachmentContentUploadRequest, Wire.AgentAttachmentContentUploadResult, TResponse>(request, static (c, r, t) => c.AttachmentUploadAsync(r, t), token);
        if (ReferenceEquals(operation, AgentRuntimeOperations.AttachmentDownloads)) return Invoke<Wire.AgentAttachmentContentDownloadRequest, Wire.AgentAttachmentContentDownloadResult, TResponse>(request, static (c, r, t) => c.AttachmentDownloadAsync(r, t), token);
        if (ReferenceEquals(operation, AgentRuntimeOperations.AttachmentDiscards)) return Invoke<Wire.AgentAttachmentContentDiscardRequest, Wire.AgentAttachmentContentDiscardResult, TResponse>(request, static (c, r, t) => c.AttachmentDiscardAsync(r, t), token);
        if (ReferenceEquals(operation, AgentRuntimeOperations.HistorySearch)) return Invoke<Wire.HistorySearchRequest, Wire.HistorySearchResponse, TResponse>(request, static (c, r, t) => c.HistorySearchAsync(r, t), token);
        if (ReferenceEquals(operation, AgentRuntimeOperations.HistoryState)) return Invoke<Wire.HistorySearchStateRequest, Wire.HistorySearchState, TResponse>(request, static (c, r, t) => c.HistoryStateAsync(r, t), token);
        if (ReferenceEquals(operation, AgentRuntimeOperations.HistoryCommands)) return Invoke<Wire.HistorySearchCommand, Wire.HistorySearchCommandResult, TResponse>(request, static (c, r, t) => c.HistoryCommandAsync(r, t), token);
        if (ReferenceEquals(operation, AgentRuntimeOperations.TranscriptAround)) return Invoke<Wire.AgentTranscriptAroundTurnRequest, Wire.AgentTranscriptAroundTurnPage, TResponse>(request, static (c, r, t) => c.TranscriptAroundAsync(r, t), token);
        if (ReferenceEquals(operation, AgentRuntimeOperations.TranscriptToolDetail)) return Invoke<Wire.AgentTranscriptToolDetailRequest, Wire.AgentTranscriptToolDetailResult, TResponse>(request, static (c, r, t) => c.TranscriptToolDetailAsync(r, t), token);
        return ValueTask.FromException<TResponse>(new InvalidOperationException("Unknown Agent owner operation."));
    }

    public IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(AgentOwnerRpcStream<TRequest, TEvent> operation, TRequest request, CancellationToken token)
        where TRequest : class where TEvent : class
    {
        if (ReferenceEquals(operation, AgentRuntimeOperations.Changes)) return Subscribe<Wire.AgentChangeSubscription, Wire.AgentRuntimeChange, TEvent>(request, static (c, r, t) => c.ChangesAsync(r, t), token);
        if (ReferenceEquals(operation, AgentRuntimeOperations.HistoryStatus)) return Subscribe<Wire.HistorySearchStatusSubscription, Wire.HistorySearchStatus, TEvent>(request, static (c, r, t) => c.HistoryStatusAsync(r, t), token);
        throw new InvalidOperationException("Unknown Agent owner stream.");
    }

    public async ValueTask<AgentAttachmentUploadHandle> UploadAttachmentAsync(AgentAttachmentUploadRequest upload, CancellationToken token)
        => Convert<AgentAttachmentUploadHandle>(await _gateway.UploadAsync(upload, token).ConfigureAwait(false));
    public ValueTask<byte[]> DownloadAttachmentAsync(AgentAttachmentMetadata metadata, CancellationToken token)
        => _gateway.DownloadAsync(Convert<Wire.AgentAttachmentMetadata>(metadata), token);
    public ValueTask DiscardAttachmentAsync(AgentAttachmentUploadHandle handle, CancellationToken token)
        => _gateway.DiscardAsync(Convert<Wire.AgentAttachmentUploadHandle>(handle), token);

    private async ValueTask<TResponse> Invoke<TWireRequest, TWireResponse, TResponse>(object request,
        Func<Wire.AgentRuntimeClient, TWireRequest, CancellationToken, ValueTask<TWireResponse>> operation, CancellationToken token)
        where TWireRequest : class where TWireResponse : class where TResponse : class
        => Convert<TResponse>(await _gateway.InvokeAsync(Convert<TWireRequest>(request), operation, token).ConfigureAwait(false));

    private async IAsyncEnumerable<TEvent> Subscribe<TWireRequest, TWireEvent, TEvent>(object request,
        Func<Wire.AgentRuntimeClient, TWireRequest, CancellationToken, IAsyncEnumerable<TWireEvent>> operation,
        [EnumeratorCancellation] CancellationToken token) where TWireRequest : class where TWireEvent : class where TEvent : class
    {
        await foreach (var item in _gateway.SubscribeAsync(Convert<TWireRequest>(request), operation, token).WithCancellation(token).ConfigureAwait(false))
            yield return Convert<TEvent>(item);
    }

    private static T Convert<T>(object value) where T : class
        => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), Json), Json)
            ?? throw new InvalidOperationException("An Agent owner value could not be mapped.");

    private sealed class UtcConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.GetDateTimeOffset();
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
    }
}

internal sealed partial class AgentAppRuntimeGateway
{
    private static bool IsRuntimeAvailabilityFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is AgentPackageUnavailableException || exception is OperationCanceledException && cancellationToken.IsCancellationRequested) return false;
        if (exception is SunderRpcException rpcException)
            return rpcException.Error.Kind is SunderRpcErrorKind.Unavailable or SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.NotFound;
        return exception is HttpRequestException or System.Net.Sockets.SocketException or TimeoutException or OperationCanceledException;
    }
    private void ThrowIfUnavailable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AgentAppRuntimeGateway));
    }
}
