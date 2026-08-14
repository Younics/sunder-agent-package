using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Package.Agent.OwnerProtocol.Generated;

namespace Sunder.Package.Agent.Runtime;

internal sealed record AgentOwnerRpcOperation<TRequest, TResponse>(
    Func<Wire.AgentRuntimeClient, TRequest, CancellationToken, ValueTask<TResponse>> InvokeAsync)
    where TRequest : class
    where TResponse : class;

internal sealed record AgentOwnerRpcStream<TRequest, TEvent>(
    Func<Wire.AgentRuntimeClient, TRequest, CancellationToken, IAsyncEnumerable<TEvent>> SubscribeAsync)
    where TRequest : class
    where TEvent : class;

internal interface IAgentOwnerClient
{
    ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
        AgentOwnerRpcOperation<TRequest, TResponse> operation,
        TRequest request,
        CancellationToken cancellationToken)
        where TRequest : class
        where TResponse : class;

    IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
        AgentOwnerRpcStream<TRequest, TEvent> stream,
        TRequest request,
        CancellationToken cancellationToken)
        where TRequest : class
        where TEvent : class;
}

internal interface IAgentOwnerRuntimeProvider
{
    ValueTask<AgentChatSnapshotProjection> GetChatSnapshotAsync(
        AgentChatSnapshotRequest request,
        CancellationToken cancellationToken);

    ValueTask<AgentDashboardProjection> GetDashboardAsync(
        AgentDashboardRequest request,
        CancellationToken cancellationToken);

    ValueTask<AgentTranscriptPage> GetTranscriptPageAsync(
        AgentTranscriptPageRequest request,
        CancellationToken cancellationToken);

    ValueTask<AgentCatalogProjection> GetCatalogAsync(
        AgentCatalogRequest request,
        CancellationToken cancellationToken);

    ValueTask<AgentProfileCommandResult> ExecuteProfileCommandAsync(
        AgentProfileCommand request,
        CancellationToken cancellationToken);

    ValueTask<AgentWorkspaceCommandResult> ExecuteWorkspaceCommandAsync(
        AgentWorkspaceCommand request,
        CancellationToken cancellationToken);

    ValueTask<AgentSessionCommandResult> ExecuteSessionCommandAsync(
        AgentSessionCommand request,
        CancellationToken cancellationToken);

    ValueTask<AgentRunCommandResult> ExecuteRunCommandAsync(
        AgentRunCommand request,
        CancellationToken cancellationToken);

    ValueTask<AgentRunCommandStatusResult> GetRunStatusAsync(
        AgentRunCommandStatusRequest request,
        CancellationToken cancellationToken);

    ValueTask<AgentPermissionProjection> ExecutePermissionCommandAsync(
        AgentPermissionCommand request,
        CancellationToken cancellationToken);

    ValueTask<AgentAttachmentTransferResult> TransferAttachmentAsync(
        AgentAttachmentTransferRequest request,
        CancellationToken cancellationToken);

    ValueTask<HistorySearchResponse> SearchHistoryAsync(
        HistorySearchRequest request,
        CancellationToken cancellationToken);

    ValueTask<HistorySearchState> GetHistoryStateAsync(
        HistorySearchStateRequest request,
        CancellationToken cancellationToken);

    ValueTask<HistorySearchCommandResult> ExecuteHistoryCommandAsync(
        HistorySearchCommand request,
        CancellationToken cancellationToken);

    ValueTask<AgentTranscriptAroundTurnPage> GetTranscriptAroundAsync(
        AgentTranscriptAroundTurnRequest request,
        CancellationToken cancellationToken);

    ValueTask<AgentTranscriptToolDetailResponse> GetTranscriptToolDetailAsync(
        AgentTranscriptToolDetailRequest request,
        CancellationToken cancellationToken);

    IAsyncEnumerable<AgentRuntimeChange> WatchChangesAsync(
        AgentChangeSubscription request,
        CancellationToken cancellationToken);

    IAsyncEnumerable<HistorySearchStatus> WatchHistoryStatusAsync(
        HistorySearchStatusSubscription request,
        CancellationToken cancellationToken);
}

internal static class AgentOwnerRuntimeRpc
{
    internal const string ContractId = "sunder.agent.owner.runtime";
    internal const string ContractVersion = "2.0.0";
    internal const string ProviderId = "agent.owner.runtime";

