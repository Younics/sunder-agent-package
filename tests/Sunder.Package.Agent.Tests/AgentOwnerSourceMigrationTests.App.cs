using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Sunder.Sdk.Rpc;
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
        Assert.Equal(20, contracts.Length);
        Assert.DoesNotContain(contracts, contract => contract.GetProperty("contractId").GetString() == "sunder.stack.contributor");
        Assert.All(contracts, contract => Assert.False(contract.TryGetProperty("runtimeUse", out _)));
        using var oracle = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repository, "compatibility/agent-rpc-migration-baseline.json")));
        var expected = oracle.RootElement.GetProperty("descriptors").EnumerateArray()
            .SelectMany(contract => contract.GetProperty("uses").EnumerateArray().Select(use => (Contract: contract, Use: use)))
            .Where(value => value.Use.GetProperty("ownerPackageId").GetString() == "sunder.package.agent"
                && value.Use.GetProperty("metadata").GetProperty("Role").GetString() == "app").ToArray();
        var uses = receipt.RootElement.GetProperty("uses").EnumerateArray().ToArray();
        Assert.Equal(3, uses.Length);
        Assert.Equal(expected.Length, uses.Length);
        Assert.Single(uses, use => use.GetProperty("required").GetBoolean());
        foreach (var use in uses)
        {
            var contract = Assert.Single(contracts, contract => contract.GetProperty("contractKey").GetString() == use.GetProperty("contractKey").GetString());
            var policy = Assert.Single(expected, value => value.Contract.GetProperty("contractId").GetString() == contract.GetProperty("contractId").GetString()).Use.GetProperty("metadata");
            Assert.Equal("app", use.GetProperty("role").GetString());
            Assert.Equal(bool.Parse(policy.GetProperty("Required").GetString()!), use.GetProperty("required").GetBoolean());
            Assert.Equal(policy.GetProperty("Actions").GetString()!.Split(';'), use.GetProperty("actions").EnumerateArray().Select(action => action.GetString()));
            Assert.Equal(policy.GetProperty("ContractVersion").GetString(), contract.GetProperty("contractVersion").GetString());
        }
    }

    // This adapter only selects typed operations on the real App client and
    // projects assertion values. Negotiation, ownership, content scopes, budgets
    // and invocation/stream lifetimes remain in the product client.
    private sealed class AppClient
    {
        private readonly Assembly _assembly;
        private readonly object _client;
        private readonly Type _operations;
        private static readonly IReadOnlyDictionary<string, string> Operations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["chat-snapshot"] = "ChatSnapshot", ["dashboard"] = "Dashboard", ["transcript-page"] = "Transcript", ["catalog"] = "Catalog",
            ["profile-command"] = "Profiles", ["workspace-command"] = "Workspaces", ["session-command"] = "SessionCommands",
            ["run-command"] = "Runs", ["run-status"] = "RunStatus", ["permission-command"] = "Permissions",
            ["attachment-upload"] = "AttachmentUploads", ["attachment-download"] = "AttachmentDownloads", ["attachment-discard"] = "AttachmentDiscards",
            ["history-search"] = "HistorySearch", ["history-state"] = "HistoryState", ["history-command"] = "HistoryCommands",
            ["transcript-around"] = "TranscriptAround", ["transcript-tool-detail"] = "TranscriptToolDetail", ["changes"] = "Changes", ["history-status"] = "HistoryStatus",
        };

        public AppClient(Assembly assembly, ISunderRpcClient rpc)
        {
            _assembly = assembly;
            _client = Activator.CreateInstance(assembly.GetType("Sunder.Package.Agent.Runtime.AgentOwnerRpcClient", true)!, rpc)!;
            _operations = assembly.GetType("Sunder.Package.Agent.Runtime.AgentRuntimeOperations", true)!;
        }

        public async Task<string> Call(string route, string json, CancellationToken token)
        {
            if (route == "transport-profile") return Serialize(await AgentRoleBoundary.Await(AgentRoleBoundary.Call(_client, "GetTransportProfileAsync", token)!));
            var operation = _operations.GetField(Operations[route])!.GetValue(null)!;
            var types = operation.GetType().GenericTypeArguments;
            var request = JsonSerializer.Deserialize(json, types[0], OwnerJson)!;
            var method = _client.GetType().GetMethod("InvokeAsync")!.MakeGenericMethod(types);
            return Serialize(await AgentRoleBoundary.Await(Invoke(method, [operation, request, token])));
        }

        public IAsyncEnumerable<string> Watch(string route, string json, CancellationToken token)
        {
            var operation = _operations.GetField(Operations[route])!.GetValue(null)!;
            var types = operation.GetType().GenericTypeArguments;
            var request = JsonSerializer.Deserialize(json, types[0], OwnerJson)!;
            var stream = Invoke(_client.GetType().GetMethod("SubscribeAsync")!.MakeGenericMethod(types), [operation, request, token]);
            return (IAsyncEnumerable<string>)typeof(AppClient).GetMethod(nameof(Events), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(types[1]).Invoke(null, [stream, token])!;
        }

        public async Task<string> Upload(byte[] bytes, CancellationToken token)
        {
            var type = _assembly.GetType("Sunder.Package.Agent.Contracts.Models.AgentAttachmentUploadRequest", true)!;
            var request = JsonSerializer.SerializeToElement(new { fileName = "owner.txt", mediaType = "text/plain", content = bytes }, OwnerJson).Deserialize(type, OwnerJson)!;
            return Serialize(await AgentRoleBoundary.Await(AgentRoleBoundary.Call(_client, "UploadAttachmentAsync", request, token)!));
        }
        public async Task<byte[]> Download(string metadata, CancellationToken token)
            => (byte[])(await AgentRoleBoundary.Await(InvokeContent("DownloadAttachmentAsync", metadata, token)))!;
        public async Task Discard(string handle, CancellationToken token)
            => await AgentRoleBoundary.Await(InvokeContent("DiscardAttachmentAsync", handle, token));
        public async Task<JsonElement> Invoke(string route, object request, CancellationToken token = default)
        { using var result = JsonDocument.Parse(await Call(route, Serialize(request), token)); return result.RootElement.Clone(); }
        private object InvokeContent(string name, string json, CancellationToken token)
        {
            var method = _client.GetType().GetMethod(name)!;
            return Invoke(method, [JsonSerializer.Deserialize(json, method.GetParameters()[0].ParameterType, OwnerJson), token]);
        }
        private object Invoke(MethodInfo method, object?[] arguments)
        {
            try { return method.Invoke(_client, arguments)!; }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        private static string Serialize(object? value) => JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), OwnerJson);
        private static async IAsyncEnumerable<string> Events<T>(IAsyncEnumerable<T> source, [EnumeratorCancellation] CancellationToken token)
        { await foreach (var item in source.WithCancellation(token).ConfigureAwait(false)) yield return Serialize(item); }
    }
}
