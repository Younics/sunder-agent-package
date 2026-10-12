using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Rpc;
using Xunit;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;

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
        var response = JsonSerializer.SerializeToElement(new AgentToolResult("test_tool", "Completed."),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var client = new RawRpcClient(response);
        client.Provider = Provider(RegressionTestBindings.ToolSources, "rpc1_tool_source");
        await using var receiver = new GeneratedServiceTestScope(client);
        var source = Assert.Single((await receiver.Providers<AgentServices.IToolSourceService>().ListAsync()).Providers).Service;
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
        var response = JsonSerializer.SerializeToElement(new AgentToolResult("test_tool", "Completed."),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var client = new RawRpcClient(response);
        client.Provider = Provider(RegressionTestBindings.ToolSources, "rpc1_unary_tool_source");
        await using var receiver = new GeneratedServiceTestScope(client);
        var source = Assert.Single((await receiver.Providers<AgentServices.IToolSourceService>().ListAsync()).Providers).Service;
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
    public async Task ObservedToolExecutionConsumer_TreatsResultAsTerminalAndDisposesStream()
    {
        const int formerMaximumJsonTextLength = 900_000;
        var argumentsJson = new string('x', formerMaximumJsonTextLength + 1);
        var toolResult = new AgentToolResult("test_tool", "Completed.", Content: "done");
        var response = JsonSerializer.SerializeToElement(new { kind = "result", result = toolResult },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var client = new RawRpcClient(response) { BlockAfterStreamResponse = true };
        client.Provider = Provider(RegressionTestBindings.ObservedToolExecutions, "rpc1_observed_tool_execution");
        await using var receiver = new GeneratedServiceTestScope(client);
        var execution = Assert.Single((await receiver.Providers<AgentServices.IToolExecutionObservedService>().ListAsync()).Providers);
        var sourceClient = new RawRpcClient(response.GetProperty("result"))
        { Provider = Provider(RegressionTestBindings.ToolSources, "rpc1_observed_source") };
        await using var sourceReceiver = new GeneratedServiceTestScope(sourceClient);
        var source = Assert.Single((await sourceReceiver.Providers<AgentServices.IToolSourceService>().ListAsync()).Providers);
        var dispatchCount = 0;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var context = new AgentToolExecutionContext(Guid.NewGuid())
        {
            DispatchStarted = () => dispatchCount++,
        };
        var invocation = new Services.AgentToolInvocationReference("test.package",
            new AgentToolDescriptor("test_tool", "Test tool", "Observed execution"), source, execution,
            null, null, null, Guid.NewGuid().ToString("N"));
        // Terminal-result policy belongs to the real tool consumer, not a generic
        // generated stream client. Keep the generated client and hostile open tail.
        var execute = typeof(Services.AgentToolService).GetMethod("ExecuteToolAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var pending = (ValueTask<AgentToolResult>)execute.Invoke(null,
            [invocation, context, new AgentToolRequest("test_tool", argumentsJson), timeout.Token])!;
        var result = await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Completed.", result.Summary);
        Assert.Equal(0, dispatchCount);
        Assert.Null(sourceClient.LastMethodId);
        Assert.True(client.StreamDisposed);
        Assert.False(client.LastRequest.TryGetProperty("payloadJson", out _));
        Assert.Equal(
            argumentsJson.Length,
            client.LastRequest.GetProperty("request").GetProperty("argumentsJson").GetString()!.Length);
        AssertSchemaValid(
            "sunder.agent.tool.execution.observed",
            CurrentPublicRpcDescriptors.Get(RegressionTestBindings.ObservedToolExecutions)
                .FindService("tool-execution-observed")!.FindMethod("execute")!.RequestSchemaReference,
            client.LastRequest);
        Assert.False(response.TryGetProperty("payloadJson", out _));
        Assert.Equal("test_tool", response.GetProperty("result").GetProperty("toolId").GetString());
        AssertSchemaValid(
            "sunder.agent.tool.execution.observed",
            CurrentPublicRpcDescriptors.Get(RegressionTestBindings.ObservedToolExecutions)
                .FindService("tool-execution-observed")!.FindMethod("execute")!.OutputSchemaReference,
            response);
    }

    [Fact]
    public async Task OffsetDateTimeProjection_PreservesExactWireTextAndRejectsUnrepresentableDateTimeOffset()
    {
        var createdAt = new DateTimeOffset(2026, 7, 28, 12, 34, 56, TimeSpan.FromMinutes(330)).AddTicks(1_234_567);
        var representable = new SunderRpcOffsetDateTime("2026-07-28T12:34:56.1234567+05:30");
        Assert.True(representable.TryGetDateTimeOffset(out var projected));
        Assert.Equal(createdAt, projected);
        Assert.False(ExactTimestamp.TryGetDateTimeOffset(out _));

        var client = new RawRpcClient(Parse("{\"timestamp\":\"2026-07-28T12:34:56.123456789Z\"}"));
        await Assert.ThrowsAsync<JsonException>(() => SunderRpcClientExtensions.InvokeAsync<DateTimeOffsetProbe, DateTimeOffsetProbe>(
            client, new("rpc1_timestamp"), "wire", "echo", new(createdAt)).AsTask());
    }

    [Fact]
    public async Task NullableUnary_UsesJsonNullWithoutAnEnvelope()
    {
        using var providers = new RegressionTestSourceProviders();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        await using var receiver = new GeneratedServiceTestScope(new RegressionTestRpcClient(state, "sunder.package.agent.tests"));
        var registration = providers.Create(RegressionTestBindings.ChatProviders, new ChatProviderProbe(), receiver);
        var snapshot = state.AddGenerated("test.package", registration, registration, providers, registration.ProviderId);
        var response = await new RegressionTestRpcClient(state, "test.caller").InvokeAsync(
            snapshot.Endpoint,
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
        request = CompleteChatRequest(request);
        AssertChatRequestIsSchemaValid(request);

        var provider = new ChatProviderProbe();
        using var providers = new RegressionTestSourceProviders();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        await using var receiver = new GeneratedServiceTestScope(new RegressionTestRpcClient(state, "sunder.package.agent.tests"));
        await DrainAsync(state, providers.Create(RegressionTestBindings.ChatProviders, provider, receiver), providers, request);

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
        request = CompleteChatRequest(request);
        AssertChatRequestIsSchemaValid(request);

        var provider = new ChatProviderProbe();
        using var providers = new RegressionTestSourceProviders();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        await using var receiver = new GeneratedServiceTestScope(new RegressionTestRpcClient(state, "sunder.package.agent.tests"));
        await DrainAsync(state, providers.Create(RegressionTestBindings.ChatProviders, provider, receiver), providers, request);

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
    public async Task ChatWire_UsesNativeJsonCanonicalBytesAndDistinctJsonPresence()
    {
        await using var lifetime = new SunderRpcResourceLifetime();
        await using var marshal = await lifetime.CreateCallAsync(new RawRpcClient(Parse("null")), new("rpc1_wire_values"));
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
        var optionsJson = await marshal.SerializeAsync(options);
        Assert.Equal(JsonValueKind.Number, optionsJson.GetProperty("additionalProperties").GetProperty("count").ValueKind);
        Assert.Equal("AAH/", optionsJson.GetProperty("continuationHandle").GetString());
        AssertSchemaValid("sunder.agent.chat.provider", "#/$defs/AgentChatOptions", optionsJson);

        var absent = await marshal.SerializeAsync(new AgentChatContent("text"));
        var explicitNull = await marshal.SerializeAsync(new AgentChatContent(
            "tool-result",
            Json: new SunderRpcOptional<JsonElement>(JsonSerializer.SerializeToElement<object?>(null))));
        Assert.False(absent.TryGetProperty("json", out _));
        Assert.Equal(JsonValueKind.Null, explicitNull.GetProperty("json").ValueKind);
    }

    [Fact]
    public async Task WireEnums_UseKebabCaseStringsAndRejectIntegers()
    {
        var serialized = JsonSerializer.SerializeToElement(new EnumProbe(
            AgentProviderReadinessStatus.NeedsConfiguration), GeneratedJson);
        Assert.Equal("needs-configuration", serialized.GetProperty("status").GetString());

        var valid = new RawRpcClient(Parse("{\"providerId\":\"probe\",\"status\":\"needs-configuration\",\"message\":\"Configure provider\"}"))
        { Provider = Provider(RegressionTestBindings.ChatProviders, "rpc1_enum_valid") };
        await using var validReceiver = new GeneratedServiceTestScope(valid);
        var validService = Assert.Single((await validReceiver.Providers<AgentServices.IChatProviderService>().ListAsync()).Providers).Service;
        Assert.Equal(AgentProviderReadinessStatus.NeedsConfiguration, (await validService.GetReadinessAsync()).Status);

        var invalid = new RawRpcClient(Parse("{\"providerId\":\"probe\",\"status\":1,\"message\":\"Configure provider\"}"))
        { Provider = Provider(RegressionTestBindings.ChatProviders, "rpc1_enum_invalid") };
        await using var invalidReceiver = new GeneratedServiceTestScope(invalid);
        var invalidService = Assert.Single((await invalidReceiver.Providers<AgentServices.IChatProviderService>().ListAsync()).Providers).Service;
        await Assert.ThrowsAsync<JsonException>(() => invalidService.GetReadinessAsync().AsTask());
    }

    [Fact]
    public async Task ChatEvents_EncodeOnlyTheirTaggedVariant()
    {
        AgentChatStreamEvent error = AgentChatStreamEvent.Error(
            "provider-error",
            "Provider failed.",
            "Safe details.",
            AgentChatProviderFailureKind.ContextWindowExceeded);

        await using var lifetime = new SunderRpcResourceLifetime();
        await using var marshal = await lifetime.CreateCallAsync(new RawRpcClient(Parse("null")), new("rpc1_wire_events"));
        marshal.JsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        var json = await marshal.SerializeAsync(error);
        Assert.Equal("error", json.GetProperty("kind").GetString());
        Assert.Equal("context-window-exceeded", json.GetProperty("errorFailureKind").GetString());
        Assert.False(json.TryGetProperty("contentItems", out _));
        AssertSchemaValid("sunder.agent.chat.provider", "#/$defs/AgentChatStreamEvent", json);
    }

    [Fact]
    public async Task CatalogEvents_EncodeOnlyTheirTaggedVariant()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var runtime = RegressionTestRuntime.Create(scope.Context);
        var workspace = runtime.GetRequiredService<Services.AgentWorkspaceService>().CreateWorkspace("Tagged event workspace");
        var catalog = runtime.GetRequiredService<Services.AgentRuntimeCatalog>();
        var handler = catalog.CreateSunderRpcProviderRegistration().Handler;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = handler.InvokeServerStreamAsync(null!, "runtime-catalog", "watch",
            JsonSerializer.SerializeToElement(new { }), cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("reset", events.Current.GetProperty("kind").GetString());
        var session = runtime.GetRequiredService<Services.AgentSessionService>().CreateSession("Tagged event", workspaceId: workspace.WorkspaceId);
        Assert.True(await events.MoveNextAsync());
        var json = events.Current;
        Assert.Equal("session", json.GetProperty("kind").GetString());
        Assert.Equal(session.SessionId, json.GetProperty("sessionId").GetGuid());
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
        var registration = services.GetRequiredService<Runtime.AgentOwnerRuntimeProvider>().CreateSunderRpcProviderRegistration();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        using var providers = new RegressionTestSourceProviders();
        var snapshot = state.AddGenerated("sunder.package.agent", registration, registration, providers, registration.ProviderId);
        var client = new RegressionTestRpcClient(state, "test.caller");

        var workspaceResult = await client.InvokeAsync(
            snapshot.Endpoint,
            "agent-runtime",
            "workspace-command",
            JsonSerializer.SerializeToElement(new AgentWorkspaceCommand(AgentWorkspaceCommandKind.Create,
                DisplayName: "Wire workspace"), CurrentPrivateRpcDescriptors.OwnerJson));
        Assert.Equal("Wire workspace", workspaceResult.GetProperty("workspace").GetProperty("displayName").GetString());

        var profileResult = await client.InvokeAsync(
            snapshot.Endpoint,
            "agent-runtime",
            "profile-command",
            JsonSerializer.SerializeToElement(new AgentProfileCommand(AgentProfileCommandKind.Create,
                DisplayName: "Wire profile"), CurrentPrivateRpcDescriptors.OwnerJson));
        var profile = profileResult.GetProperty("profile").Clone();
        var providerId = profile.GetProperty("chatProviderId").GetString();
        Assert.Equal("probe", providerId);

        var catalog = await client.InvokeAsync(
            snapshot.Endpoint,
            "agent-runtime",
            "catalog",
            JsonSerializer.SerializeToElement(new AgentCatalogRequest(
                profile.Deserialize<AgentProfileRecord>(CurrentPrivateRpcDescriptors.OwnerJson), providerId),
                CurrentPrivateRpcDescriptors.OwnerJson));
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
            JsonSerializer.SerializeToElement(new AgentTranscriptToolDetailRecord(sessionId,
                null, null, null, null, longIdentifier, null, longOutput, null, null, null, null,
                false, false, null, null, null, null, null, null, maximumWireInteger), CurrentPrivateRpcDescriptors.OwnerJson));
        AssertOwnerSchemaValid(
            "#/$defs/AgentTurnMutation",
            JsonSerializer.SerializeToElement(new AgentTurnMutation(sessionId, Guid.NewGuid(), maximumWireInteger,
                AgentTurnMutationKind.Add, longOutput.Length, longOutput,
                new DateTimeOffset(2026, 7, 28, 12, 34, 56, TimeSpan.FromHours(5.5)).AddTicks(1_234_567),
                RuntimeRevision: maximumWireInteger), CurrentPrivateRpcDescriptors.OwnerJson));
        AssertOwnerSchemaValid(
            "#/$defs/AgentModelDescriptor",
            JsonSerializer.SerializeToElement(new AgentModelDescriptor(longIdentifier, "Model", 2_000_001, 2_000_001),
                CurrentPrivateRpcDescriptors.OwnerJson));
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
        var descriptor = CurrentPublicRpcDescriptors.Get(contractId);
        Assert.True(descriptor.IsValid(schemaReference, value, out var error), error);
    }

    private static void AssertOwnerSchemaValid(string schemaReference, JsonElement value)
        => Assert.True(
            CurrentPrivateRpcDescriptors.Get("sunder.agent.owner.runtime").IsValid(schemaReference, value, out var error),
            error);

    private static JsonElement CompleteChatRequest(JsonElement request)
    {
        var root = JsonNode.Parse(request.GetRawText())!.AsObject();
        var options = JsonSerializer.SerializeToNode(new AgentChatOptions(null, null, null, null, null,
            null, null, null, null, null, null, null, [], null, null),
            new JsonSerializerOptions(GeneratedJson) { DefaultIgnoreCondition = JsonIgnoreCondition.Never })!.AsObject();
        foreach (var property in root["options"]!.AsObject()) options[property.Key] = property.Value?.DeepClone();
        root["options"] = options;
        return JsonSerializer.SerializeToElement(root);
    }

    private static async Task DrainAsync(RegressionTestRpcState state, Sunder.Sdk.Worker.SunderWorkerProviderRegistration registration,
        IDisposable lifetime, JsonElement request)
    {
        var snapshot = state.AddGenerated("test.package", registration, registration, lifetime, registration.ProviderId);
        await foreach (var _ in new RegressionTestRpcClient(state, "test.caller").SubscribeAsync(
                           snapshot.Endpoint,
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
    private sealed record EnumProbe(AgentProviderReadinessStatus Status);

    private static JsonSerializerOptions GeneratedJson => RegressionTestRpcValues.Json;

    private static SunderRpcProviderSnapshot Provider(string contractId, string endpoint)
    {
        var descriptor = CurrentPublicRpcDescriptors.Get(contractId);
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
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SunderRpcCatalogSnapshot(1, 1,
                Provider is { } provider && (string.IsNullOrEmpty(contractId) || contractId == provider.ContractId)
                    ? [provider] : []));
        }

        public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
            long afterRevision,
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

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