    private const string OperationFailedCode = "agent.owner.operation-failed";
    private const string DescriptorResource =
        "Sunder.Package.Agent.OwnerProtocol.Contracts.agent-owner-runtime.rpc.json";

    private static readonly JsonSerializerOptions MapperOptions = CreateMapperOptions();
    private static readonly Lazy<SunderRpcContractDescriptor> ContractDescriptor =
        new(LoadDescriptor, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static SunderRpcContractDescriptor Descriptor => ContractDescriptor.Value;

    internal static ISunderRpcServiceHandler CreateHandler(IAgentOwnerRuntimeProvider provider)
        => new Wire.SunderAgentOwnerRuntimeRpcHandler(new ProviderAdapter(provider));

    internal static AgentOwnerRpcOperation<TRequest, TResponse> CreateOperation<
        TRequest,
        TResponse,
        TWireRequest,
        TWireResponse>(
        Func<Wire.AgentRuntimeClient, TWireRequest, CancellationToken, ValueTask<TWireResponse>> invoke)
        where TRequest : class
        where TResponse : class
        where TWireRequest : class
        where TWireResponse : class
        => new(async (client, request, cancellationToken) =>
            Convert<TResponse>(await invoke(
                client,
                Convert<TWireRequest>(request),
                cancellationToken).ConfigureAwait(false)));

    internal static AgentOwnerRpcStream<TRequest, TEvent> CreateStream<
        TRequest,
        TEvent,
        TWireRequest,
        TWireEvent>(
        Func<Wire.AgentRuntimeClient, TWireRequest, CancellationToken, IAsyncEnumerable<TWireEvent>> subscribe)
        where TRequest : class
        where TEvent : class
        where TWireRequest : class
        where TWireEvent : class
        => new((client, request, cancellationToken) => ConvertStream<TEvent, TWireEvent>(
            subscribe(client, Convert<TWireRequest>(request), cancellationToken),
            cancellationToken));

    private static async IAsyncEnumerable<TEvent> ConvertStream<TEvent, TWireEvent>(
        IAsyncEnumerable<TWireEvent> source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TEvent : class
        where TWireEvent : class
    {
        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return Convert<TEvent>(item);
        }
    }

    private static TTarget Convert<TTarget>(object source)
        where TTarget : class
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(source, source.GetType(), MapperOptions);
        return JsonSerializer.Deserialize<TTarget>(bytes, MapperOptions)
               ?? throw new InvalidOperationException(
                   $"RPC value '{source.GetType().Name}' could not be mapped to '{typeof(TTarget).Name}'.");
    }

