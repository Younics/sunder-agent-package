using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Package.Agent.Runtime;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentOwnerSourceMigrationTests
{
    private static void VerifyAppReceipt(string repository)
    {
        using var receipt = CanonicalAgentRoles.Receipt(app: true);
        Assert.Empty(receipt.RootElement.GetProperty("providers").EnumerateArray());
        Assert.Equal("app", receipt.RootElement.GetProperty("role").GetString());
        foreach (var member in new[] { "packageArtifacts", "packageArtifactImports", "packageApiImports", "providerImports", "platformImports" })
            Assert.Empty(receipt.RootElement.GetProperty(member).EnumerateArray());
        var contracts = receipt.RootElement.GetProperty("contracts").EnumerateArray().ToArray();
        Assert.Equal(21, contracts.Length);
        Assert.DoesNotContain(contracts, contract => contract.GetProperty("contractId").GetString() == "sunder.stack.contributor");
        Assert.All(contracts, contract => Assert.False(contract.TryGetProperty("runtimeUse", out _)));
        // The App role imports only its authored current closure. The Worker role
        // owns the remaining public extension uses.
        var expected = new Dictionary<string, (string ContractId, bool Required, string[] Actions)>(StringComparer.Ordinal)
        {
            ["child-run-executor"] = ("sunder.agent.child.run.executor", false, ["discover", "invoke"]),
            ["owner-runtime"] = ("sunder.agent.owner.runtime", true, ["discover", "invoke", "subscribe"]),
            ["runtime-catalog"] = ("sunder.agent.runtime.catalog", false, ["discover", "invoke", "subscribe"]),
            ["workspace-editor"] = ("sunder.agent.workspace.editor", false, ["discover", "invoke"]),
            ["agent-profile"] = ("sunder.package.agent.agent.profile", true, ["discover", "invoke"]),
        };
        var uses = receipt.RootElement.GetProperty("uses").EnumerateArray().ToArray();
        Assert.Equal(expected.Count, uses.Length);
        Assert.Equal(2, uses.Count(use => use.GetProperty("required").GetBoolean()));
        foreach (var use in uses)
        {
            var contract = Assert.Single(contracts, contract => contract.GetProperty("contractKey").GetString() == use.GetProperty("contractKey").GetString());
            var policy = expected[contract.GetProperty("contractKey").GetString()!];
            Assert.Equal("app", use.GetProperty("role").GetString());
            Assert.Equal(policy.ContractId, contract.GetProperty("contractId").GetString());
            Assert.Equal(policy.Required, use.GetProperty("required").GetBoolean());
            Assert.Equal(policy.Actions, use.GetProperty("actions").EnumerateArray().Select(action => action.GetString()));
            Assert.Equal(CurrentPrivateRpcDescriptors.Get(policy.ContractId).Version,
                contract.GetProperty("contractVersion").GetString());
        }
    }

    // This adapter only selects typed operations on the real App client and
    // projects assertion values. Negotiation, ownership, content scopes, budgets
    // and invocation/stream lifetimes remain in the product client.
    private sealed class AppClient : IAsyncDisposable
    {
        private readonly Assembly _assembly;
        private readonly object _client;
        private readonly object _resources;
        private readonly GeneratedServiceTestScope _receiver;
        private readonly JsonSerializerOptions _json;
        private Task? _dispose;
        private static readonly IReadOnlyDictionary<string, string> Operations = typeof(AgentOwnerRuntimeProvider)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.GetCustomAttribute<SunderMethodAttribute>() is not null)
            .ToDictionary(method => method.GetCustomAttribute<SunderMethodAttribute>()!.MethodId!, method => method.Name, StringComparer.Ordinal);

        public AppClient(Assembly assembly, ISunderRpcClient rpc)
        {
            _assembly = assembly;
            _receiver = new GeneratedServiceTestScope(rpc);
            _client = Activator.CreateInstance(assembly.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Internal.CSunder_Agent_Owner_RuntimeClient", true)!, rpc, _receiver)!;
            _json = (JsonSerializerOptions)_client.GetType().GetField("JsonOptions", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var contract = assembly.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Services.IAgentRuntimeService", true)!;
            _resources = Activator.CreateInstance(assembly.GetType("Sunder.Package.Agent.Runtime.AgentOwnerResources", true)!,
                _receiver.Providers(contract), rpc)!;
        }

        public async Task<string> Call(string route, string json, CancellationToken token)
        {
            if (route == "transport-profile") return Serialize(await AgentRoleBoundary.Await(AgentRoleBoundary.Call(_resources, "GetTransportProfileAsync", token)!));
            var method = _client.GetType().GetMethod(Operations[route])!;
            var requestType = method.GetParameters()[0].ParameterType;
            var request = JsonSerializer.Deserialize(json, requestType, _json)!;
            await PrepareAsync(requestType, request, token);
            return Serialize(await AgentRoleBoundary.Await(Invoke(_client, method, [request, token])));
        }

        public async IAsyncEnumerable<string> Watch(string route, string json, [EnumeratorCancellation] CancellationToken token)
        {
            var method = _client.GetType().GetMethod(Operations[route])!;
            var requestType = method.GetParameters()[0].ParameterType;
            var request = JsonSerializer.Deserialize(json, requestType, _json)!;
            await PrepareAsync(requestType, request, token);
            var stream = Invoke(_client, method, [request, token]);
            var values = (IAsyncEnumerable<string>)typeof(AppClient).GetMethod(nameof(Events), BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(method.ReturnType.GenericTypeArguments[0]).Invoke(this, [stream, token])!;
            await foreach (var value in values.WithCancellation(token)) yield return value;
        }

        public async Task<string> Upload(byte[] bytes, CancellationToken token)
        {
            var type = _assembly.GetType("Sunder.Package.Agent.Contracts.Models.AgentAttachmentUploadRequest", true)!;
            var request = Activator.CreateInstance(type, "owner.txt", "text/plain", bytes)!;
            return Serialize(await AgentRoleBoundary.Await(AgentRoleBoundary.Call(_resources, "UploadAttachmentAsync", request, token)!));
        }
        public async Task<byte[]> Download(string metadata, CancellationToken token)
            => (byte[])(await AgentRoleBoundary.Await(InvokeContent("DownloadAttachmentAsync", metadata, token)))!;
        public async Task Discard(string handle, CancellationToken token)
            => await AgentRoleBoundary.Await(InvokeContent("DiscardAttachmentAsync", handle, token));
        public async Task<JsonElement> Invoke(string route, object request, CancellationToken token = default)
        {
            // The negotiation probe has no request model. Do not ask the closed
            // product JSON graph to serialize a test-owned anonymous CLR type.
            string json;
            if (route == "transport-profile")
            {
                Assert.Empty(request.GetType().GetProperties());
                json = "{}";
            }
            else json = JsonSerializer.Serialize(request, request.GetType(), OwnerJson);
            using var result = JsonDocument.Parse(await Call(route, json, token));
            return result.RootElement.Clone();
        }
        private object InvokeContent(string name, string json, CancellationToken token)
        {
            var method = _resources.GetType().GetMethod(name)!;
            return Invoke(_resources, method, [JsonSerializer.Deserialize(json, method.GetParameters()[0].ParameterType, _json), token]);
        }
        private async Task PrepareAsync(Type requestType, object request, CancellationToken token)
        {
            var method = _assembly.GetType("Sunder.Package.Agent.Runtime.IAgentOwnerResources", true)!
                .GetMethod("PrepareAsync")!.MakeGenericMethod(requestType);
            await AgentRoleBoundary.Await(Invoke(_resources, method, [request, token]));
        }
        private static object Invoke(object target, MethodInfo method, object?[] arguments)
        {
            try { return method.Invoke(target, arguments)!; }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        private string Serialize(object? value) => JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), _json);
        private async IAsyncEnumerable<string> Events<T>(IAsyncEnumerable<T> source, [EnumeratorCancellation] CancellationToken token)
        { await foreach (var item in source.WithCancellation(token).ConfigureAwait(false)) yield return Serialize(item); }
        public ValueTask DisposeAsync() => new(_dispose ??= DisposeCoreAsync());
        private async Task DisposeCoreAsync()
        {
            try { await ((IAsyncDisposable)_client).DisposeAsync(); }
            finally { await _receiver.DisposeAsync(); }
        }
    }
}
