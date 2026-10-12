using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static async Task VerifyLMStudioRoles(string worker)
    {
        var load = new AssemblyLoadContext("lmstudio-role-local", isCollectible: true);
        try
        {
            using var workerStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(worker, "worker"), "Sunder.Package.Agent.Provider.LMStudio.dll"));
            var runtime = load.LoadFromStream(workerStream);
            var probes = await BuildProviderRoleProbes(worker, runtime, SingleProjectRoleOutput(worker, "worker"));
            await VerifyGeneratedProviderComposition(runtime, "LMStudio");
            var agentOwner = LoadAgentChatOwner(load, worker);
            using var appStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(worker, "app"), "Sunder.Package.Agent.Provider.LMStudio.App.dll"));
            var presentation = load.LoadFromStream(appStream);
            AssertProviderSemanticOwnership(runtime, presentation);
            foreach (var assembly in new[] { runtime, presentation })
                Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name == "Sunder.Package.Agent.Provider.LMStudio.Protocol");
            Assert.DoesNotContain(presentation.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Provider.LMStudio" or "Sunder.Sdk.Worker" or "OpenAI");
            using var package = RegressionTestPackageScope.Create();
            var context = new TestPackageContext("sunder.package.agent.provider.lmstudio", package.RootPath, version: "2.0.0", storage: package.Context.Storage);
            await context.Settings.SetValueAsync("connection.baseUrl", "http://127.0.0.1:43123/api/v1/");
            using var handler = new LMStudioArtifactHttp();
            var clock = new LMStudioArtifactClock();
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var rpc = new RegressionTestRpcClient(state, context.PackageId);
            var generated = CreateControlledGeneratedWorker(probes, context, rpc, services =>
            {
                var connection = runtime.GetType("Sunder.Package.Agent.Provider.LMStudio.LMStudioConnection", true)!;
                services.AddSingleton(connection, provider => AgentRoleBoundary.Create(connection, context,
                    ProviderCredentials(runtime, provider, "LMStudio"), handler, TimeSpan.FromSeconds(5)));
                var models = runtime.GetType("Sunder.Package.Agent.Provider.LMStudio.LMStudioModelCatalogService", true)!;
                services.AddSingleton(models, provider => AgentRoleBoundary.Create(models, provider.GetRequiredService(connection), null, clock));
            });
            await using var lifetime = generated.Lifetime;
            var options = generated.Options;
            Assert.NotNull(options.SettingsSchema); Assert.Empty(handler.Requests);
            var registrations = options.Providers.ToDictionary(value => value.ProviderId);
            Assert.Equal(new[] { "lmstudio.chat", "lmstudio.credentials", "lmstudio.embedding" }, registrations.Keys.Order(StringComparer.Ordinal));
            var credentialDescriptor = CurrentPrivateRpcDescriptors.Get("sunder.package.agent.provider.lmstudio.credentials");
            foreach (var provider in options.Providers)
            {
                var descriptor = provider.ProviderId == "lmstudio.credentials" ? credentialDescriptor : CurrentPublicRpcDescriptors.Get(provider.ContractId);
                Assert.Equal(descriptor.Sha256, provider.DescriptorSha256); Assert.Equal(descriptor.AbiSha256, provider.AbiSha256);
                state.Add(context.PackageId, descriptor, provider.Handler, provider.Handler, Noop.Instance, provider.ProviderId, "2.0.0");
            }
            await using var appServices = CreateGeneratedApp(presentation, context, rpc);
            var credentials = appServices.GetRequiredService(presentation.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Provider_Lmstudio_Services.ICredentialSettingsService", true)!);
            Assert.False(await Credential("GetStatusAsync", [CancellationToken.None]));
            foreach (var kind in new[] { "chat", "embedding" })
            {
                var descriptor = await Rpc(kind, "describe", new { });
                Assert.Equal("lmstudio", descriptor.GetProperty("providerId").GetString());
                Assert.Empty(descriptor.GetProperty("supportedAuthModes").EnumerateArray());
            }
            Assert.Empty(handler.Requests);
            foreach (var kind in new[] { "chat", "embedding" })
                Assert.Equal("ready", (await Rpc(kind, "get-readiness", new { })).GetProperty("status").GetString());
            Assert.Equal(1, handler.ModelRequests);
            Assert.Null(handler.Requests.Last().Authorization);
            var models = await Rpc("chat", "list-models", new { });
            Assert.Equal(new[] { "lmstudio/chat-model", "lmstudio/mystery-model", "lmstudio/both-model" }, models.EnumerateArray().Select(value => value.GetProperty("modelId").GetString()));
            Assert.Equal(8192, models[0].GetProperty("contextWindow").GetInt32());
            Assert.Equal(1536, models[0].GetProperty("maxOutputTokens").GetInt32());
            var embeddings = await Rpc("embedding", "list-models", new { });
            Assert.Equal(2, embeddings.GetArrayLength());
            Assert.Equal(3, Assert.Single(embeddings.EnumerateArray(), value => value.GetProperty("modelId").GetString() == "lmstudio/embedding-model").GetProperty("dimensions").GetInt32());
            Assert.Equal("lmstudio/chat-model", (await Rpc("chat", "resolve-utility-model", new { })).GetString());
            Assert.True((await Rpc("chat", "get-capabilities", "lmstudio/chat-model")).GetProperty("supportsStreamingToolCalls").GetBoolean());
            Assert.Equal("http://127.0.0.1:43123/api/v1", (await Rpc("embedding", "space-identity", "lmstudio/embedding-model")).GetString());
            Assert.Equal(1, handler.ModelRequests);

            var viewModel = appServices.GetRequiredService(presentation.GetType("Sunder.Package.Agent.Provider.LMStudio.LMStudioSettingsViewModel", true)!);
            await ((Task)viewModel.GetType().GetMethod("InitializeAsync")!.Invoke(viewModel, [CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True((bool)viewModel.GetType().GetProperty("IsReady")!.GetValue(viewModel)!);
            viewModel.GetType().GetProperty("BaseUrl")!.SetValue(viewModel, "not-a-url");
            await Command("SaveConnectionCommand");
            Assert.Equal("http://127.0.0.1:43123/api/v1/", await context.Settings.GetValueAsync("connection.baseUrl"));
            viewModel.GetType().GetProperty("BaseUrl")!.SetValue(viewModel, " http://127.0.0.1:43123/alternate/v1/ ");
            await Command("SaveConnectionCommand");
            Assert.Equal("http://127.0.0.1:43123/alternate/v1", await context.Settings.GetValueAsync("connection.baseUrl"));
            viewModel.GetType().GetProperty("UtilityModelId")!.SetValue(viewModel, " utility-model ");
            await Command("SaveUtilityModelCommand");
            Assert.Equal("lmstudio/utility-model", (await Rpc("chat", "resolve-utility-model", new { })).GetString());
            await context.Settings.DeleteValueAsync("utility.modelId");
            await Rpc("chat", "list-models", new { }); Assert.Equal(2, handler.ModelRequests);
            Assert.EndsWith("/alternate/v1/models", handler.Requests.Last().Uri.AbsoluteUri, StringComparison.Ordinal);
            Assert.True(await Credential("SetAsync", ["  artifact-lmstudio-key  ", CancellationToken.None]));
            Assert.Equal("artifact-lmstudio-key", await context.Secrets.GetSecretAsync("connection.apiKey"));
            await Rpc("embedding", "list-models", new { }); Assert.Equal(3, handler.ModelRequests);
            Assert.Equal("Bearer artifact-lmstudio-key", handler.Requests.Last().Authorization);

            await VerifyLMStudioCatalog(context, handler, clock, Rpc);
            await VerifyLMStudioEmbeddings(handler, Rpc);
            var chatProvider = await CreateAgentChatProvider(agentOwner, rpc.ForCaller("sunder.package.agent"), Assert.Single((await rpc.DiscoverAsync(RegressionTestBindings.ChatProviders)).Providers));
            await using var chatLifetime = (IAsyncDisposable)chatProvider;
            using var chat = await CreateAgentChatClientAsync(chatProvider, new("lmstudio", "lmstudio/chat-model"));
            var tool = AIFunctionFactory.CreateDeclaration("read_file", "Read a file", JsonSerializer.SerializeToElement(new
            { type = "object", properties = new { path = new { type = "string" } }, required = new[] { "path" } }));
            var chatOptions = new ChatOptions { Instructions = "Use tools safely.", MaxOutputTokens = 321, AllowMultipleToolCalls = true, Tools = [tool] };
            ChatMessage[] messages = [new(ChatRole.Assistant, [new FunctionCallContent("saved-call", "read_file", new Dictionary<string, object?> { ["path"] = "saved.txt" })]),
                new(ChatRole.Tool, [new FunctionResultContent("saved-call", "saved result")]), new(ChatRole.User, "request-canary")];
            var updates = await Read(messages, chatOptions);
            Assert.Equal("## Heading\n\n- item", string.Concat(updates.SelectMany(value => value.Contents).OfType<TextContent>().Select(value => value.Text)));
            var usage = Assert.Single(updates.SelectMany(value => value.Contents).OfType<UsageContent>()).Details;
            Assert.Equal(12, usage.InputTokenCount); Assert.Equal(7, usage.OutputTokenCount); Assert.Equal(19, usage.TotalTokenCount);
            Assert.Equal(4, usage.CachedInputTokenCount); Assert.Equal(3, usage.ReasoningTokenCount);
            var request = handler.Requests.Last();
            Assert.Equal("Bearer artifact-lmstudio-key", request.Authorization);
            Assert.Equal("chat-model", request.Body!.Value.GetProperty("model").GetString());
            Assert.Equal(321, request.Body.Value.GetProperty("max_completion_tokens").GetInt32());
            Assert.True(request.Body.Value.GetProperty("parallel_tool_calls").GetBoolean());
            Assert.Equal(new[] { "system", "assistant", "tool", "user" }, request.Body.Value.GetProperty("messages").EnumerateArray().Select(value => value.GetProperty("role").GetString()));
            Assert.Equal("saved-call", request.Body.Value.GetProperty("messages")[2].GetProperty("tool_call_id").GetString());
            Assert.Equal("path", request.Body.Value.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters").GetProperty("required")[0].GetString());
            handler.ChatMode = "tools";
            updates = await Read(options: chatOptions);
            var content = updates.SelectMany(value => value.Contents).Where(value => value is not UsageContent).ToArray();
            Assert.Equal("Before", Assert.IsType<TextContent>(content[0]).Text);
            var call = Assert.IsType<FunctionCallContent>(content[1]);
            Assert.Equal("call.artifact", call.CallId); Assert.Equal("read_file", call.Name);
            Assert.Equal("README.md", Assert.IsType<JsonElement>(call.Arguments!["path"]).GetString());
            Assert.Equal("After", Assert.IsType<TextContent>(content[2]).Text);
            foreach (var (mode, code) in new[] { ("eof", "lmstudio-incomplete-response"), ("malformed-tool", "lmstudio-malformed-tool-call"), ("context-limit", "lmstudio-sdk-error") })
            {
                handler.ChatMode = mode;
                var error = await AssertRoleChatFailure(agentOwner, () => Read(options: chatOptions));
                Assert.Equal(code, error.ErrorCode);
                if (mode == "context-limit") Assert.Equal(AgentChatProviderFailureKind.ContextWindowExceeded, error.FailureKind);
                Assert.DoesNotContain("artifact-lmstudio-key", error.ToString(), StringComparison.Ordinal);
            }
            handler.ChatMode = "normal";
            var blockedChat = handler.BlockNext("chat/completions");
            using (var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                var pending = Read(token: cancelled.Token);
                await blockedChat.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                await blockedChat.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Credential("SetAsync", ["not-stored", cancelled.Token]));
            }
            Assert.True(await Credential("SetAsync", ["rotated-key", CancellationToken.None])); await Read();
            Assert.Equal("Bearer rotated-key", handler.Requests.Last().Authorization);
            await context.Settings.SetValueAsync("connection.baseUrl", "http://remote.test/v1");
            var requestCount = handler.Requests.Count;
            foreach (var kind in new[] { "chat", "embedding" })
                Assert.Equal("needs-configuration", (await Rpc(kind, "get-readiness", new { })).GetProperty("status").GetString());
            Assert.Equal("lmstudio-base-url-invalid", (await AssertRoleChatFailure(agentOwner, () => Read())).ErrorCode);
            Assert.Equal(requestCount, handler.Requests.Count);
            await context.Settings.SetValueAsync("connection.baseUrl", "https://rotated.test/v1/"); await Read();
            Assert.Equal("rotated.test", handler.Requests.Last().Uri.Host);
            Assert.Equal("https://rotated.test/v1", (await Rpc("embedding", "space-identity", "lmstudio/embedding-model")).GetString());
            Assert.False(await Credential("ClearAsync", [CancellationToken.None])); await Read();
            Assert.Null(handler.Requests.Last().Authorization); Assert.False(handler.IsDisposed);
            var oldCredentials = Assert.Single((await rpc.DiscoverAsync(credentialDescriptor.ContractId)).Providers).Endpoint;
            var credentialRegistration = registrations["lmstudio.credentials"];
            await state.RetireAsync(credentialRegistration.Handler).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("rpc.provider-not-found", (await Assert.ThrowsAsync<SunderRpcException>(() => Credential("GetStatusAsync", [CancellationToken.None]))).Error.Code);
            state.Add(context.PackageId, credentialDescriptor, credentialRegistration.Handler, credentialRegistration.Handler, Noop.Instance, credentialRegistration.ProviderId, "2.0.0");
            Assert.NotEqual(oldCredentials, Assert.Single((await rpc.DiscoverAsync(credentialDescriptor.ContractId)).Providers).Endpoint);
            Assert.False(await Credential("GetStatusAsync", [CancellationToken.None]));
            await VerifyProviderStreamLifecycle(runtime, "LMStudio", agentOwner, probes);
            await lifetime.DisposeAsync(); await lifetime.DisposeAsync(); Assert.True(handler.IsDisposed);

            async Task<bool> Credential(string name, object[] args)
            {
                var value = credentials.GetType().GetMethod(name)!.Invoke(credentials, args)!;
                var task = (Task)value.GetType().GetMethod("AsTask")!.Invoke(value, null)!;
                await task.WaitAsync(TimeSpan.FromSeconds(10));
                var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
                return Assert.IsType<bool>(result);
            }
            async Task<JsonElement> Rpc(string kind, string method, object request, CancellationToken token = default)
            {
                var selected = Assert.Single((await rpc.DiscoverAsync(registrations["lmstudio." + kind].ContractId, token)).Providers);
                return await rpc.InvokeAsync(selected.Endpoint, kind + "-provider", method, JsonSerializer.SerializeToElement(request, MemoryProviderJson), cancellationToken: token).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }
            Task Command(string name) => ((IAsyncRelayCommand)viewModel.GetType().GetProperty(name)!.GetValue(viewModel)!).ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));
            async Task<List<ChatResponseUpdate>> Read(ChatMessage[]? input = null, ChatOptions? options = null, CancellationToken token = default)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
                var result = new List<ChatResponseUpdate>();
                await foreach (var update in chat.GetStreamingResponseAsync(input ?? [new(ChatRole.User, "request-canary")], options, deadline.Token)) result.Add(update);
                return result;
            }
        }
        finally { load.Unload(); }
    }
}
