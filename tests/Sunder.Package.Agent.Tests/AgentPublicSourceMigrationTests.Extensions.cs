using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private static async Task VerifyExtensionsAsync(string root, AssemblyLoadContext loadContext, Assembly assembly,
        IReadOnlyDictionary<string, SunderRpcContractDescriptor> descriptors)
    {
        await using (var fixture = await CreateExtension(root, loadContext, assembly, "WorkspaceEditor", descriptors["sunder.agent.workspace.editor"]))
        {
            var workspace = CreateResolver(fixture.Rpc).Workspace;
            var context = JsonSerializer.SerializeToElement(new { configurationId = "configuration.one", targetId = "local", workspace }, ExecutionJson);
            fixture.Probe.Replies["CanEditAsync"] = "true";
            Assert.True((await fixture.Call("CanEditAsync", context)).GetBoolean());
            fixture.Probe.Replies["GetSectionsAsync"] = """
                [{"sectionId":"paths","title":"Paths 漢字","fields":[{"fieldId":"paths","kind":"path-list","label":"Paths","useFolderPicker":true,"useSecondaryFolderPicker":false,"actions":[{"actionId":"open","kind":"open-package-settings","label":"Open","parameters":{"reset":null,"category":"files"}}],"items":[{"isDefault":false,"itemId":"one","value":"/work/one"}]}]}]
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
            Assert.False(received.TryGetProperty("value", out _));
            Assert.False(received.GetProperty("items")[0].GetProperty("isDefault").GetBoolean());
            var calls = fixture.Probe.Calls;
            var invalid = JsonSerializer.SerializeToElement(new { configurationId = "", targetId = "local", workspace }, ExecutionJson);
            var validation = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Call("CanEditAsync", invalid));
            Assert.Equal(SunderRpcErrorKind.Validation, validation.Error.Kind);
            Assert.Equal(calls, fixture.Probe.Calls);
        }

        await using (var fixture = await CreateExtension(root, loadContext, assembly, "PermissionSurface", descriptors["sunder.agent.permission.surface"]))
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

        await using (var fixture = await CreateExtension(root, loadContext, assembly, "ProfileCapabilityConsumer", descriptors["sunder.agent.profile.capability.consumer"]))
        {
            fixture.Probe.Replies["DescribeAsync"] = """
                {"capabilities":[{"capabilityKind":"memory","description":"","displayName":"Memory"}],"consumerId":"memory.one","displayName":"Memory consumer"}
                """;
            var result = await fixture.Call("DescribeAsync", EmptyRequest());
            Assert.Equal("", result.GetProperty("capabilities")[0].GetProperty("description").GetString());
        }

        await using (var fixture = await CreateExtension(root, loadContext, assembly, "SelectableCapabilityProvider", descriptors["sunder.agent.selectable.capability.provider"]))
        {
            fixture.Probe.Replies["ListAsync"] = """
                [{"capabilityId":"tool.one","displayName":"Tool","groupSortOrder":0,"isSelectable":false,"kind":"tool"}]
                """;
            var request = JsonSerializer.SerializeToElement(new { profile = new
            {
                createdAtUtc = "2026-09-18T00:00:00Z", displayName = "Profile", isInternal = false,
                profileId = "default", updatedAtUtc = "2026-09-18T00:00:00Z", behaviorLoopSettingsJson = "{broken",
            } });
            var result = await fixture.Call("ListAsync", request);
            Assert.False(result[0].GetProperty("isSelectable").GetBoolean());
            Assert.False(result[0].TryGetProperty("description", out _));
            Assert.Equal("{broken", fixture.Probe.LastRequest.GetProperty("profile").GetProperty("behaviorLoopSettingsJson").GetString());
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

        await using (var fixture = await CreateExtension(root, loadContext, assembly, "EmbeddingProvider", descriptors["sunder.agent.embedding.provider"]))
        {
            fixture.Probe.Replies["DescribeAsync"] = """
                {"displayName":"Embedding","providerId":"embedding.one","supportedAuthModes":["api-key","codex-connected"]}
                """;
            Assert.Equal("codex-connected", (await fixture.Call("DescribeAsync", EmptyRequest())).GetProperty("supportedAuthModes")[1].GetString());
            fixture.Probe.Replies["ListModelsAsync"] = "[{\"displayName\":\"Model\",\"isRecommended\":false,\"modelId\":\"model.one\"}]";
            Assert.False((await fixture.Call("ListModelsAsync", EmptyRequest()))[0].TryGetProperty("dimensions", out _));
            fixture.Probe.Replies["SpaceIdentityAsync"] = "\"space:v1\"";
            Assert.Equal("space:v1", (await fixture.Call("SpaceIdentityAsync", JsonSerializer.SerializeToElement(new { value = "model.one" }))).GetString());
            fixture.Probe.Replies["GenerateAsync"] = "null";
            Assert.Equal(JsonValueKind.Null, (await fixture.Call("GenerateAsync", JsonSerializer.SerializeToElement(new { modelId = "model.one", text = "Hello" }))).ValueKind);
            fixture.Probe.Replies["GenerateBatchAsync"] = """
                [null,{"dimensions":2,"modelId":"model.one","values":[0.125,-0.5]},null]
                """;
            var request = JsonSerializer.SerializeToElement(new { modelId = "model.one", texts = new[] { "first", "second", "third" } });
            var result = await fixture.Call("GenerateBatchAsync", request);
            Assert.Equal(3, result.GetArrayLength());
            Assert.Equal(JsonValueKind.Null, result[0].ValueKind);
            Assert.Equal(JsonValueKind.Null, result[2].ValueKind);
            Assert.Equal([0.125, -0.5], result[1].GetProperty("values").EnumerateArray().Select(value => value.GetDouble()).ToArray());
            fixture.Probe.Replies["GenerateBatchAsync"] = "[{\"dimensions\":0,\"modelId\":\"model.one\",\"values\":[]}]";
            var invalid = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Call("GenerateBatchAsync", request));
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, invalid.Error.Kind);
        }
    }

    private static JsonElement EmptyRequest() => JsonSerializer.SerializeToElement(new { });

    private static async Task<ExtensionFixture> CreateExtension(string root, AssemblyLoadContext loadContext, Assembly assembly,
        string service, SunderRpcContractDescriptor descriptor)
    {
        var prefix = "Sunder.Generated.Rpc." + service + ".";
        var providerType = assembly.GetType(prefix + "I" + service + "Provider", true)!;
        var probe = new ExtensionProbe();
        var provider = await BuildGeneratedProvider(root, loadContext, providerType, probe.Invoke);
        var handler = Assert.IsAssignableFrom<ISunderRpcServiceHandler>(Activator.CreateInstance(
            assembly.GetType(prefix + "SunderAgent" + service + "RpcHandler", true)!, provider));
        var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        state.Add("test.extension.owner", descriptor, provider, handler, EmptyDisposable.Instance, "test.extension.provider", "2.0.0");
        var selected = Assert.Single((await rpc.DiscoverAsync(descriptor.ContractId)).Providers);
        var scope = await rpc.CreateCallScopeAsync();
        var client = Activator.CreateInstance(assembly.GetType(prefix + service + "Client", true)!, scope, selected.Endpoint)!;
        return new(state, rpc, scope, client, probe);
    }

    private sealed class ExtensionFixture(RegressionTestRpcState state, RegressionTestRpcClient rpc,
        ISunderRpcCallScope scope, object client, ExtensionProbe probe) : IAsyncDisposable
    {
        public RegressionTestRpcClient Rpc { get; } = rpc;
        public object Client { get; } = client;
        public ExtensionProbe Probe { get; } = probe;
        public Task<JsonElement> Call(string method, JsonElement request) => InvokeGeneratedClient(Client, method, request);
        public async ValueTask DisposeAsync() { await scope.DisposeAsync(); state.Dispose(); }
    }

    private sealed class ExtensionProbe
    {
        public Dictionary<string, string> Replies { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string[]> Streams { get; } = new(StringComparer.Ordinal);
        public JsonElement LastRequest { get; private set; }
        public int Calls { get; private set; }
        public bool WaitAfterEvent { get; set; }
        public bool ForgedCancellation { get; set; }
        public object Invoke(string method, Type resultType, object? request, CancellationToken token)
        {
            LastRequest = JsonSerializer.SerializeToElement(request, ExecutionJson);
            return typeof(ExtensionProbe).GetMethod(Streams.ContainsKey(method) ? nameof(Stream) : nameof(Reply), BindingFlags.Instance | BindingFlags.NonPublic)!
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
        var request = JsonSerializer.Deserialize(requestValue?.GetRawText() ?? "{}", method.GetParameters()[0].ParameterType, ExecutionJson);
        var stream = method.Invoke(client, [request, token])!;
        return (Task<List<JsonElement>>)typeof(AgentPublicSourceMigrationTests).GetMethod(nameof(ReadStream), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(method.ReturnType.GetGenericArguments()[0]).Invoke(null, [stream, token, cancelAfterFirst])!;
    }

    private static async Task<List<JsonElement>> ReadStream<T>(object stream, CancellationToken token, CancellationTokenSource? cancelAfterFirst)
    {
        var values = new List<JsonElement>();
        await using var enumerator = ((IAsyncEnumerable<T>)stream).GetAsyncEnumerator(token);
        while (await enumerator.MoveNextAsync())
        {
            values.Add(JsonSerializer.SerializeToElement(enumerator.Current, ExecutionJson));
            if (cancelAfterFirst is null) continue;
            cancelAfterFirst.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            break;
        }
        return values;
    }
}
