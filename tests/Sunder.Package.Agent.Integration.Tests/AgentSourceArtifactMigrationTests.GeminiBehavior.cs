using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using Google.GenAI;
using HttpOptions = Google.GenAI.Types.HttpOptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static async Task VerifyGeminiRoles(string worker)
    {
        var load = new AssemblyLoadContext("gemini-role-local", isCollectible: true);
        try
        {
            using var workerStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(worker, "worker"), "Sunder.Package.Agent.Provider.Gemini.dll"));
            var runtime = load.LoadFromStream(workerStream);
            var probes = await BuildProviderRoleProbes(worker, runtime, SingleProjectRoleOutput(worker, "worker"));
            await VerifyGeneratedProviderComposition(runtime, "Gemini");
            using var ownerStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(Path.Combine(Path.GetDirectoryName(worker)!, "Worker"), "worker"), "Sunder.Package.Agent.dll"));
            var agentOwner = load.LoadFromStream(ownerStream);
            await VerifyAgentEmbeddingConsumer(agentOwner, runtime, probes);
            using var appStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(worker, "app"), "Sunder.Package.Agent.Provider.Gemini.App.dll"));
            var presentation = load.LoadFromStream(appStream);
            AssertProviderSemanticOwnership(runtime, presentation);
            foreach (var assembly in new[] { runtime, presentation })
                Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name == "Sunder.Package.Agent.Provider.Gemini.Protocol");
            Assert.DoesNotContain(presentation.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Provider.Gemini" or "Sunder.Sdk.Worker" or "Google.GenAI");
            using var package = RegressionTestPackageScope.Create();
            var context = new TestPackageContext("younics.ai.agent.provider.gemini", package.RootPath, version: "2.0.0", storage: package.Context.Storage);
            await using var peer = new GeminiLoopbackPeer();
            var factoryCalls = new List<string>();
            Func<string, Client> factory = key => { factoryCalls.Add(key); return new(apiKey: key, httpOptions: new HttpOptions { BaseUrl = peer.Endpoint }); };
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var rpc = new RegressionTestRpcClient(state, "younics.ai.agent.provider.gemini");
            var generated = CreateControlledGeneratedWorker(probes, context, rpc,
                services => ConfigureProviderFactory(runtime, services, context, "Gemini", factory));
            await using var lifetime = generated.Lifetime;
            var options = generated.Options;
            Assert.NotNull(options.SettingsSchema); Assert.Empty(factoryCalls);
            var registrations = options.Providers.ToDictionary(value => value.ProviderId);
            Assert.Equal(new[] { "gemini.chat", "gemini.credentials", "gemini.embedding" }, registrations.Keys.Order(StringComparer.Ordinal));
            var credentialDescriptor = CurrentPrivateRpcDescriptors.Get("younics.ai.agent.provider.gemini.credentials");
            foreach (var provider in options.Providers)
            {
                var descriptor = provider.ProviderId == "gemini.credentials" ? credentialDescriptor : CurrentPublicRpcDescriptors.Get(provider.ContractId);
                Assert.Equal(descriptor.Sha256, provider.DescriptorSha256); Assert.Equal(descriptor.AbiSha256, provider.AbiSha256);
                state.Add(context.PackageId, descriptor, provider.Handler, provider.Handler, Noop.Instance, provider.ProviderId, "2.0.0");
            }
            await using var appServices = CreateGeneratedApp(presentation, context, rpc);
            var credentials = appServices.GetRequiredService(presentation.GetType("Sunder.Generated.Packages.Younics_Ai_Agent_Provider_Gemini_Services.ICredentialSettingsService", true)!);
            Assert.False(await Credential("GetStatusAsync", [CancellationToken.None]));
            foreach (var id in new[] { "gemini.chat", "gemini.embedding" })
                Assert.Equal("needs-configuration", (await Rpc(id, "get-readiness", new { })).GetProperty("status").GetString());
            Assert.True(await Credential("SetAsync", ["  artifact-secret  ", CancellationToken.None]));
            Assert.Equal("artifact-secret", await context.Secrets.GetSecretAsync("auth.apiKey"));
            foreach (var id in new[] { "gemini.chat", "gemini.embedding" })
            {
                Assert.Equal("ready", (await Rpc(id, "get-readiness", new { })).GetProperty("status").GetString());
                Assert.Equal("gemini", (await Rpc(id, "describe", new { })).GetProperty("providerId").GetString());
                Assert.NotEmpty((await Rpc(id, "list-models", new { })).EnumerateArray());
            }
            Assert.Equal("gemini/gemini-2.5-flash", (await Rpc("gemini.chat", "resolve-utility-model", new { })).GetString());
            Assert.True((await Rpc("gemini.chat", "get-capabilities", "gemini/gemini-3-flash-preview")).GetProperty("supportsVideoInput").GetBoolean());
            Assert.Equal("https://generativelanguage.googleapis.com", (await Rpc("gemini.embedding", "space-identity", "gemini/text-embedding-004")).GetString());
            Assert.Equal(JsonValueKind.Null, (await Rpc("gemini.embedding", "generate", new { modelId = "gemini/text-embedding-004", text = " " })).ValueKind);
            Assert.Empty(factoryCalls);
            var batch = await Rpc("gemini.embedding", "generate-batch", new { modelId = "gemini/text-embedding-004", texts = new[] { "first", " ", "second" } });
            Assert.Equal(3, batch.GetArrayLength()); Assert.Equal(JsonValueKind.Null, batch[1].ValueKind);
            Assert.True(batch[0].ValueKind == JsonValueKind.Object, "Embedding response: " + batch.GetRawText() + "; requests: " + string.Join(", ", peer.Requests.Select(value => value.Path)));
            Assert.Equal(3, batch[0].GetProperty("values").GetArrayLength()); Assert.Equal(0.25, batch[2].GetProperty("values")[0].GetDouble());
            Assert.False(batch[0].TryGetProperty("dimensions", out _));
            Assert.Equal("gemini/text-embedding-004", batch[0].GetProperty("modelId").GetString());
            var embeddingCatalog = CreateAgentEmbeddingCatalog(agentOwner, new RegressionTestRpcClient(state, "younics.ai.agent"));
            await using var embeddingLifetime = (IAsyncDisposable)embeddingCatalog;
            var generatedEmbedding = EmbeddingService(await SingleAgentEmbeddingProvider(embeddingCatalog));
            Assert.Equal(AgentProviderReadinessStatus.Ready, (await generatedEmbedding.GetReadinessAsync()).Status);
            var generatedBatch = await generatedEmbedding.GenerateBatchAsync("gemini/text-embedding-004", ["first", " ", "second"]);
            Assert.Equal(3, generatedBatch.Count); Assert.Null(generatedBatch[1]);
            Assert.Equal(3, generatedBatch[0]!.Values.Count);
            Assert.Equal(0.25, generatedBatch[2]!.Values[0]);
            var chatProvider = await CreateAgentChatProvider(agentOwner, rpc.ForCaller("younics.ai.agent"), Assert.Single((await rpc.DiscoverAsync(RegressionTestBindings.ChatProviders)).Providers));
            await using var chatLifetime = (IAsyncDisposable)chatProvider;
            using var chat = await CreateAgentChatClientAsync(chatProvider, new("gemini", "gemini/gemini-3-flash-preview"));
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in chat.GetStreamingResponseAsync([
                new ChatMessage(ChatRole.System, "System instruction"),
                new ChatMessage(ChatRole.User, [new TextContent("before"), new DataContent(new byte[] { 1, 2, 3 }, "image/png") { Name = "fixture.png" }, new TextContent("after")])],
                new ChatOptions { Instructions = "Option instruction", MaxOutputTokens = 321, Reasoning = new() { Effort = ReasoningEffort.Medium } })) updates.Add(update);
            Assert.Contains(updates.SelectMany(value => value.Contents).OfType<TextContent>(), content => content.Text == "artifact response");
            var request = peer.Requests.Last();
            Assert.Contains(":streamGenerateContent", request.Path, StringComparison.Ordinal);
            Assert.Equal(321, request.Body.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32());
            Assert.Equal("MEDIUM", request.Body.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString());
            var parts = request.Body.GetProperty("contents")[0].GetProperty("parts");
            Assert.Equal("before", parts[0].GetProperty("text").GetString());
            Assert.Equal("AQID", parts[1].GetProperty("inlineData").GetProperty("data").GetString());
            Assert.Equal("after", parts[2].GetProperty("text").GetString());
            var tool = AIFunctionFactory.CreateDeclaration("read_file", "Read", JsonSerializer.SerializeToElement(new { type = "object", properties = new { path = new { type = "string" } } }));
            updates.Clear();
            await foreach (var update in chat.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "read")], new ChatOptions { Tools = [tool] })) updates.Add(update);
            var content = Assert.Single(updates).Contents;
            Assert.Equal("AQI=", Assert.IsType<TextReasoningContent>(content[0]).ProtectedData);
            Assert.Equal("read_file", Assert.IsType<FunctionCallContent>(content[1]).Name);
            Assert.Equal("call.artifact", Assert.IsType<FunctionCallContent>(content[1]).CallId);
            Assert.All(factoryCalls, key => Assert.Equal("artifact-secret", key));
            var viewModel = appServices.GetRequiredService(presentation.GetType("Sunder.Package.Agent.Provider.Gemini.GeminiSettingsViewModel", true)!);
            await ((Task)viewModel.GetType().GetMethod("InitializeAsync")!.Invoke(viewModel, [CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(10));
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Credential("SetAsync", ["not-stored", cancelled.Token]));
            }
            Assert.Equal("artifact-secret", await context.Secrets.GetSecretAsync("auth.apiKey"));
            Assert.False(await Credential("ClearAsync", [CancellationToken.None]));
            var denied = await Assert.ThrowsAsync<SunderRpcException>(() => Rpc("gemini.embedding", "generate", new { modelId = "gemini/text-embedding-004", text = "blocked" }));
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, denied.Error.Kind);
            Assert.DoesNotContain("artifact-secret", denied.ToString(), StringComparison.Ordinal);
            await state.RetireAsync(registrations["gemini.credentials"].Handler).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("rpc.provider-not-found", (await Assert.ThrowsAsync<SunderRpcException>(() => Credential("GetStatusAsync", [CancellationToken.None]))).Error.Code);
            await VerifyProviderStreamLifecycle(runtime, "Gemini", agentOwner, probes);
            await lifetime.DisposeAsync(); await lifetime.DisposeAsync();

            async Task<bool> Credential(string name, object[] args)
            {
                var value = credentials.GetType().GetMethod(name)!.Invoke(credentials, args)!;
                var task = (Task)value.GetType().GetMethod("AsTask")!.Invoke(value, null)!;
                await task.WaitAsync(TimeSpan.FromSeconds(10));
                var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
                return Assert.IsType<bool>(result);
            }
            async Task<JsonElement> Rpc(string id, string method, object request)
            {
                var selected = Assert.Single((await rpc.DiscoverAsync(registrations[id].ContractId)).Providers);
                return await rpc.InvokeAsync(selected.Endpoint, id == "gemini.chat" ? "chat-provider" : "embedding-provider", method,
                    JsonSerializer.SerializeToElement(request, MemoryProviderJson)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally { load.Unload(); }
    }
}
