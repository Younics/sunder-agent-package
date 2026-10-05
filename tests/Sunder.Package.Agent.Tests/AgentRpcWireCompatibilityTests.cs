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
    public void GeneratedHandlerCodec_PreservesExactOffsetNanosecondsAndBinary64Values()
    {
        var observed = Parse(CanonicalNanosecondJson).Deserialize<WireProbe>(GeneratedJson)!;
        var response = JsonSerializer.SerializeToElement(observed, GeneratedJson);

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

        var response = await SunderRpcClientExtensions.InvokeAsync<WireProbe, WireProbe>(
            client,
            endpoint,
            "wire",
            "echo",
            request);
        AssertProbe(response);
        AssertRequest(client.LastRequest);

        var events = new List<WireProbe>();
        await foreach (var item in SunderRpcClientExtensions.SubscribeAsync<WireProbe, WireProbe>(
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
    public async Task ToolExecutionClient_RequestsFreshExtendedDeadline()
    {
        var response = Parse("{\"toolId\":\"test_tool\",\"summary\":\"Completed.\",\"isError\":false,\"requiresPromptContextRefresh\":false,\"wasTruncated\":false}");
        var client = new RawRpcClient(response);
        client.Provider = Provider(AgentRpcContractIds.ToolSource, "rpc1_tool_source");
        using var source = new Runtime.Rpc.GeneratedToolSourceClient(client, client.Provider);
        var before = DateTimeOffset.UtcNow;

        _ = await source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid()),
            new AgentToolRequest("test_tool", "{}"));

        Assert.Equal("execute", client.LastMethodId);
        Assert.NotNull(client.LastOptions?.DeadlineUtc);
        Assert.InRange(
            client.LastOptions!.DeadlineUtc!.Value,
            before.AddMinutes(34),
            DateTimeOffset.UtcNow.AddMinutes(36));
    }

    [Fact]
    public async Task LegacyToolSourceClient_RemainsUnaryWhenDispatchObservationIsRequested()
    {
        var response = Parse("{\"toolId\":\"test_tool\",\"summary\":\"Completed.\",\"isError\":false,\"requiresPromptContextRefresh\":false,\"wasTruncated\":false}");
        var client = new RawRpcClient(response);
        client.Provider = Provider(AgentRpcContractIds.ToolSource, "rpc1_unary_tool_source");
        using var source = new Runtime.Rpc.GeneratedToolSourceClient(client, client.Provider);
        var dispatchCount = 0;

        _ = await source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid())
            {
                DispatchStarted = () => dispatchCount++,
            },
            new AgentToolRequest("test_tool", "{}"));

        Assert.Equal("execute", client.LastMethodId);
        Assert.Equal(0, dispatchCount);
    }

    [Fact]
    public async Task ObservedToolExecutionClient_TreatsResultAsTerminalAndDisposesStream()
    {
        const int formerMaximumJsonTextLength = 900_000;
        var argumentsJson = new string('x', formerMaximumJsonTextLength + 1);
        var toolResult = new AgentToolResult("test_tool", "Completed.", Content: "done");
        var response = JsonSerializer.SerializeToElement(new { kind = "result", result = toolResult }, RegressionTestRpcValues.Json);
        var client = new RawRpcClient(response) { BlockAfterStreamResponse = true };
        client.Provider = Provider(AgentRpcContractIds.ObservedToolExecution, "rpc1_observed_tool_execution");
        using var execution = new Runtime.Rpc.GeneratedObservedToolExecutionClient(client, client.Provider);
        var dispatchCount = 0;

        var result = await execution.ExecuteAsync(
                new AgentToolExecutionContext(Guid.NewGuid())
                {
                    DispatchStarted = () => dispatchCount++,
                },
                new AgentToolRequest("test_tool", argumentsJson))
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("Completed.", result.Summary);
        Assert.Equal(0, dispatchCount);
        Assert.True(client.StreamDisposed);
        Assert.False(client.LastRequest.TryGetProperty("payloadJson", out _));
        Assert.Equal(
            argumentsJson.Length,
            client.LastRequest.GetProperty("request").GetProperty("argumentsJson").GetString()!.Length);
        AssertSchemaValid(
            "sunder.agent.tool.execution.observed",
            "#/$defs/ToolInvocationRequest",
            client.LastRequest);
        Assert.False(response.TryGetProperty("payloadJson", out _));
        Assert.Equal("test_tool", response.GetProperty("result").GetProperty("toolId").GetString());
        AssertSchemaValid(
            "sunder.agent.tool.execution.observed",
            "#/$defs/ObservedToolInvocationEvent",
            response);
    }

    [Fact]
    public async Task DateTimeOffsetProjection_RejectsUnrepresentableNanoseconds()
    {
        var createdAt = new DateTimeOffset(2026, 7, 28, 12, 34, 56, TimeSpan.FromMinutes(330)).AddTicks(1_234_567);
        var chat = AgentChatWireMapper.FromUpdate(new ChatResponseUpdate { CreatedAt = createdAt });
        Assert.Equal("2026-07-28T07:04:56.1234567Z", chat.CreatedAt!.Value.Value);
        Assert.Equal(createdAt, AgentChatWireMapper.ToUpdate(chat).CreatedAt);
        var unrepresentable = Assert.Throws<SunderRpcException>(() => AgentChatWireMapper.ToUpdate(new(CreatedAt: ExactTimestamp)));
        Assert.Equal(SunderRpcErrorKind.Protocol, unrepresentable.Error.Kind);
        Assert.Equal("agent.rpc.timestamp-not-representable", unrepresentable.Error.Code);

        var client = new RawRpcClient(Parse("{\"timestamp\":\"2026-07-28T12:34:56.123456789Z\"}"));
        await Assert.ThrowsAsync<JsonException>(() => SunderRpcClientExtensions.InvokeAsync<DateTimeOffsetProbe, DateTimeOffsetProbe>(
            client, new("rpc1_timestamp"), "wire", "echo", new(createdAt)).AsTask());
    }

    [Fact]
    public async Task NullableUnary_UsesJsonNullWithoutAnEnvelope()
    {
        using var roles = new RegressionTestProviderRoles();
        var handler = roles.Create("Provider.Gemini", "Rpc.ChatProvider", new ChatProviderProbe()).Handler;
        var response = await handler.InvokeUnaryAsync(
            null!,
            "chat-provider",
            "resolve-utility-model",
            JsonSerializer.SerializeToElement(new { }));
        Assert.Equal(JsonValueKind.Null, response.ValueKind);

        var client = new RawRpcClient(Parse("null"));
        var result = await SunderRpcClientExtensions.InvokeAsync<object, string?>(
            client,
            new SunderRpcEndpointReference("rpc1_wire_compatibility"),
            "wire",
            "none",
            new { }, requestAllowsNull: false, responseAllowsNull: true);
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
        using var roles = new RegressionTestProviderRoles();
        await DrainAsync(roles.Create("Provider.Gemini", "Rpc.ChatProvider", provider).Handler, request);

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
        using var roles = new RegressionTestProviderRoles();
        await DrainAsync(roles.Create("Provider.Gemini", "Rpc.ChatProvider", provider).Handler, request);

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
        var optionsJson = Parse(RegressionTestRpcValues.Serialize(options));
        Assert.Equal(JsonValueKind.Number, optionsJson.GetProperty("additionalProperties").GetProperty("count").ValueKind);
        Assert.Equal("AAH/", optionsJson.GetProperty("continuationHandle").GetString());
        AssertSchemaValid("sunder.agent.chat.provider", "#/$defs/AgentChatOptions", optionsJson);

        var absent = Parse(RegressionTestRpcValues.Serialize(new AgentChatContent("text")));
        var explicitNull = Parse(RegressionTestRpcValues.Serialize(new AgentChatContent(
            "tool-result",
            Json: new SunderRpcOptional<JsonElement>(JsonSerializer.SerializeToElement<object?>(null)))));
        Assert.False(absent.TryGetProperty("json", out _));
        Assert.Equal(JsonValueKind.Null, explicitNull.GetProperty("json").ValueKind);
    }

    [Fact]
    public async Task WireEnums_UseKebabCaseStringsAndRejectIntegers()
    {
        var serialized = JsonSerializer.SerializeToElement(new EnumProbe(
            Sunder.Generated.Rpc.ChatProvider.AgentProviderReadinessStatus.NeedsConfiguration), GeneratedJson);
        Assert.Equal("needs-configuration", serialized.GetProperty("status").GetString());

        var client = new RawRpcClient(Parse("{\"status\":1}"));
        await Assert.ThrowsAsync<JsonException>(() => SunderRpcClientExtensions.InvokeAsync<object, EnumProbe>(
            client, new("rpc1_enum"), "wire", "echo", new { }).AsTask());
    }

    [Fact]
    public void ChatEvents_EncodeOnlyTheirTaggedVariant()
    {
        AgentChatStreamEvent error = AgentChatStreamEvent.Error(
            "provider-error",
            "Provider failed.",
            "Safe details.",
            AgentChatProviderFailureKind.ContextWindowExceeded);

        var json = JsonSerializer.SerializeToElement(error, RegressionTestRpcValues.Json);
        Assert.Equal("error", json.GetProperty("kind").GetString());
        Assert.Equal("context-window-exceeded", json.GetProperty("errorFailureKind").GetString());
        Assert.False(json.TryGetProperty("contentItems", out _));
        AssertSchemaValid("sunder.agent.chat.provider", "#/$defs/AgentChatStreamEvent", json);
    }

    [Fact]
    public void CatalogEvents_EncodeOnlyTheirTaggedVariant()
    {
        Sunder.Generated.Rpc.RuntimeCatalog.AgentRuntimeCatalogEvent catalogEvent =
            new Sunder.Generated.Rpc.RuntimeCatalog.AgentSessionCatalogEvent { Sequence = 1, SessionId = Guid.NewGuid() };

        var json = JsonSerializer.SerializeToElement(catalogEvent, GeneratedJson);
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
        extensionCatalog.AddProvider(RegressionTestBindings.ChatProviders, new ChatProviderProbe());
        await using var services = RegressionTestRuntime.Create(scope.Context, extensionCatalog);
        var handler = services.GetRequiredService<Rpc.Owner.OwnerRuntimeService>().CreateSunderRpcProviderRegistration().Handler;

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

    [Fact]
    public void OwnerDescriptor_AcceptsValuesBeyondFormerTransportCeilings()
    {
        var longIdentifier = new string('i', 513);
        var longOutput = new string('x', 16_385);
        const long maximumWireInteger = 9_007_199_254_740_991;
        var sessionId = Guid.NewGuid();

        AssertOwnerSchemaValid(
            "#/$defs/AgentTranscriptToolDetailRecord",
            JsonSerializer.SerializeToElement(new
            {
                isError = false,
                outputText = longOutput,
                revision = maximumWireInteger,
                sessionId,
                toolId = longIdentifier,
                wasTransportTruncated = false,
                wasTruncated = false,
            }));
        AssertOwnerSchemaValid(
            "#/$defs/AgentTurnMutation",
            JsonSerializer.SerializeToElement(new
            {
                baseContentLength = longOutput.Length,
                contentRevision = maximumWireInteger,
                kind = 0,
                runtimeRevision = maximumWireInteger,
                sessionId,
                text = longOutput,
                turnId = Guid.NewGuid(),
                updatedAtUtc = ExactTimestampText,
            }));
        AssertOwnerSchemaValid(
            "#/$defs/AgentModelDescriptor",
            JsonSerializer.SerializeToElement(new
            {
                contextWindow = 2_000_001,
                displayName = "Model",
                isRecommended = false,
                maxOutputTokens = 2_000_001,
                modelId = longIdentifier,
            }));
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
        var descriptor = RegressionTestRpcDescriptors.Get(contractId);
        Assert.True(descriptor.IsValid(schemaReference, value, out var error), error);
    }

    private static void AssertOwnerSchemaValid(string schemaReference, JsonElement value)
        => Assert.True(
            RegressionTestRpcDescriptors.Get("sunder.agent.owner.runtime").IsValid(schemaReference, value, out var error),
            error);

    private static async Task DrainAsync(ISunderRpcServiceHandler handler, JsonElement request)
    {
        await foreach (var _ in handler.InvokeServerStreamAsync(
                           null!,
                           "chat-provider",
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
    private sealed record EnumProbe(Sunder.Generated.Rpc.ChatProvider.AgentProviderReadinessStatus Status);

    private static JsonSerializerOptions GeneratedJson => (JsonSerializerOptions)typeof(Sunder.Generated.Rpc.ChatProvider.SunderAgentChatProviderRpcHandler)
        .GetField("JsonOptions", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;

    private static SunderRpcProviderSnapshot Provider(string contractId, string endpoint)
    {
        var descriptor = RegressionTestRpcDescriptors.Get(contractId);
        return new("test.package", "2.0.0", "test.provider", contractId, descriptor.Version, descriptor.Sha256, descriptor.AbiSha256!,
            Guid.NewGuid(), 1, 1, new(endpoint), 1, SunderRpcProviderState.Active);
    }

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
        internal SunderRpcProviderSnapshot? Provider { get; set; }
        public JsonElement LastRequest { get; private set; }
        public string? LastServiceId { get; private set; }
        public string? LastMethodId { get; private set; }
        public SunderRpcCallOptions? LastOptions { get; private set; }
        public bool BlockAfterStreamResponse { get; init; }
        public bool StreamDisposed { get; private set; }

        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
            SunderRpcEndpointReference endpoint,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Provider);

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
            LastServiceId = serviceId;
            LastMethodId = methodId;
            LastOptions = options;
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
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                LastRequest = request.Clone();
                LastServiceId = serviceId;
                LastMethodId = methodId;
                LastOptions = options;
                await Task.Yield();
                yield return response;
                if (BlockAfterStreamResponse)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
            }
            finally
            {
                StreamDisposed = true;
            }
        }

        public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ISunderRpcCallScope>(new Scope(this, options));
        }

        private sealed class Scope(RawRpcClient owner, SunderRpcCallOptions? options) : ISunderRpcCallScope
        {
            private bool _disposed;
            public DateTimeOffset DeadlineUtc { get; } = options?.DeadlineUtc ?? DateTimeOffset.UtcNow.AddMinutes(1);
            public ValueTask DisposeAsync() { _disposed = true; return ValueTask.CompletedTask; }
            public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
                => owner.GetProviderAsync(endpoint, cancellationToken);
            public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default)
                => owner.TryReportInvariantViolationAsync(endpoint, exception, cancellationToken);
            public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
                => owner.DiscoverAsync(contractId, cancellationToken);
            public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default)
                => owner.WatchAsync(afterRevision, afterSequence, cancellationToken);
            public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
                SunderRpcCallOptions? callOptions = null, CancellationToken cancellationToken = default)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return owner.InvokeAsync(endpoint, serviceId, methodId, request, callOptions ?? options, cancellationToken);
            }
            public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
                SunderRpcCallOptions? callOptions = null, CancellationToken cancellationToken = default)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return owner.SubscribeAsync(endpoint, serviceId, methodId, request, callOptions ?? options, cancellationToken);
            }
            public ValueTask<SunderRpcContentReference> RegisterContentAsync(SunderRpcEndpointReference endpoint, Stream source,
                SunderRpcContentRegistrationOptions registrationOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(SunderRpcEndpointReference endpoint, string path,
                SunderRpcContentRegistrationOptions registrationOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<Stream> OpenContentAsync(SunderRpcContentReference reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }
}
