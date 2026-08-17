using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentRpcWireCompatibilityTests
{
    private const string ExactTimestampText = "2026-07-28T12:34:56.123456789+05:30";
    private const string CanonicalNanosecondJson =
        "{\"timestamp\":\"" + ExactTimestampText + "\",\"value\":1.0000000000000002}";
    private const double Binary64Value = 1.0000000000000002d;
    private static readonly SunderRpcOffsetDateTime ExactTimestamp = new(ExactTimestampText);

    [Fact]
    public async Task Handler_PreservesExactOffsetNanosecondsAndBinary64Values()
    {
        WireProbe? observed = null;
        var handler = AgentRpcServiceHandler.Create()
            .AddUnary<WireProbe, WireProbe>("wire", "echo", (_, request, _) =>
            {
                observed = request;
                return ValueTask.FromResult(request);
            })
            .Build();

        var response = await handler.InvokeUnaryAsync(
            null!,
            "wire",
            "echo",
            Parse(CanonicalNanosecondJson));

        AssertProbe(observed!);
        Assert.Equal(ExactTimestampText, response.GetProperty("timestamp").GetString());
        AssertBinary64(response.GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task Client_PreservesExactOffsetNanosecondsForUnaryAndStreamCalls()
    {
        var client = new RawRpcClient(Parse(CanonicalNanosecondJson));
        var endpoint = new SunderRpcEndpointReference("rpc1_wire_compatibility");
        var request = new WireProbe(ExactTimestamp, Binary64Value);

        var response = await AgentRpcServiceHandler.InvokeAsync<WireProbe, WireProbe>(
            client,
            endpoint,
            "wire",
            "echo",
            request);
        AssertProbe(response);
        AssertRequest(client.LastRequest);

        var events = new List<WireProbe>();
        await foreach (var item in AgentRpcServiceHandler.SubscribeAsync<WireProbe, WireProbe>(
                           client,
                           endpoint,
                           "wire",
                           "watch",
                           request))
        {
            events.Add(item);
        }

        AssertProbe(Assert.Single(events));
        AssertRequest(client.LastRequest);
    }

    [Fact]
    public async Task DateTimeOffsetProjection_RejectsUnrepresentableNanoseconds()
    {
        var handler = AgentRpcServiceHandler.Create()
            .AddUnary<DateTimeOffsetProbe, DateTimeOffsetProbe>("wire", "echo", (_, request, _) =>
                ValueTask.FromResult(request))
            .Build();

        await Assert.ThrowsAsync<JsonException>(() => handler.InvokeUnaryAsync(
            null!,
            "wire",
            "echo",
            Parse("{\"timestamp\":\"2026-07-28T12:34:56.123456789Z\"}")).AsTask());
    }

    [Fact]
    public async Task NullableUnary_UsesJsonNullWithoutAnEnvelope()
    {
        var handler = AgentRpcServiceHandler.Create()
            .AddNullableUnary<AgentRpcEmpty, string>("wire", "none", (_, _, _) =>
                ValueTask.FromResult<string?>(null))
            .Build();
        var response = await handler.InvokeUnaryAsync(
            null!,
            "wire",
            "none",
            JsonSerializer.SerializeToElement(new AgentRpcEmpty()));
        Assert.Equal(JsonValueKind.Null, response.ValueKind);

        var client = new RawRpcClient(Parse("null"));
        var result = await AgentRpcServiceHandler.InvokeNullableAsync<AgentRpcEmpty, string>(
            client,
            new SunderRpcEndpointReference("rpc1_wire_compatibility"),
            "wire",
            "none",
            new AgentRpcEmpty());
        Assert.Null(result);
    }

    [Fact]
    public async Task ChatHandler_PreservesBinary32Options()
    {
        var temperature = float.BitIncrement(0.5f);
        var topP = float.BitDecrement(1f);
        const float frequencyPenalty = -0.75f;
        const float presencePenalty = 2f;
        var request = JsonSerializer.SerializeToElement(new
        {
            providerId = "probe",
            modelId = "probe-model",
            messages = Array.Empty<object>(),
            options = new
            {
                temperature = (double)temperature,
                topP = (double)topP,
                frequencyPenalty = (double)frequencyPenalty,
                presencePenalty = (double)presencePenalty,
                tools = Array.Empty<object>(),
            },
        });
        AssertChatRequestIsSchemaValid(request);

        var provider = new ChatProviderProbe();
        await DrainAsync(AgentChatProviderRpc.CreateHandler(provider), request);

        Assert.Equal(1, provider.Client.CallCount);
        var observed = Assert.IsType<ChatOptions>(provider.Client.ObservedOptions);
        AssertSingleBits(temperature, observed.Temperature);
        AssertSingleBits(topP, observed.TopP);
        AssertSingleBits(frequencyPenalty, observed.FrequencyPenalty);
        AssertSingleBits(presencePenalty, observed.PresencePenalty);
    }

    [Theory]
    [InlineData("temperature")]
    [InlineData("topP")]
    [InlineData("frequencyPenalty")]
    [InlineData("presencePenalty")]
    public async Task ChatHandler_DecodesNumbersUsingBinary32Semantics(string propertyName)
    {
        var request = Parse(
            "{\"providerId\":\"probe\",\"modelId\":\"probe-model\",\"messages\":[],\"options\":{\""
            + propertyName
            + "\":1.0000000000000002,\"tools\":[]}}");
        AssertChatRequestIsSchemaValid(request);

        var provider = new ChatProviderProbe();
        await DrainAsync(AgentChatProviderRpc.CreateHandler(provider), request);

        Assert.Equal(1, provider.Client.CallCount);
        var options = Assert.IsType<ChatOptions>(provider.Client.ObservedOptions);
        var actual = propertyName switch
        {
            "temperature" => options.Temperature,
            "topP" => options.TopP,
            "frequencyPenalty" => options.FrequencyPenalty,
            "presencePenalty" => options.PresencePenalty,
            _ => throw new ArgumentOutOfRangeException(nameof(propertyName)),
        };
        AssertSingleBits(1f, actual);
    }

    [Fact]
    public void ChatWire_UsesNativeJsonCanonicalBytesAndDistinctJsonPresence()
    {
        var options = new AgentChatOptions(
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            new Dictionary<string, JsonElement>
            {
                ["count"] = JsonSerializer.SerializeToElement(3),
            },
            [0, 1, 255]);
        var optionsJson = Parse(AgentRpcServiceHandler.Serialize(options));
        Assert.Equal(JsonValueKind.Number, optionsJson.GetProperty("additionalProperties").GetProperty("count").ValueKind);
        Assert.Equal("AAH/", optionsJson.GetProperty("continuationHandle").GetString());
        AssertSchemaValid("sunder.agent.chat.provider", "#/$defs/AgentChatOptions", optionsJson);

        var absent = Parse(AgentRpcServiceHandler.Serialize(new AgentChatContent("text")));
        var explicitNull = Parse(AgentRpcServiceHandler.Serialize(new AgentChatContent(
            "tool-result",
            Json: new SunderRpcOptional<JsonElement>(JsonSerializer.SerializeToElement<object?>(null)))));
        Assert.False(absent.TryGetProperty("json", out _));
        Assert.Equal(JsonValueKind.Null, explicitNull.GetProperty("json").ValueKind);
    }

    [Fact]
    public async Task WireEnums_UseKebabCaseStringsAndRejectIntegers()
    {
        var serialized = Parse(AgentRpcServiceHandler.Serialize(new EnumProbe(
            AgentProviderReadinessStatus.NeedsConfiguration)));
        Assert.Equal("needs-configuration", serialized.GetProperty("status").GetString());

        var handler = AgentRpcServiceHandler.Create()
            .AddUnary<EnumProbe, EnumProbe>("wire", "echo", (_, request, _) => ValueTask.FromResult(request))
            .Build();
        await Assert.ThrowsAsync<JsonException>(() => handler.InvokeUnaryAsync(
            null!,
            "wire",
            "echo",
            Parse("{\"status\":1}")).AsTask());
    }

    [Fact]
    public void ChatEvents_EncodeOnlyTheirTaggedVariant()
    {
        AgentChatStreamEvent error = AgentChatStreamEvent.Error(
            "provider-error",
            "Provider failed.",
            "Safe details.",
            AgentChatProviderFailureKind.ContextWindowExceeded);

        var json = Parse(AgentRpcServiceHandler.Serialize(error));
        Assert.Equal("error", json.GetProperty("kind").GetString());
        Assert.Equal("context-window-exceeded", json.GetProperty("errorFailureKind").GetString());
        Assert.False(json.TryGetProperty("contentItems", out _));
        AssertSchemaValid("sunder.agent.chat.provider", "#/$defs/AgentChatStreamEvent", json);
    }

    [Fact]
    public void CatalogEvents_EncodeOnlyTheirTaggedVariant()
    {
        AgentRuntimeCatalogEvent catalogEvent = new AgentSessionCatalogEvent(1, Guid.NewGuid());

        var json = Parse(AgentRpcServiceHandler.Serialize(catalogEvent));
        Assert.Equal("session", json.GetProperty("kind").GetString());
        Assert.False(json.TryGetProperty("profileId", out _));
        Assert.False(json.TryGetProperty("turn", out _));
        AssertSchemaValid("sunder.agent.runtime.catalog", "#/$defs/AgentRuntimeCatalogEvent", json);
    }

    [Fact]
    public async Task OwnerCatalog_PreservesModelReleaseDateAfterWorkspaceAndProfileCreation()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var extensionCatalog = new RegressionTestExtensionCatalog();
        extensionCatalog.AddProvider(AgentRpcServices.ChatProviders, new ChatProviderProbe());
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<AgentRpcCatalog>(extensionCatalog);
        new PackageModule().ConfigureRuntimeServices(serviceCollection, scope.Context);
        await using var services = serviceCollection.BuildServiceProvider();
        var handler = AgentOwnerRuntimeRpc.CreateHandler(
            services.GetRequiredService<AgentOwnerRuntimeProvider>());

        var workspaceResult = await handler.InvokeUnaryAsync(
            null!,
            "agent-runtime",
            "workspace-command",
            JsonSerializer.SerializeToElement(new { kind = 0, displayName = "Wire workspace" }));
        Assert.Equal("Wire workspace", workspaceResult.GetProperty("workspace").GetProperty("displayName").GetString());

        var profileResult = await handler.InvokeUnaryAsync(
            null!,
            "agent-runtime",
            "profile-command",
            JsonSerializer.SerializeToElement(new { kind = 0, displayName = "Wire profile" }));
        var profile = profileResult.GetProperty("profile").Clone();
        var providerId = profile.GetProperty("chatProviderId").GetString();
        Assert.Equal("probe", providerId);

        var catalog = await handler.InvokeUnaryAsync(
            null!,
            "agent-runtime",
            "catalog",
            JsonSerializer.SerializeToElement(new { profile, chatProviderId = providerId }));
        var model = Assert.Single(catalog.GetProperty("chatModels").EnumerateArray().ToArray());
        Assert.Equal("2026-01-15", model.GetProperty("releaseDate").GetString());
    }

    private static void AssertProbe(WireProbe probe)
    {
        Assert.Equal(ExactTimestamp, probe.Timestamp);
        AssertBinary64(probe.Value);
    }

    private static void AssertRequest(JsonElement request)
    {
        Assert.Equal(ExactTimestampText, request.GetProperty("timestamp").GetString());
        AssertBinary64(request.GetProperty("value").GetDouble());
    }

    private static void AssertBinary64(double value)
        => Assert.Equal(BitConverter.DoubleToInt64Bits(Binary64Value), BitConverter.DoubleToInt64Bits(value));

    private static void AssertSingleBits(float expected, float? actual)
        => Assert.Equal(
            BitConverter.SingleToInt32Bits(expected),
            BitConverter.SingleToInt32Bits(Assert.IsType<float>(actual)));

    private static void AssertChatRequestIsSchemaValid(JsonElement request)
        => AssertSchemaValid("sunder.agent.chat.provider", "#/$defs/AgentChatStreamRequest", request);

    private static void AssertSchemaValid(string contractId, string schemaReference, JsonElement value)
    {
        var descriptor = AgentRpcContractDescriptors.Get(contractId);
        Assert.True(descriptor.IsValid(schemaReference, value, out var error), error);
    }

    private static async Task DrainAsync(ISunderRpcServiceHandler handler, JsonElement request)
    {
        await foreach (var _ in handler.InvokeServerStreamAsync(
                           null!,
                           AgentChatProviderRpc.ServiceId,
                           "stream-chat",
                           request))
        {
        }
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed record WireProbe(SunderRpcOffsetDateTime Timestamp, double Value);
    private sealed record DateTimeOffsetProbe(DateTimeOffset Timestamp);
    private sealed record EnumProbe(AgentProviderReadinessStatus Status);

    private sealed class ChatProviderProbe : IAgentChatProvider
    {
        public OptionCaptureChatClient Client { get; } = new();

        public AgentProviderDescriptor Descriptor { get; } = new(
            "probe",
            "Probe",
            [],
            SupportsStreaming: true,
            SupportsInterruptibleRuns: true);

        public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentModelDescriptor>>(
            [
                new AgentModelDescriptor("probe/model", "Probe Model", 128_000, 8_192, IsRecommended: true)
                {
                    ReleaseDate = new DateOnly(2026, 1, 15),
                },
            ]);

        public ValueTask<AgentProviderReadiness> GetReadinessAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.Ready,
                "Ready."));

        public ValueTask<AgentProviderRunCapabilities> GetRunCapabilitiesAsync(
            string? modelId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<IChatClient> CreateChatClientAsync(
            AgentChatClientContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IChatClient>(Client);
    }

    private sealed class OptionCaptureChatClient : IChatClient
    {
        public int CallCount { get; private set; }
        public ChatOptions? ObservedOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            ObservedOptions = options;
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class RawRpcClient(JsonElement response) : ISunderRpcClient
    {
        public JsonElement LastRequest { get; private set; }

        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
            SunderRpcEndpointReference endpoint,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<bool> TryReportInvariantViolationAsync(
            SunderRpcEndpointReference endpoint,
            Exception exception,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
            string contractId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
            long afterRevision,
            long afterSequence,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<JsonElement> InvokeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request.Clone();
            return ValueTask.FromResult(response);
        }

        public async IAsyncEnumerable<JsonElement> SubscribeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request.Clone();
            await Task.Yield();
            yield return response;
        }
    }
}