    private static JsonSerializerOptions CreateMapperOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new SunderRpcV2JsonConverterFactory());
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        return options;
    }

    internal static bool ShouldMap(Exception exception)
        => exception is AgentOwnerOperationException;

    private static SunderRpcException Map(AgentOwnerOperationException exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? "The requested Agent operation failed."
            : exception.Message;
        if (message.Length > 2_048)
        {
            message = message[..2_048];
        }

        return new SunderRpcException(new SunderRpcError(
            SunderRpcErrorKind.Domain,
            OperationFailedCode,
            message));
    }

    private static SunderRpcContractDescriptor LoadDescriptor()
    {
        var assembly = typeof(AgentOwnerRuntimeRpc).Assembly;
        using var stream = assembly.GetManifestResourceStream(DescriptorResource)
            ?? throw new InvalidOperationException("The Agent owner RPC descriptor is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var descriptor = SunderRpcContractDescriptor.Parse(memory.ToArray());
        if (descriptor.ContractId != ContractId || descriptor.Version != ContractVersion)
        {
            throw new InvalidDataException("The Agent owner RPC descriptor identity is invalid.");
        }
        return descriptor;
    }

    private sealed class ProviderAdapter(IAgentOwnerRuntimeProvider provider) : Wire.IAgentRuntimeProvider
    {
        public ValueTask<Wire.AgentAttachmentTransferResult> AttachmentTransferAsync(
            SunderRpcInvocationContext context,
            Wire.AgentAttachmentTransferRequest request,
            CancellationToken cancellationToken = default)
            => InvokeAsync<AgentAttachmentTransferRequest, AgentAttachmentTransferResult, Wire.AgentAttachmentTransferResult>(
                request, provider.TransferAttachmentAsync, cancellationToken);

        public ValueTask<Wire.AgentCatalogProjection> CatalogAsync(
            SunderRpcInvocationContext context,
            Wire.AgentCatalogRequest request,
            CancellationToken cancellationToken = default)
            => InvokeAsync<AgentCatalogRequest, AgentCatalogProjection, Wire.AgentCatalogProjection>(
                request, provider.GetCatalogAsync, cancellationToken);

        public IAsyncEnumerable<Wire.AgentRuntimeChange> ChangesAsync(
            SunderRpcInvocationContext context,
            Wire.AgentChangeSubscription request,
            CancellationToken cancellationToken = default)
            => StreamAsync<AgentChangeSubscription, AgentRuntimeChange, Wire.AgentRuntimeChange>(
                request, provider.WatchChangesAsync, cancellationToken);

        public ValueTask<Wire.AgentChatSnapshotProjection> ChatSnapshotAsync(
            SunderRpcInvocationContext context,
            Wire.AgentChatSnapshotRequest request,
            CancellationToken cancellationToken = default)
            => InvokeAsync<AgentChatSnapshotRequest, AgentChatSnapshotProjection, Wire.AgentChatSnapshotProjection>(
                request, provider.GetChatSnapshotAsync, cancellationToken);

        public ValueTask<Wire.AgentDashboardProjection> DashboardAsync(
            SunderRpcInvocationContext context,
            Wire.AgentDashboardRequest request,
            CancellationToken cancellationToken = default)
            => InvokeAsync<AgentDashboardRequest, AgentDashboardProjection, Wire.AgentDashboardProjection>(
                request, provider.GetDashboardAsync, cancellationToken);

        public ValueTask<Wire.HistorySearchCommandResult> HistoryCommandAsync(
            SunderRpcInvocationContext context,
            Wire.HistorySearchCommand request,
            CancellationToken cancellationToken = default)
            => InvokeAsync<HistorySearchCommand, HistorySearchCommandResult, Wire.HistorySearchCommandResult>(
                request, provider.ExecuteHistoryCommandAsync, cancellationToken);

        public ValueTask<Wire.HistorySearchResponse> HistorySearchAsync(
            SunderRpcInvocationContext context,
            Wire.HistorySearchRequest request,
            CancellationToken cancellationToken = default)
            => InvokeAsync<HistorySearchRequest, HistorySearchResponse, Wire.HistorySearchResponse>(
                request, provider.SearchHistoryAsync, cancellationToken);

        public ValueTask<Wire.HistorySearchState> HistoryStateAsync(
            SunderRpcInvocationContext context,
            Wire.HistorySearchStateRequest request,
            CancellationToken cancellationToken = default)
            => InvokeAsync<HistorySearchStateRequest, HistorySearchState, Wire.HistorySearchState>(
                request, provider.GetHistoryStateAsync, cancellationToken);

        public IAsyncEnumerable<Wire.HistorySearchStatus> HistoryStatusAsync(
            SunderRpcInvocationContext context,
            Wire.HistorySearchStatusSubscription request,
            CancellationToken cancellationToken = default)
            => StreamAsync<HistorySearchStatusSubscription, HistorySearchStatus, Wire.HistorySearchStatus>(
                request, provider.WatchHistoryStatusAsync, cancellationToken);

        public ValueTask<Wire.AgentPermissionProjection> PermissionCommandAsync(
            SunderRpcInvocationContext context,
            Wire.AgentPermissionCommand request,
            CancellationToken cancellationToken = default)
            => InvokeAsync<AgentPermissionCommand, AgentPermissionProjection, Wire.AgentPermissionProjection>(
                request, provider.ExecutePermissionCommandAsync, cancellationToken);

        public ValueTask<Wire.AgentProfileCommandResult> ProfileCommandAsync(
            SunderRpcInvocationContext context,
            Wire.AgentProfileCommand request,
            CancellationToken cancellationToken = default)
            => InvokeAsync<AgentProfileCommand, AgentProfileCommandResult, Wire.AgentProfileCommandResult>(
                request, provider.ExecuteProfileCommandAsync, cancellationToken);

        public ValueTask<Wire.AgentRunCommandResult> RunCommandAsync(
            SunderRpcInvocationContext context,
            Wire.AgentRunCommand request,
            CancellationToken cancellationToken = default)
            => InvokeAsync<AgentRunCommand, AgentRunCommandResult, Wire.AgentRunCommandResult>(
                request, provider.ExecuteRunCommandAsync, cancellationToken);

        public ValueTask<Wire.AgentRunCommandStatusResult> RunStatusAsync(
            SunderRpcInvocationContext context,
            Wire.AgentRunCommandStatusRequest request,
            CancellationToken cancellationToken = default)
            => InvokeAsync<AgentRunCommandStatusRequest, AgentRunCommandStatusResult, Wire.AgentRunCommandStatusResult>(
                request, provider.GetRunStatusAsync, cancellationToken);

        public ValueTask<Wire.AgentSessionCommandResult> SessionCommandAsync(
            SunderRpcInvocationContext context,
            Wire.AgentSessionCommand request,
            CancellationToken cancellationToken = default)
            => InvokeAsync<AgentSessionCommand, AgentSessionCommandResult, Wire.AgentSessionCommandResult>(
                request, provider.ExecuteSessionCommandAsync, cancellationToken);

        public ValueTask<Wire.AgentTranscriptAroundTurnPage> TranscriptAroundAsync(
            SunderRpcInvocationContext context,
            Wire.AgentTranscriptAroundTurnRequest request,
            CancellationToken cancellationToken = default)
            => InvokeAsync<AgentTranscriptAroundTurnRequest, AgentTranscriptAroundTurnPage, Wire.AgentTranscriptAroundTurnPage>(
                request, provider.GetTranscriptAroundAsync, cancellationToken);

        public ValueTask<Wire.AgentTranscriptPage> TranscriptPageAsync(
            SunderRpcInvocationContext context,
            Wire.AgentTranscriptPageRequest request,
            CancellationToken cancellationToken = default)
            => InvokeAsync<AgentTranscriptPageRequest, AgentTranscriptPage, Wire.AgentTranscriptPage>(
                request, provider.GetTranscriptPageAsync, cancellationToken);

        public ValueTask<Wire.AgentTranscriptToolDetailResponse> TranscriptToolDetailAsync(
            SunderRpcInvocationContext context,
            Wire.AgentTranscriptToolDetailRequest request,
            CancellationToken cancellationToken = default)
            => InvokeAsync<AgentTranscriptToolDetailRequest, AgentTranscriptToolDetailResponse, Wire.AgentTranscriptToolDetailResponse>(
                request, provider.GetTranscriptToolDetailAsync, cancellationToken);

        public ValueTask<Wire.AgentWorkspaceCommandResult> WorkspaceCommandAsync(
            SunderRpcInvocationContext context,
            Wire.AgentWorkspaceCommand request,
            CancellationToken cancellationToken = default)
            => InvokeAsync<AgentWorkspaceCommand, AgentWorkspaceCommandResult, Wire.AgentWorkspaceCommandResult>(
                request, provider.ExecuteWorkspaceCommandAsync, cancellationToken);

        private static async ValueTask<TWireResponse> InvokeAsync<TRequest, TResponse, TWireResponse>(
            object wireRequest,
            Func<TRequest, CancellationToken, ValueTask<TResponse>> invoke,
            CancellationToken cancellationToken)
            where TRequest : class
            where TResponse : class
            where TWireResponse : class
        {
            try
            {
                var response = await invoke(
                    Convert<TRequest>(wireRequest),
                    cancellationToken).ConfigureAwait(false);
                return Convert<TWireResponse>(response);
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                throw Map((AgentOwnerOperationException)exception);
            }
        }

        private static async IAsyncEnumerable<TWireEvent> StreamAsync<TRequest, TEvent, TWireEvent>(
            object wireRequest,
            Func<TRequest, CancellationToken, IAsyncEnumerable<TEvent>> subscribe,
            [EnumeratorCancellation] CancellationToken cancellationToken)
            where TRequest : class
            where TEvent : class
            where TWireEvent : class
        {
            await using var enumerator = subscribe(
                Convert<TRequest>(wireRequest),
                cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (await MoveNextAsync(enumerator, cancellationToken).ConfigureAwait(false))
            {
                yield return Convert<TWireEvent>(enumerator.Current);
            }
        }

        private static async ValueTask<bool> MoveNextAsync<T>(
            IAsyncEnumerator<T> enumerator,
            CancellationToken cancellationToken)
        {
            try
            {
                return await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                throw Map((AgentOwnerOperationException)exception);
            }
        }
    }

    private sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
            => DateTimeOffset.Parse(
                reader.GetString() ?? throw new JsonException("A UTC date-time value is required."),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);

        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToUniversalTime().ToString(
                "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
                CultureInfo.InvariantCulture));
    }
}

internal sealed class AgentOwnerOperationException(
    string message,
    Exception? innerException = null) : InvalidOperationException(message, innerException);
