using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using Anthropic;
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
    internal static async Task VerifyAnthropicRoles(string worker)
    {
        var load = new AssemblyLoadContext("anthropic-role-local", isCollectible: true);
        try
        {
            using var workerStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(worker, "worker"), "Sunder.Package.Agent.Provider.Anthropic.dll"));
            var runtime = load.LoadFromStream(workerStream);
            var probes = await BuildProviderRoleProbes(worker, runtime, SingleProjectRoleOutput(worker, "worker"));
            await VerifyGeneratedProviderComposition(runtime, "Anthropic");
            var agentOwner = LoadAgentChatOwner(load, worker);
            using var appStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(worker, "app"), "Sunder.Package.Agent.Provider.Anthropic.App.dll"));
            var presentation = load.LoadFromStream(appStream);
            AssertProviderSemanticOwnership(runtime, presentation);
            foreach (var assembly in new[] { runtime, presentation })
                Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name == "Sunder.Package.Agent.Provider.Anthropic.Protocol");
            Assert.DoesNotContain(presentation.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Provider.Anthropic" or "Sunder.Sdk.Worker" or "Anthropic");
            using var package = RegressionTestPackageScope.Create();
            var context = new TestPackageContext("sunder.package.agent.provider.anthropic", package.RootPath, version: "2.0.0", storage: package.Context.Storage);
            using var handler = new AnthropicArtifactHttp();
            using var http = new HttpClient(handler);
            var factoryKeys = new List<string>();
            Func<string, IAnthropicClient> factory = key =>
            {
                factoryKeys.Add(key);
                return new AnthropicClient { ApiKey = key, HttpClient = http, MaxRetries = 0 };
            };
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var rpc = new RegressionTestRpcClient(state, context.PackageId);
            var generated = CreateControlledGeneratedWorker(probes, context, rpc,
                services => ConfigureProviderFactory(runtime, services, context, "Anthropic", factory));
            await using var lifetime = generated.Lifetime;
            var options = generated.Options;
            Assert.NotNull(options.SettingsSchema); Assert.Empty(factoryKeys);
            var registrations = options.Providers.ToDictionary(value => value.ProviderId);
            Assert.Equal(new[] { "anthropic.chat", "anthropic.credentials" }, registrations.Keys.Order(StringComparer.Ordinal));
            var credentialDescriptor = RegressionTestRpcDescriptors.Get("sunder.package.agent.provider.anthropic.credentials");
            foreach (var provider in options.Providers)
            {
                var descriptor = provider.ProviderId == "anthropic.credentials" ? credentialDescriptor : RegressionTestRpcDescriptors.Get(provider.ContractId);
                Assert.Equal(descriptor.Sha256, provider.DescriptorSha256); Assert.Equal(descriptor.AbiSha256, provider.AbiSha256);
                state.Add(context.PackageId, descriptor, provider.Handler, provider.Handler, Noop.Instance, provider.ProviderId, "2.0.0");
            }
            await using var appServices = CreateGeneratedApp(presentation, context, rpc);
            var credentials = appServices.GetRequiredService(presentation.GetType("Sunder.Package.Agent.Provider.Anthropic.ProviderCredentialOwnerRpcClient", true)!);
            var selectedChat = Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.ChatProvider)).Providers);
            var endpoint = selectedChat.Endpoint;
            var chatProvider = CreateAgentChatProvider(agentOwner, rpc.ForCaller("sunder.package.agent"), selectedChat);
            await using var chatLifetime = (IAsyncDisposable)chatProvider;
            using var chat = await CreateAgentChatClientAsync(chatProvider, new("anthropic", "anthropic/claude-opus-4-8"));
            Assert.False(await Credential("GetStatusAsync", [CancellationToken.None]));
            Assert.Equal("needs-configuration", (await Rpc("get-readiness", new { })).GetProperty("status").GetString());
            Assert.Equal("missing-api-key", (await AssertRoleChatFailure(agentOwner, () => Read())).ErrorCode);
            Assert.Empty(factoryKeys); Assert.Empty(handler.Requests);
            Assert.True(await Credential("SetAsync", ["  artifact-anthropic-secret  ", CancellationToken.None]));
            Assert.Equal("artifact-anthropic-secret", await context.Secrets.GetSecretAsync("auth.apiKey"));
            Assert.Equal("ready", (await Rpc("get-readiness", new { })).GetProperty("status").GetString());
            Assert.Equal("anthropic", (await Rpc("describe", new { })).GetProperty("providerId").GetString());
            var models = await Rpc("list-models", new { }); Assert.NotEmpty(models.EnumerateArray());
            Assert.Contains(models.EnumerateArray(), value => value.GetProperty("modelId").GetString() == "anthropic/claude-opus-4-8");
            Assert.Equal("anthropic/claude-haiku-4-5", (await Rpc("resolve-utility-model", new { })).GetString());
            var capabilities = await Rpc("get-capabilities", new { modelId = "anthropic/claude-opus-4-8" });
            Assert.True(capabilities.GetProperty("supportsImageInput").GetBoolean()); Assert.True(capabilities.GetProperty("supportsPdfInput").GetBoolean());
            Assert.False(capabilities.GetProperty("supportsStreamingToolCalls").GetBoolean()); Assert.Empty(factoryKeys);

            ChatMessage[] messages = [new(ChatRole.User, [new TextContent("before"), new DataContent(new byte[] { 1, 2, 3 }, "image/png") { Name = "image.png" },
                new TextContent("between"), new DataContent(new byte[] { 4, 5, 6 }, "application/pdf") { Name = "doc.pdf" }])];
            var chatOptions = new ChatOptions { Instructions = "Be precise.", MaxOutputTokens = 1234, Reasoning = new() { Effort = ReasoningEffort.High, Output = ReasoningOutput.Summary } };
            var updates = await Read(messages, chatOptions);
            Assert.Contains(updates.SelectMany(value => value.Contents).OfType<TextReasoningContent>(), value => value.Text == "Checking constraints.");
            Assert.Equal("## Heading\n\n- item", string.Concat(updates.SelectMany(value => value.Contents).OfType<TextContent>().Select(value => value.Text)));
            var usage = Assert.Single(updates.SelectMany(value => value.Contents).OfType<UsageContent>()).Details;
            Assert.Equal(6, usage.InputTokenCount); Assert.Equal(2, usage.OutputTokenCount); Assert.Equal(3, usage.CachedInputTokenCount);
            var captured = handler.Requests.Last();
            Assert.Equal("artifact-anthropic-secret", captured.Headers["x-api-key"]);
            Assert.Equal("claude-opus-4-8", captured.Body.GetProperty("model").GetString());
            Assert.Equal(1234, captured.Body.GetProperty("max_tokens").GetInt32());
            Assert.Equal("Be precise.", captured.Body.GetProperty("system").GetString());
            Assert.Equal("adaptive", captured.Body.GetProperty("thinking").GetProperty("type").GetString());
            Assert.Equal("high", captured.Body.GetProperty("output_config").GetProperty("effort").GetString());
            var parts = captured.Body.GetProperty("messages")[0].GetProperty("content");
            Assert.Equal(new[] { "text", "image", "text", "document" }, parts.EnumerateArray().Select(value => value.GetProperty("type").GetString()));
            Assert.Equal("AQID", parts[1].GetProperty("source").GetProperty("data").GetString());
            Assert.Equal("BAUG", parts[3].GetProperty("source").GetProperty("data").GetString());
            Assert.Equal("doc.pdf", parts[3].GetProperty("title").GetString());

            var tool = AIFunctionFactory.CreateDeclaration("read_file", "Read", JsonSerializer.SerializeToElement(new
            { type = "object", properties = new { path = new { type = "string" } }, required = new[] { "path" } }));
            var toolOptions = new ChatOptions { Tools = [tool], AllowMultipleToolCalls = true };
            foreach (var fast in new[] { false, true })
            {
                toolOptions.AdditionalProperties = fast ? new() { [AgentChatModelOptionKeys.SpeedOptionId] = "fast" } : null;
                updates = await Read([new(ChatRole.User, "Read the file.")], toolOptions);
                var content = updates.SelectMany(value => value.Contents).Where(value => value is not UsageContent).ToArray();
                Assert.Equal("Before", Assert.IsType<TextContent>(content[0]).Text);
                Assert.Equal("artifact-signature", Assert.IsType<TextReasoningContent>(content[1]).ProtectedData);
                var call = Assert.IsType<FunctionCallContent>(content[2]);
                Assert.Equal("read_file", call.Name); Assert.Equal("call.artifact", call.CallId);
                Assert.Equal("README.md", Assert.IsType<JsonElement>(call.Arguments!["path"]).GetString());
                Assert.Equal("After", Assert.IsType<TextContent>(content[3]).Text);
                captured = handler.Requests.Last();
                Assert.False(captured.Body.GetProperty("tool_choice").GetProperty("disable_parallel_tool_use").GetBoolean());
                Assert.Equal("path", captured.Body.GetProperty("tools")[0].GetProperty("input_schema").GetProperty("required")[0].GetString());
                if (fast)
                {
                    Assert.Contains("fast-mode-2026-02-01", captured.Headers["anthropic-beta"], StringComparison.Ordinal);
                    Assert.Equal("fast", captured.Body.GetProperty("speed").GetString());
                }
                else Assert.False(captured.Body.TryGetProperty("speed", out _));
            }
            await Read([new(ChatRole.Assistant, [new TextReasoningContent("Saved reasoning") { ProtectedData = "saved-signature" },
                new FunctionCallContent("saved-call", "read_file", new Dictionary<string, object?> { ["path"] = "saved.txt" })]),
                new(ChatRole.Tool, [new FunctionResultContent("saved-call", "saved result")])], toolOptions);
            captured = handler.Requests.Last();
            Assert.Equal("saved-signature", captured.Body.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("signature").GetString());
            Assert.Equal("saved-call", captured.Body.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("tool_use_id").GetString());
            updates = await Read(options: new() { AdditionalProperties = new() { [AgentChatModelOptionKeys.SpeedOptionId] = "fast" },
                Reasoning = new() { Effort = ReasoningEffort.ExtraHigh, Output = ReasoningOutput.Summary } });
            Assert.NotEmpty(updates);
            captured = handler.Requests.Last();
            Assert.Equal("fast", captured.Body.GetProperty("speed").GetString());
            Assert.Equal("xhigh", captured.Body.GetProperty("output_config").GetProperty("effort").GetString());
            Assert.All(factoryKeys, key => Assert.Equal("artifact-anthropic-secret", key));

            foreach (var (mode, code) in new[] { ("eof", "anthropic-incomplete-response"), ("malformed-tool", "anthropic-malformed-tool-call"), ("context-limit", "anthropic-http-error") })
            {
                handler.Mode = mode;
                var error = await AssertRoleChatFailure(agentOwner, () => Read(options: mode == "malformed-tool" ? toolOptions : null));
                Assert.Equal(code, error.ErrorCode);
                if (mode == "context-limit") Assert.Equal(AgentChatProviderFailureKind.ContextWindowExceeded, error.FailureKind);
                Assert.DoesNotContain("artifact-anthropic-secret", error.ToString(), StringComparison.Ordinal);
            }
            handler.Mode = "hang";
            using (var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                var pending = Read(token: cancelled.Token);
                await handler.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                Assert.True(handler.CancellationObserved);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Credential("SetAsync", ["not-stored", cancelled.Token]));
            }
            handler.Mode = "normal";
            Assert.Equal("artifact-anthropic-secret", await context.Secrets.GetSecretAsync("auth.apiKey"));
            Assert.True(await Credential("SetAsync", ["rotated-secret", CancellationToken.None])); await Read();
            Assert.Equal("rotated-secret", factoryKeys.Last());
            var viewModel = appServices.GetRequiredService(presentation.GetType("Sunder.Package.Agent.Provider.Anthropic.AnthropicSettingsViewModel", true)!);
            await ((Task)viewModel.GetType().GetMethod("InitializeAsync")!.Invoke(viewModel, [CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(await Credential("ClearAsync", [CancellationToken.None]));
            var count = factoryKeys.Count;
            Assert.Equal("missing-api-key", (await AssertRoleChatFailure(agentOwner, () => Read())).ErrorCode);
            Assert.Equal(count, factoryKeys.Count);
            var oldCredentials = Assert.Single((await rpc.DiscoverAsync(credentialDescriptor.ContractId)).Providers).Endpoint;
            var credentialRegistration = registrations["anthropic.credentials"];
            await state.RetireAsync(credentialRegistration.Handler).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("provider.credential.unavailable", (await Assert.ThrowsAsync<SunderRpcException>(() => Credential("GetStatusAsync", [CancellationToken.None]))).Error.Code);
            state.Add(context.PackageId, credentialDescriptor, credentialRegistration.Handler, credentialRegistration.Handler, Noop.Instance, credentialRegistration.ProviderId, "2.0.0");
            Assert.NotEqual(oldCredentials, Assert.Single((await rpc.DiscoverAsync(credentialDescriptor.ContractId)).Providers).Endpoint);
            Assert.False(await Credential("GetStatusAsync", [CancellationToken.None]));
            Assert.Equal(handler.Responses, handler.DisposedResponses);
            await VerifyProviderStreamLifecycle(runtime, "Anthropic", agentOwner, probes);
            await lifetime.DisposeAsync(); await lifetime.DisposeAsync();

            async Task<bool> Credential(string name, object[] args)
            {
                var value = credentials.GetType().GetMethod(name)!.Invoke(credentials, args)!;
                var task = (Task)value.GetType().GetMethod("AsTask")!.Invoke(value, null)!;
                await task.WaitAsync(TimeSpan.FromSeconds(10));
                var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
                Assert.Same(presentation, result.GetType().Assembly);
                return (bool)result.GetType().GetProperty("HasStoredCredential")!.GetValue(result)!;
            }
            Task<JsonElement> Rpc(string method, object request)
                => rpc.InvokeAsync(endpoint, "chat-provider", method, JsonSerializer.SerializeToElement(request, MemoryProviderJson)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
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
