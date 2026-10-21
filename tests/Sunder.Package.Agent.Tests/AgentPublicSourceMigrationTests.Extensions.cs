extern alias AgentApp;

using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private static async Task VerifyExtensionsAsync(string root, AssemblyLoadContext loadContext, Assembly assembly,
        IReadOnlyDictionary<string, SunderRpcContractDescriptor> descriptors)
    {
        await using (var fixture = await CreateExtension(root, loadContext, assembly, "WorkspaceEditor", descriptors["younics.ai.agent.workspace.editor"]))
        {
            var now = DateTimeOffset.UtcNow;
            var workspace = new AgentWorkspaceRecord("workspace.one", "Workspace", null, now, now);
            var context = JsonSerializer.SerializeToElement(new { configurationId = "configuration.one", targetId = "local", workspace }, ExecutionJson);
            fixture.Probe.Replies["CanEditAsync"] = "true";
            Assert.True((await fixture.Call("CanEditAsync", context)).GetBoolean());
            var validEditorRequest = fixture.State.LastInvocationRequest;
            fixture.Probe.Replies["GetSectionsAsync"] = """
                [{"sectionId":"paths","title":"Paths 漢字","description":null,"fields":[{"fieldId":"paths","kind":"path-list","label":"Paths","description":null,"value":null,"options":null,"addItemLabel":null,"defaultNewItemValue":null,"defaultNewSecondaryItemValue":null,"itemValueLabel":null,"secondaryItemValueLabel":null,"useFolderPicker":true,"useSecondaryFolderPicker":false,"actions":[{"actionId":"open","kind":"open-package-settings","label":"Open","packageId":null,"parameters":{"reset":null,"category":"files"}}],"items":[{"isDefault":false,"itemId":"one","value":"/work/one","secondaryValue":null}]}]}]
                """;
            var sections = await fixture.Call("GetSectionsAsync", context);
            var field = sections[0].GetProperty("fields")[0];
            Assert.Equal("path-list", field.GetProperty("kind").GetString());
            Assert.False(field.GetProperty("useSecondaryFolderPicker").GetBoolean());
            Assert.Equal(JsonValueKind.Null, field.GetProperty("actions")[0].GetProperty("parameters").GetProperty("reset").ValueKind);
            Assert.False(field.GetProperty("items")[0].TryGetProperty("secondaryValue", out _));
            fixture.Probe.Replies["SaveSectionAsync"] = "{\"message\":\"Rejected path\",\"success\":false}";
            var request = JsonSerializer.SerializeToElement(new
            {
                context,
                request = new { sectionId = "paths", fields = new { paths = new { items = new[] { new { isDefault = false, itemId = "one", value = "/work/one" } } } } },
            }, ExecutionJson);
            Assert.False((await fixture.Call("SaveSectionAsync", request)).GetProperty("success").GetBoolean());
            var received = fixture.Probe.LastRequest.GetProperty("request").GetProperty("fields").GetProperty("paths");
            Assert.Equal(JsonValueKind.Null, received.GetProperty("value").ValueKind);
            Assert.False(received.GetProperty("items")[0].GetProperty("isDefault").GetBoolean());
            var calls = fixture.Probe.Calls;
            var peerCalls = fixture.State.InvocationAttemptCount;
            var invalid = JsonSerializer.SerializeToElement(new { configurationId = "", targetId = "local", workspace }, ExecutionJson);
            await Assert.ThrowsAsync<JsonException>(() => fixture.Call("CanEditAsync", invalid));
            Assert.Equal(peerCalls, fixture.State.InvocationAttemptCount);
            Assert.Equal(calls, fixture.Probe.Calls);
            var invalidWire = JsonNode.Parse(validEditorRequest.GetRawText())!.AsObject();
            invalidWire["configurationId"] = "";
            var endpoint = Assert.Single(fixture.State.Discover("younics.ai.agent.workspace.editor").Providers).Endpoint;
            var validation = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Rpc.InvokeAsync(endpoint,
                "workspace-editor", "can-edit", JsonSerializer.SerializeToElement(invalidWire)).AsTask());
            Assert.Equal(SunderRpcErrorKind.Validation, validation.Error.Kind);
            Assert.Equal(peerCalls + 1, fixture.State.InvocationAttemptCount);
            Assert.Equal(calls, fixture.Probe.Calls);
        }

        await using (var fixture = await CreateExtension(root, loadContext, assembly, "PermissionSurface", descriptors["younics.ai.agent.permission.surface"]))
        {
            fixture.Probe.Replies["DescribeAsync"] = """
                {"actions":[{"actionId":"read","boundaries":[{"boundaryId":"outside","defaultDecision":"deny","description":"Outside workspace","displayName":"Outside"}],"description":"Read content","displayName":"Read"}],"displayName":"Files","surfaceId":"files"}
                """;
            var result = await fixture.Call("DescribeAsync", EmptyRequest());
            Assert.Equal("deny", result.GetProperty("actions")[0].GetProperty("boundaries")[0].GetProperty("defaultDecision").GetString());
            fixture.Probe.Replies["DescribeAsync"] = "{\"actions\":[],\"displayName\":\"Files\",\"surfaceId\":\"\"}";
            var invalid = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Call("DescribeAsync", EmptyRequest()));
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, invalid.Error.Kind);
        }

        await using (var fixture = await CreateExtension(root, loadContext, assembly, "ProfileCapabilityConsumer", descriptors["younics.ai.agent.profile.capability.consumer"]))
        {
            fixture.Probe.Replies["DescribeAsync"] = """
                {"capabilities":[{"capabilityKind":"memory","description":"","displayName":"Memory"}],"consumerId":"memory.one","displayName":"Memory consumer"}
                """;
            var result = await fixture.Call("DescribeAsync", EmptyRequest());
            Assert.Equal("", result.GetProperty("capabilities")[0].GetProperty("description").GetString());
        }

        await using (var fixture = await CreateExtension(root, loadContext, assembly, "SelectableCapabilityProvider", descriptors["younics.ai.agent.selectable.capability.provider"]))
        {
            fixture.Probe.Replies["ListAsync"] = """
                [{"capabilityId":"tool.one","displayName":"Tool","description":null,"groupDescription":null,"groupDisplayName":null,"groupId":null,"groupSortOrder":0,"isSelectable":false,"kind":"tool","sourceDisplayName":null,"sourceId":null,"statusText":null}]
                """;
            var request = JsonSerializer.SerializeToElement(new { profile = new
            {
                createdAtUtc = "2026-09-18T00:00:00Z", displayName = "Profile", isInternal = false,
                profileId = "default", updatedAtUtc = "2026-09-18T00:00:00Z", behaviorLoopSettingsJson = "{broken",
            } });
            var result = await fixture.Call("ListAsync", request);
            Assert.False(result[0].GetProperty("isSelectable").GetBoolean());
            Assert.False(result[0].TryGetProperty("description", out _));
            Assert.Equal("{broken", fixture.Probe.LastRequest.GetProperty("behaviorLoopSettingsJson").GetString());
            fixture.Probe.Streams["WatchAsync"] = ["{\"revision\":0}", "{\"revision\":1}"];
            var events = await ReadGeneratedStream(fixture.Client, CancellationToken.None, cancelAfterFirst: null);
            Assert.Equal([0L, 1L], events.Select(value => value.GetProperty("revision").GetInt64()).ToArray());
            fixture.Probe.ForgedCancellation = true;
            var forged = await Assert.ThrowsAsync<SunderRpcException>(() => ReadGeneratedStream(fixture.Client, CancellationToken.None, null));
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, forged.Error.Kind);
            fixture.Probe.ForgedCancellation = false;
            using var cancellation = new CancellationTokenSource();
            fixture.Probe.WaitAfterEvent = true;
            var cancelled = await ReadGeneratedStream(fixture.Client, cancellation.Token, cancellation);
            Assert.Single(cancelled);
        }

        await using (var fixture = await CreateExtension(root, loadContext, assembly, "EmbeddingProvider", descriptors["younics.ai.agent.embedding.provider"]))
        {
            fixture.Probe.Replies["DescribeAsync"] = """
                {"displayName":"Embedding","providerId":"embedding.one","packageId":null,"supportedAuthModes":["api-key","codex-connected"]}
                """;
            Assert.Equal("codex-connected", (await fixture.Call("DescribeAsync", EmptyRequest())).GetProperty("supportedAuthModes")[1].GetString());
            fixture.Probe.Replies["ListModelsAsync"] = "[{\"displayName\":\"Model\",\"isRecommended\":false,\"modelId\":\"model.one\",\"dimensions\":null}]";
            Assert.False((await fixture.Call("ListModelsAsync", EmptyRequest()))[0].TryGetProperty("dimensions", out _));
            fixture.Probe.Replies["SpaceIdentityAsync"] = "\"space:v1\"";
            Assert.Equal("space:v1", (await fixture.Call("SpaceIdentityAsync", JsonSerializer.SerializeToElement(new { value = "model.one" }))).GetString());
            fixture.Probe.Replies["GenerateAsync"] = "null";
            Assert.Equal(JsonValueKind.Null, (await fixture.Call("GenerateAsync", JsonSerializer.SerializeToElement(new { modelId = "model.one", text = "Hello" }))).ValueKind);
            fixture.Probe.Replies["GenerateBatchAsync"] = """
                [null,{"modelId":"model.one","values":[0.125,-0.5]},null]
                """;
            var request = JsonSerializer.SerializeToElement(new { modelId = "model.one", texts = new[] { "first", "second", "third" } });
            var result = await fixture.Call("GenerateBatchAsync", request);
            Assert.Equal(3, result.GetArrayLength());
            Assert.Equal(JsonValueKind.Null, result[0].ValueKind);
            Assert.Equal(JsonValueKind.Null, result[2].ValueKind);
            Assert.Equal([0.125, -0.5], result[1].GetProperty("values").EnumerateArray().Select(value => value.GetDouble()).ToArray());
            Assert.False(result[1].TryGetProperty("dimensions", out _));
            fixture.Probe.Replies["GenerateBatchAsync"] = "[{\"modelId\":\"model.one\",\"values\":null}]";
            var invalid = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Call("GenerateBatchAsync", request));
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, invalid.Error.Kind);
        }
    }

    private static JsonElement EmptyRequest() => JsonSerializer.SerializeToElement(new { });


    private static async Task<ExtensionFixture> CreateExtension(string root, AssemblyLoadContext loadContext, Assembly assembly,
        string service, SunderRpcContractDescriptor descriptor)
    {
        var probe = new ExtensionProbe();
        var provider = await CreateGeneratedExtensionProviderAsync(root, loadContext, service, probe.Invoke);
        var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "younics.ai.agent");
        var receiver = new GeneratedServiceTestScope(rpc.ForCaller("test.extension.owner"));
        probe.ProjectionReceiver = receiver;
        probe.ContractId = descriptor.ContractId;
        var registration = receiver.Register(provider, descriptor.ContractId);
        state.Add("test.extension.owner", descriptor, provider, registration.Handler, EmptyDisposable.Instance, registration.ProviderId, "2.0.0");
        var clientReceiver = new GeneratedServiceTestScope(rpc);
        var serviceType = service == "WorkspaceEditor"
            ? typeof(AgentApp::Sunder.Generated.Packages.Younics_Ai_Agent_Services.IWorkspaceEditorService)
            : typeof(AgentServices.IChatProviderService).Assembly.GetType(
                "Sunder.Generated.Packages.Younics_Ai_Agent_Services.I" + service + "Service", true)!;
        var catalog = clientReceiver.Providers(serviceType);
        var snapshot = await AgentRoleBoundary.Await(AgentRoleBoundary.Call(catalog, "ListAsync", CancellationToken.None)!);
        var selected = Assert.Single(((IEnumerable)snapshot!.GetType().GetProperty("Providers")!.GetValue(snapshot)!).Cast<object>());
        var client = selected.GetType().GetProperty("Service")!.GetValue(selected)!;
        return new(state, rpc, clientReceiver, receiver, client, probe);
    }

    private sealed class ExtensionFixture(RegressionTestRpcState state, RegressionTestRpcClient rpc,
        GeneratedServiceTestScope clientReceiver, GeneratedServiceTestScope receiver, object client, ExtensionProbe probe) : IAsyncDisposable
    {
        public RegressionTestRpcClient Rpc { get; } = rpc;
        public RegressionTestRpcState State { get; } = state;
        public object Client { get; } = client;
        public ExtensionProbe Probe { get; } = probe;
        public Task<JsonElement> Call(string method, JsonElement request) => InvokeGeneratedClient(Client, method, request);
        public async ValueTask DisposeAsync()
        {
            await clientReceiver.DisposeAsync();
            await receiver.DisposeAsync();
            State.Dispose();
        }
    }

    private sealed class ExtensionProbe
    {
        public Dictionary<string, string> Replies { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string[]> Streams { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<object>> TypedStreams { get; } = new(StringComparer.Ordinal);
        public GeneratedServiceTestScope? ProjectionReceiver { get; set; }
        public string? ContractId { get; set; }
        public JsonElement LastRequest { get; private set; }
        public int Calls { get; private set; }
        public bool WaitAfterEvent { get; set; }
        public bool ForgedCancellation { get; set; }
        public object Invoke(string method, Type resultType, object? request, CancellationToken token)
        {
            LastRequest = JsonSerializer.SerializeToElement(ObserveFixtureArgument(request), ExecutionJson);
            if (resultType == typeof(void))
            {
                token.ThrowIfCancellationRequested();
                Calls++;
                Assert.Equal("{}", Replies[method]);
                return ValueTask.CompletedTask;
            }
            return typeof(ExtensionProbe).GetMethod(Streams.ContainsKey(method) || TypedStreams.ContainsKey(method) ? nameof(Stream) : nameof(Reply), BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(resultType).Invoke(this, [method, token])!;
        }
        private ValueTask<T> Reply<T>(string method, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(JsonSerializer.Deserialize<T>(Replies[method], ExecutionJson)!);
        }
        private async IAsyncEnumerable<T> Stream<T>(string method, [EnumeratorCancellation] CancellationToken token)
        {
            Calls++;
            if (ForgedCancellation) throw new OperationCanceledException("Unrequested provider cancellation.");
            if (TypedStreams.TryGetValue(method, out var typed))
            {
                var projection = RegressionTestGeneratedProviderProjection.FromCandidate(
                    Environment.GetEnvironmentVariable("SUNDER_AGENT_PUBLIC_FIXTURE_CANDIDATE")!, ContractId!);
                foreach (var value in typed)
                {
                    token.ThrowIfCancellationRequested();
                    yield return (T)(await projection.ProjectAsync(value, typeof(T), ProjectionReceiver!, token))!;
                    if (WaitAfterEvent) await Task.Delay(Timeout.Infinite, token);
                }
                yield break;
            }
            foreach (var value in Streams[method])
            {
                token.ThrowIfCancellationRequested();
                yield return JsonSerializer.Deserialize<T>(value, ExecutionJson)!;
                if (WaitAfterEvent) await Task.Delay(Timeout.Infinite, token);
            }
        }
    }

    private static Task<List<JsonElement>> ReadGeneratedStream(object client, CancellationToken token, CancellationTokenSource? cancelAfterFirst,
        string methodName = "WatchAsync", JsonElement? requestValue = null)
    {
        var method = client.GetType().GetMethod(methodName)!;
        var stream = method.Invoke(client, ExtensionArguments(method, requestValue ?? EmptyRequest(), token))!;
        return (Task<List<JsonElement>>)typeof(AgentPublicSourceMigrationTests).GetMethod(nameof(ReadStream), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(method.ReturnType.GetGenericArguments()[0]).Invoke(null, [stream, token, cancelAfterFirst])!;
    }

    private static async Task<List<JsonElement>> ReadStream<T>(object stream, CancellationToken token, CancellationTokenSource? cancelAfterFirst)
    {
        var values = new List<JsonElement>();
        await using var enumerator = ((IAsyncEnumerable<T>)stream).GetAsyncEnumerator(token);
        while (await enumerator.MoveNextAsync())
        {
            values.Add(enumerator.Current switch
            {
                AgentToolDispatchedEvent => RunJson(new { kind = "dispatched" }),
                AgentToolExecutionCompletedEvent completed => RunJson(new { kind = "result", completed.Result }),
                AgentChatStreamEvent chat => await ChatEventSnapshotAsync(chat, token, received: true),
                _ => JsonSerializer.SerializeToElement(enumerator.Current, ExecutionJson),
            });
            if (cancelAfterFirst is null) continue;
            cancelAfterFirst.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            break;
        }
        return values;
    }

    private static Task<JsonElement> InvokeGeneratedClient(object client, string methodName, JsonElement request)
    {
        var method = client.GetType().GetMethod(methodName)
            ?? throw new InvalidOperationException($"Generated client '{client.GetType()}' has no '{methodName}' method.");
        var result = method.Invoke(client, ExtensionArguments(method, request, CancellationToken.None))!;
        if (method.ReturnType == typeof(ValueTask)) return SerializeGeneratedCompletion((ValueTask)result);
        var resultType = method.ReturnType.GetGenericArguments().Single();
        return (Task<JsonElement>)typeof(AgentPublicSourceMigrationTests)
            .GetMethod(nameof(SerializeGeneratedResult), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(resultType)
            .Invoke(null, [result])!;
    }

    private static object?[] ExtensionArguments(MethodInfo method, JsonElement request, CancellationToken token)
    {
        return method.GetParameters().Select(parameter =>
        {
            if (parameter.ParameterType == typeof(CancellationToken)) return (object)token;
            var value = request;
            if (request.ValueKind == JsonValueKind.Object)
            {
                if (request.TryGetProperty(parameter.Name!, out var named)) value = named;
                else if ((parameter.ParameterType == typeof(string) || parameter.ParameterType.IsPrimitive)
                    && request.TryGetProperty("value", out var scalar)) value = scalar;
                else if (!request.EnumerateObject().Any()
                    && new NullabilityInfoContext().Create(parameter).ReadState == NullabilityState.Nullable)
                    return null;
            }
            return JsonSerializer.Deserialize(value.GetRawText(), parameter.ParameterType, ExecutionJson);
        }).ToArray();
    }

    // Inspect already decoded fixture arguments, never encode/decode RPC values.
    // Reflection avoids asking the standalone JSON serializer to marshal a null
    // service/content property merely because its declared type is a resource.
    private static object? ObserveFixtureArgument(object? value)
    {
        if (value is null) return null;
        if (value is JsonElement || value is string || value is byte[]) return value;
        var type = value.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(SunderRpcOptional<>))
            return ObserveFixtureArgument(type.GetProperty("Value")!.GetValue(value));
        if (value is IDictionary dictionary)
        {
            var entries = new Dictionary<string, object?>();
            foreach (DictionaryEntry entry in dictionary) entries.Add((string)entry.Key, ObserveFixtureArgument(entry.Value));
            return entries;
        }
        if (value is IEnumerable sequence) return sequence.Cast<object?>().Select(ObserveFixtureArgument).ToArray();
        if (type.Namespace?.StartsWith("Sunder.Generated.Packages.", StringComparison.Ordinal) == true && !type.IsEnum)
        {
            var properties = new Dictionary<string, object?>();
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var member = property.GetValue(value);
                if (member is not null && member.GetType().IsGenericType
                    && member.GetType().GetGenericTypeDefinition() == typeof(SunderRpcOptional<>)
                    && !(bool)member.GetType().GetProperty("IsPresent")!.GetValue(member)!) continue;
                var name = property.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name
                    ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                properties.Add(name, ObserveFixtureArgument(member));
            }
            return properties;
        }
        return value;
    }

    private static async Task<JsonElement> SerializeGeneratedResult<T>(ValueTask<T> result)
        => JsonSerializer.SerializeToElement(await result, ExecutionJson);

    private static async Task<JsonElement> SerializeGeneratedCompletion(ValueTask result)
    {
        await result;
        return EmptyRequest();
    }
}
