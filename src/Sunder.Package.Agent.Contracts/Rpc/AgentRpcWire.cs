using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Protocol;

public sealed record AgentRpcEmpty;

public sealed class AgentRpcServiceHandler : ISunderRpcServiceHandler, ISunderRpcDeclaredErrorMetadata
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> EmptyDeclaredErrors =
        new ReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(
            new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal));
    private static readonly TimeSpan ExtendedDeadlineEnvelope = TimeSpan.FromMinutes(35);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new SunderRpcV2JsonConverterFactory(),
            new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false),
        },
    };
    private readonly IReadOnlyDictionary<(string ServiceId, string MethodId), UnaryHandler> _unary;
    private readonly IReadOnlyDictionary<(string ServiceId, string MethodId), StreamHandler> _streams;

    private AgentRpcServiceHandler(
        IReadOnlyDictionary<(string ServiceId, string MethodId), UnaryHandler> unary,
        IReadOnlyDictionary<(string ServiceId, string MethodId), StreamHandler> streams,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> declaredErrors)
    {
        _unary = unary;
        _streams = streams;
        DeclaredErrors = declaredErrors;
    }

    public static Builder Create() => new();

    internal static Builder Create(SunderRpcContractDescriptor descriptor) => new(descriptor);

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> DeclaredErrors { get; }

    public ValueTask<JsonElement> InvokeUnaryAsync(
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        CancellationToken cancellationToken = default)
        => _unary.TryGetValue((serviceId, methodId), out var handler)
            ? handler(context, request, cancellationToken)
            : ValueTask.FromException<JsonElement>(NotFound(serviceId, methodId));

    public IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        CancellationToken cancellationToken = default)
        => _streams.TryGetValue((serviceId, methodId), out var handler)
            ? handler(context, request, cancellationToken)
            : ThrowStream(NotFound(serviceId, methodId), cancellationToken);

    public sealed class Builder
    {
        private readonly Dictionary<(string ServiceId, string MethodId), UnaryHandler> _unary = [];
        private readonly Dictionary<(string ServiceId, string MethodId), StreamHandler> _streams = [];
        private readonly SunderRpcContractDescriptor? _descriptor;

        public Builder()
        {
        }

        internal Builder(SunderRpcContractDescriptor descriptor)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            _descriptor = descriptor;
        }

        public Builder AddUnary<TRequest, TResponse>(
            string serviceId,
            string methodId,
            Func<SunderRpcInvocationContext, TRequest, CancellationToken, ValueTask<TResponse>> handler)
            where TRequest : notnull
            where TResponse : notnull
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (!_unary.TryAdd((serviceId, methodId), async (context, request, cancellationToken) =>
                JsonSerializer.SerializeToElement(
                    await handler(
                        context,
                        Deserialize<TRequest>(request),
                        cancellationToken).ConfigureAwait(false),
                    JsonOptions)))
            {
                throw new InvalidOperationException($"Duplicate Agent RPC unary method '{serviceId}/{methodId}'.");
            }
            return this;
        }

        public Builder AddNullableUnary<TRequest, TResponse>(
            string serviceId,
            string methodId,
            Func<SunderRpcInvocationContext, TRequest, CancellationToken, ValueTask<TResponse?>> handler)
            where TRequest : notnull
            where TResponse : class
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (!_unary.TryAdd((serviceId, methodId), async (context, request, cancellationToken) =>
                JsonSerializer.SerializeToElement<TResponse?>(
                    await handler(
                        context,
                        Deserialize<TRequest>(request),
                        cancellationToken).ConfigureAwait(false),
                    JsonOptions)))
            {
                throw new InvalidOperationException($"Duplicate Agent RPC unary method '{serviceId}/{methodId}'.");
            }
            return this;
        }

        public Builder AddStream<TRequest, TEvent>(
            string serviceId,
            string methodId,
            Func<SunderRpcInvocationContext, TRequest, CancellationToken, IAsyncEnumerable<TEvent>> handler)
            where TRequest : notnull
            where TEvent : notnull
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (!_streams.TryAdd((serviceId, methodId), (context, request, cancellationToken) =>
                SerializeStream(handler(context, Deserialize<TRequest>(request), cancellationToken), cancellationToken)))
            {
                throw new InvalidOperationException($"Duplicate Agent RPC stream method '{serviceId}/{methodId}'.");
            }
            return this;
        }

        public AgentRpcServiceHandler Build()
        {
            var unary = new ReadOnlyDictionary<(string ServiceId, string MethodId), UnaryHandler>(
                new Dictionary<(string ServiceId, string MethodId), UnaryHandler>(_unary));
            var streams = new ReadOnlyDictionary<(string ServiceId, string MethodId), StreamHandler>(
                new Dictionary<(string ServiceId, string MethodId), StreamHandler>(_streams));
            if (_descriptor is null)
            {
                return new(unary, streams, EmptyDeclaredErrors);
            }

            ValidateRoutes(_descriptor);
            return new(unary, streams, CreateDeclaredErrorMap(_descriptor));
        }

        private void ValidateRoutes(SunderRpcContractDescriptor descriptor)
        {
            var expected = descriptor.Services
                .SelectMany(service => service.Methods.Select(method =>
                    new KeyValuePair<(string ServiceId, string MethodId), SunderRpcMethodKind>(
                        (service.ServiceId, method.MethodId),
                        method.Kind)))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value);
            var failures = new List<string>();
            foreach (var (route, kind) in expected)
            {
                var hasUnary = _unary.ContainsKey(route);
                var hasStream = _streams.ContainsKey(route);
                if (kind == SunderRpcMethodKind.Unary ? !hasUnary || hasStream : hasUnary || !hasStream)
                {
                    failures.Add($"{route.ServiceId}/{route.MethodId} must be {FormatKind(kind)}");
                }
            }

            foreach (var route in _unary.Keys.Concat(_streams.Keys).Distinct())
            {
                if (!expected.ContainsKey(route))
                {
                    failures.Add($"{route.ServiceId}/{route.MethodId} is not declared");
                }
            }

            if (failures.Count != 0)
            {
                throw new InvalidOperationException(
                    $"Agent RPC handler routes do not match contract '{descriptor.ContractId}': "
                    + string.Join(", ", failures.Order(StringComparer.Ordinal))
                    + ".");
            }
        }

        private static string FormatKind(SunderRpcMethodKind kind)
            => kind == SunderRpcMethodKind.Unary ? "unary" : "server-stream";
    }

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> CreateDeclaredErrorMap(
        SunderRpcContractDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new ReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(
            descriptor.Services.ToDictionary(
                static service => service.ServiceId,
                static service => (IReadOnlyDictionary<string, IReadOnlyList<string>>)
                    new ReadOnlyDictionary<string, IReadOnlyList<string>>(
                        service.Methods.ToDictionary(
                            static method => method.MethodId,
                            static method => method.Errors,
                            StringComparer.Ordinal)),
                StringComparer.Ordinal));
    }

    public static async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
        ISunderRpcClient client,
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        TRequest request,
        CancellationToken cancellationToken = default)
        where TRequest : notnull
        where TResponse : notnull
        => await InvokeAsync<TRequest, TResponse>(
            client,
            endpoint,
            serviceId,
            methodId,
            request,
            options: null,
            cancellationToken).ConfigureAwait(false);

    internal static async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
        ISunderRpcClient client,
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        TRequest request,
        SunderRpcCallOptions? options,
        CancellationToken cancellationToken = default)
        where TRequest : notnull
        where TResponse : notnull
    {
        var response = await client.InvokeAsync(
                endpoint,
                serviceId,
                methodId,
                JsonSerializer.SerializeToElement(request, JsonOptions),
                options,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return Deserialize<TResponse>(
            response,
            "agent.rpc.response.null",
            "The Agent RPC response deserialized to null.");
    }

    internal static SunderRpcCallOptions CreateExtendedCallOptions()
        => new(DateTimeOffset.UtcNow + ExtendedDeadlineEnvelope);

    public static async ValueTask<TResponse?> InvokeNullableAsync<TRequest, TResponse>(
        ISunderRpcClient client,
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        TRequest request,
        CancellationToken cancellationToken = default)
        where TRequest : notnull
        where TResponse : class
    {
        var response = await client.InvokeAsync(
                endpoint,
                serviceId,
                methodId,
                JsonSerializer.SerializeToElement(request, JsonOptions),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return response.ValueKind == JsonValueKind.Null
            ? null
            : response.Deserialize<TResponse>(JsonOptions)
              ?? throw Protocol(
                  "agent.rpc.response.null",
                  "The Agent RPC response deserialized to null.");
    }

    public static IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
        ISunderRpcClient client,
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        TRequest request,
        CancellationToken cancellationToken = default)
        where TRequest : notnull
        where TEvent : notnull
        => SubscribeAsync<TRequest, TEvent>(
            client,
            endpoint,
            serviceId,
            methodId,
            request,
            options: null,
            cancellationToken);

    internal static async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
        ISunderRpcClient client,
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        TRequest request,
        SunderRpcCallOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
        where TRequest : notnull
        where TEvent : notnull
    {
        var requestElement = JsonSerializer.SerializeToElement(request, JsonOptions);
        await foreach (var item in client.SubscribeAsync(
                           endpoint,
                           serviceId,
                           methodId,
                           requestElement,
                           options,
                           cancellationToken: cancellationToken)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return Deserialize<TEvent>(
                item,
                "agent.rpc.event.null",
                "The Agent RPC event deserialized to null.");
        }
    }

    public static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value, JsonOptions);

    internal static SunderRpcOffsetDateTime ToWireTimestamp(DateTimeOffset value)
        => new(value.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            CultureInfo.InvariantCulture));

    internal static DateTimeOffset FromWireTimestamp(SunderRpcOffsetDateTime value)
        => value.TryGetDateTimeOffset(out var result)
            ? result
            : throw Protocol(
                "agent.rpc.timestamp-not-representable",
                "The exact RPC date-time cannot be represented by DateTimeOffset.");

    private static T Deserialize<T>(JsonElement value)
        => Deserialize<T>(
            value,
            "agent.rpc.request.null",
            "The Agent RPC request deserialized to null.");

    private static T Deserialize<T>(JsonElement value, string code, string message)
        => value.Deserialize<T>(JsonOptions)
           ?? throw Protocol(code, message);

    private static async IAsyncEnumerable<JsonElement> SerializeStream<T>(
        IAsyncEnumerable<T> source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return JsonSerializer.SerializeToElement(item, JsonOptions);
        }
    }

    private static async IAsyncEnumerable<JsonElement> ThrowStream(
        Exception exception,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        throw exception;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static SunderRpcException NotFound(string serviceId, string methodId)
        => new(new SunderRpcError(
            SunderRpcErrorKind.NotFound,
            "agent.rpc.method-not-found",
            $"Agent RPC method '{serviceId}/{methodId}' is not implemented by this provider."));

    private static SunderRpcException Protocol(string code, string message)
        => new(new SunderRpcError(SunderRpcErrorKind.Protocol, code, message));

    private delegate ValueTask<JsonElement> UnaryHandler(
        SunderRpcInvocationContext context,
        JsonElement request,
        CancellationToken cancellationToken);

    private delegate IAsyncEnumerable<JsonElement> StreamHandler(
        SunderRpcInvocationContext context,
        JsonElement request,
        CancellationToken cancellationToken);
}
