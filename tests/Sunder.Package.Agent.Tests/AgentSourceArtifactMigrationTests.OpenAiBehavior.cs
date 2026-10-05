using System.ClientModel;
using System.ClientModel.Primitives;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Authentication;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static async Task VerifyOpenAiRoles(string worker)
    {
        var load = new AssemblyLoadContext("openai-role-local", isCollectible: true);
        try
        {
            using var workerStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(worker, "worker"), "Sunder.Package.Agent.Provider.OpenAI.dll"));
            var runtime = load.LoadFromStream(workerStream);
            var probes = await BuildProviderRoleProbes(worker, runtime, SingleProjectRoleOutput(worker, "worker"));
            await VerifyGeneratedOpenAiComposition(runtime);
            var agentOwner = LoadAgentChatOwner(load, worker);
            using var appStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(worker, "app"), "Sunder.Package.Agent.Provider.OpenAI.App.dll"));
            var presentation = load.LoadFromStream(appStream);
            AssertProviderSemanticOwnership(runtime, presentation);
            foreach (var assembly in new[] { runtime, presentation })
            {
                Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name == "Sunder.Package.Agent.Provider.OpenAI.Shared");
                foreach (var name in new[] { "OpenAiAuthOperationResult", "IOpenAiOwnerManagementClient", "OpenAiUtilityModelSelection", "OpenAiAuthMode", "OpenAiModelCatalog" })
                    Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Provider.OpenAI." + name, true)!.Assembly);
            }
            Assert.DoesNotContain(presentation.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Provider.OpenAI" or "Sunder.Sdk.Worker" or "OpenAI");
            using var package = RegressionTestPackageScope.Create();
            var context = new TestPackageContext("sunder.package.agent.provider.openai", package.RootPath, version: "2.0.0", storage: package.Context.Storage);
            await context.Settings.SetValueAsync("auth.mode", "api-key");
            using var backend = new OpenAiArtifactHttp();
            using var apiHttp = new HttpClient(backend, disposeHandler: false);
            using var transport = new HttpClientPipelineTransport(apiHttp);
            Func<string, OpenAIClient> factory = key => new(new ApiKeyCredential(key), new OpenAIClientOptions
            { Endpoint = new Uri("https://api.artifact.test/v1"), Transport = transport, RetryPolicy = new ClientRetryPolicy(0) });
            var authType = runtime.GetType("Sunder.Package.Agent.Provider.OpenAI.Auth.CodexConnectedAuthStrategy", true)!;
            Action<IServiceCollection> configure = services =>
            {
                services.AddSingleton<HttpClient>(_ => new(backend, disposeHandler: false) { BaseAddress = new Uri("https://codex.artifact.test/backend-api/"), Timeout = Timeout.InfiniteTimeSpan });
                services.AddSingleton(authType, _ => authType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke([
                    context, (Func<HttpClient>)(() => new(backend, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) }),
                     TimeProvider.System, TimeSpan.FromMinutes(5), null]));
                Type Type(string name) => runtime.GetType("Sunder.Package.Agent.Provider.OpenAI." + name, true)!;
                var codex = Type("Transport.CodexConnectedTransport");
                services.AddSingleton(codex, provider => AgentRoleBoundary.Create(codex, provider.GetRequiredService(authType), provider.GetRequiredService<HttpClient>()));
                var chat = Type("OpenAiAgentProvider");
                services.AddSingleton(chat, provider => AgentRoleBoundary.Create(chat,
                    provider.GetRequiredService(Type("Auth.ApiKeyAuthStrategy")), provider.GetRequiredService(authType),
                    provider.GetRequiredService(codex), provider.GetRequiredService(Type("Transport.CodexResponseContinuationStore")), context, factory));
                var embedding = Type("OpenAiEmbeddingProvider");
                services.AddSingleton(embedding, provider => AgentRoleBoundary.Create(embedding,
                    provider.GetRequiredService(Type("Auth.ApiKeyAuthStrategy")), context,
                    (Func<string, string, OpenAI.Embeddings.EmbeddingClient>)((model, key) => factory(key).GetEmbeddingClient(model))));
            };
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var rpc = new RegressionTestRpcClient(state, context.PackageId);
            var generated = CreateControlledGeneratedWorker(probes, context, rpc, configure);
            var options = generated.Options;
            await using var composition = Assert.IsType<SunderWorkerComposition>(options.Composition);
            Assert.NotNull(options.SettingsSchema); Assert.NotNull(options.AuthHandler); Assert.Empty(backend.Requests);
            var registrations = options.Providers.ToDictionary(value => value.ProviderId);
            Assert.Equal(new[] { "openai.chat", "openai.continuation.cleaner", "openai.embedding", "openai.management" }, registrations.Keys.Order(StringComparer.Ordinal));
            var descriptor = RegressionTestRpcDescriptors.Get("sunder.package.agent.provider.openai.management");
            foreach (var provider in options.Providers)
            {
                var expected = provider.ProviderId == "openai.management" ? descriptor : RegressionTestRpcDescriptors.Get(provider.ContractId);
                Assert.Equal(expected.Sha256, provider.DescriptorSha256); Assert.Equal(expected.AbiSha256, provider.AbiSha256);
                state.Add(context.PackageId, expected, provider.Handler, provider.Handler, Noop.Instance, provider.ProviderId, "2.0.0");
            }
            await using var appServices = CreateGeneratedApp(presentation, context, rpc);
            var management = appServices.GetRequiredService(presentation.GetType("Sunder.Package.Agent.Provider.OpenAI.OpenAiOwnerManagementRpcClient", true)!);
            var chatProvider = CreateAgentChatProvider(agentOwner, rpc.ForCaller("sunder.package.agent"), Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.ChatProvider)).Providers));
            await using var chatLifetime = (IAsyncDisposable)chatProvider;
            using var chat = await CreateAgentChatClientAsync(chatProvider, new("openai", "openai/gpt-5.4"));
            Assert.False((await Manage("HasCredentialAsync", [CancellationToken.None])).GetBoolean());
            Assert.False((await Manage("GetAuthStatusAsync", [CancellationToken.None])).GetProperty("isConnected").GetBoolean());
            Assert.Equal(PackageAuthStatusKind.Unavailable, (await options.AuthHandler.GetStatusAsync()).Status);
            foreach (var kind in new[] { "chat", "embedding" })
            {
                Assert.Equal("openai", (await Rpc(kind, "describe", new { })).GetProperty("providerId").GetString());
                Assert.Equal("needs-configuration", (await Rpc(kind, "get-readiness", new { })).GetProperty("status").GetString());
                Assert.NotEmpty((await Rpc(kind, "list-models", new { })).EnumerateArray());
            }
            Assert.Equal("openai-auth-required", (await AssertRoleChatFailure(agentOwner, () => Read())).ErrorCode);
            Assert.Empty(backend.Requests);
            Assert.True((await Manage("SetCredentialAsync", ["  artifact-api-key  ", CancellationToken.None])).GetBoolean());
            Assert.Equal("artifact-api-key", await context.Secrets.GetSecretAsync("auth.apiKey"));
            Assert.Equal("openai.management.failed", (await Assert.ThrowsAsync<SunderRpcException>(() => Manage("SetCredentialAsync", [" ", CancellationToken.None]))).Error.Code);
            foreach (var kind in new[] { "chat", "embedding" }) Assert.Equal("ready", (await Rpc(kind, "get-readiness", new { })).GetProperty("status").GetString());
            Assert.Equal("openai/gpt-5.4-mini", (await Rpc("chat", "resolve-utility-model", new { })).GetString());
            Assert.True((await Rpc("chat", "get-capabilities", new { modelId = "openai/gpt-5.4" })).GetProperty("supportsPdfInput").GetBoolean());
            Assert.Equal("https://api.openai.com/v1", (await Rpc("embedding", "space-identity", new { value = "openai/text-embedding-3-small" })).GetString());
            Assert.Empty(backend.Requests);
            var batch = await Rpc("embedding", "generate-batch", new { modelId = "openai/text-embedding-3-small", texts = new[] { "first", " ", "second" } });
            Assert.Equal(3, batch.GetArrayLength()); Assert.Equal(JsonValueKind.Null, batch[1].ValueKind);
            Assert.Equal(0.25, batch[0].GetProperty("values")[0].GetDouble()); Assert.Equal(1.25, batch[2].GetProperty("values")[0].GetDouble());
            Assert.Equal(3, batch[0].GetProperty("dimensions").GetInt32());
            Assert.Equal("openai/text-embedding-3-small", batch[2].GetProperty("modelId").GetString());
            var count = backend.Requests.Count;
            Assert.Equal(JsonValueKind.Null, (await Rpc("embedding", "generate", new { modelId = "openai/text-embedding-3-small", text = " " })).ValueKind);
            Assert.Empty((await Rpc("embedding", "generate-batch", new { modelId = "openai/text-embedding-3-small", texts = Array.Empty<string>() })).EnumerateArray());
            Assert.Equal(count, backend.Requests.Count);
            backend.BadEmbeddingIndexes = true;
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, (await Assert.ThrowsAsync<SunderRpcException>(() => Rpc("embedding", "generate-batch", new { modelId = "openai/text-embedding-3-small", texts = new[] { "first", "second" } }))).Error.Kind);
            backend.BadEmbeddingIndexes = false;
            var updates = await Read([new(ChatRole.User, [new TextContent("Inspect"), new DataContent(new byte[] { 1, 2, 3 }, "image/png") { Name = "fixture.png" }])],
                new() { Instructions = "Be precise.", MaxOutputTokens = 321, Reasoning = new() { Effort = ReasoningEffort.High },
                    AdditionalProperties = new() { [AgentChatModelOptionKeys.SpeedOptionId] = "fast" } });
            Assert.Contains(updates.SelectMany(value => value.Contents).OfType<TextContent>(), value => value.Text == "API text");
            var request = backend.Requests.Last();
            Assert.Equal("Bearer artifact-api-key", request.Authorization);
            Assert.Equal("priority", request.Body.GetProperty("service_tier").GetString());
            Assert.Equal(321, request.Body.GetProperty("max_output_tokens").GetInt32());
            Assert.Contains("data:image/png;base64,AQID", request.Body.GetRawText(), StringComparison.Ordinal);
            foreach (var mode in new[] { "failed", "context-limit" })
            {
                backend.ApiMode = mode;
                var error = await AssertRoleChatFailure(agentOwner, () => Read());
                if (mode == "context-limit") Assert.Equal(AgentChatProviderFailureKind.ContextWindowExceeded, error.FailureKind);
                else Assert.Equal("openai-response-failed", error.ErrorCode);
                Assert.DoesNotContain("artifact-api-key", error.ToString(), StringComparison.Ordinal);
            }
            backend.ApiMode = "normal";
            var blocked = backend.BlockNext("api");
            using (var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                var pending = Read(token: cancelled.Token);
                await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                await blocked.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Manage("ClearCredentialAsync", [cancelled.Token]));
            }
            var viewModel = appServices.GetRequiredService(presentation.GetType("Sunder.Package.Agent.Provider.OpenAI.OpenAiSettingsViewModel", true)!);
            await ((Task)viewModel.GetType().GetMethod("InitializeAsync")!.Invoke(viewModel, [CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(10));
            await VerifyOpenAiConnected(runtime, agentOwner, options, context, backend, Rpc, Manage, Read);
            var registration = registrations["openai.management"];
            var old = Assert.Single((await rpc.DiscoverAsync(descriptor.ContractId)).Providers).Endpoint;
            await state.RetireAsync(registration.Handler).WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Manage("HasCredentialAsync", [CancellationToken.None]));
            state.Add(context.PackageId, descriptor, registration.Handler, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
            Assert.NotEqual(old, Assert.Single((await rpc.DiscoverAsync(descriptor.ContractId)).Providers).Endpoint);
            Assert.True((await Manage("HasCredentialAsync", [CancellationToken.None])).GetBoolean());
            Assert.False((await Manage("ClearCredentialAsync", [CancellationToken.None])).GetBoolean());
            Assert.Equal("needs-configuration", (await Rpc("embedding", "get-readiness", new { })).GetProperty("status").GetString());
            await VerifyProviderStreamLifecycle(runtime, "OpenAI", agentOwner, probes);
            await VerifyOpenAiAppCallbackLifetime(presentation, context, rpc);

            async Task<JsonElement> Manage(string method, object[] args)
            {
                var pending = management.GetType().GetMethod(method)!.Invoke(management, args)!;
                var task = (Task)pending.GetType().GetMethod("AsTask")!.Invoke(pending, null)!;
                await task.WaitAsync(TimeSpan.FromSeconds(10));
                var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
                if (result is not bool) Assert.Same(presentation, result.GetType().Assembly);
                return JsonSerializer.SerializeToElement(result, result.GetType(), MemoryProviderJson);
            }
            async Task<JsonElement> Rpc(string kind, string method, object request, CancellationToken token = default)
            {
                var id = kind == "session-cleaner" ? "openai.continuation.cleaner" : "openai." + kind;
                var selected = Assert.Single((await rpc.DiscoverAsync(registrations[id].ContractId, token)).Providers);
                return await rpc.InvokeAsync(selected.Endpoint, kind == "session-cleaner" ? kind : kind + "-provider", method,
                    JsonSerializer.SerializeToElement(request, MemoryProviderJson), cancellationToken: token).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }
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

    private static async Task VerifyOpenAiAppCallbackLifetime(Assembly app, TestPackageContext context, ISunderRpcClient rpc)
    {
        var callbacks = new AppCallbackProbe(context.PackageId, PackageCallbackHandlerIds.Authentication);
        var appContext = new TestPackageContext(context.PackageId, version: "2.0.0", storage: context.Storage,
            settings: context.Settings, secrets: context.Secrets, callbacks: callbacks);
        await using var services = CreateGeneratedApp(app, appContext, rpc);
        var type = app.GetType("Sunder.Package.Agent.Provider.OpenAI.OpenAiSettingsViewModel", true)!;
        var model = services.GetRequiredService(type);
        var flow = type.GetField("_callbackFlow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
        Assert.Same(callbacks, flow.GetType().GetField("_callbacks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(flow));
        var command = Assert.IsAssignableFrom<IAsyncRelayCommand>(type.GetProperty("AuthorizeCommand")!.GetValue(model));
        var pending = command.ExecuteAsync(null);
        await callbacks.Polled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("codex-connected", await context.Settings.GetValueAsync("auth.mode"));
        Assert.False(command.CanExecute(null));
        await command.ExecuteAsync(null);
        Assert.Equal(1, callbacks.Starts); Assert.Equal(1, callbacks.Opens);
        ((IDisposable)model).Dispose();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await callbacks.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, callbacks.Cancellations);
    }

}
