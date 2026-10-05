extern alias WebWorker;

using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
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
    private static async Task VerifyWebBehavior(string root, string worker)
    {
        var load = new AssemblyLoadContext("web-generated-artifact", isCollectible: true);
        try
        {
            var path = Path.Combine(SingleProjectRoleOutput(worker, "worker"), "Sunder.Package.Agent.Tools.Web.dll");
            using var bytes = File.OpenRead(path);
            var runtime = load.LoadFromStream(bytes);
            Assert.NotNull(runtime.GetType("Sunder.Package.Agent.Tools.Web.Services.WebToolSource"));
            Assert.Equal("Sunder.Generated.Package.WorkerEntryPoint", runtime.EntryPoint!.DeclaringType!.FullName);
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Tools.Web.WebWorkerComposition"));
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Tools.Web.WebWorkerBindings"));
            Assert.DoesNotContain(runtime.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Protocol" or "Sunder.Agent.Execution.Common");
            foreach (var type in new[] { "Sunder.Package.Agent.Contracts.Models.AgentToolExecutionContext", "Sunder.Package.Agent.Contracts.Models.AgentToolDescriptor",
                "Sunder.Package.Agent.Contracts.Models.AgentToolResult", "Sunder.Package.Agent.Contracts.Contracts.IAgentToolSource", "Sunder.Agent.Execution.Common.BoundedValue" })
                Assert.Same(runtime, runtime.GetType(type, true)!.Assembly);
            Assert.NotSame(typeof(AgentToolResult), runtime.GetType(typeof(AgentToolResult).FullName!));
            using var package = RegressionTestPackageScope.Create();
            var context = new TestPackageContext("sunder.package.agent.tools.web", package.RootPath, version: "2.0.0", storage: package.Context.Storage);
            var peer = new WebHttpPeer();
            await using var backend = await BuildWebBackend(root, worker, load, peer);
            await VerifyWebGeneratedComposition(backend, package);
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var options = backend.Create(context, new RegressionTestRpcClient(state, context.PackageId));
            Assert.Null(options.Composition); Assert.NotNull(options.SettingsSchema);
            Assert.Empty(peer.Hosts); Assert.Empty(peer.Fetches); Assert.Empty(peer.Searches);
            var registration = Assert.Single(options.Providers);
            Assert.Equal("web.tools", registration.ProviderId);
            var descriptor = RegressionTestRpcDescriptors.Get(AgentRpcContractIds.ToolSource);
            Assert.Equal(descriptor.Sha256, registration.DescriptorSha256); Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
            state.Add(context.PackageId, descriptor, registration.Handler, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
            var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
            var endpoint = Assert.Single((await rpc.DiscoverAsync(descriptor.ContractId)).Providers).Endpoint;
            Assert.Equal("web", (await Call("describe", new { })).GetProperty("sourceId").GetString());
            var tools = await Call("list-tools", ToolSourceContext(new(null, null)));
            Assert.Equal(new[] { "web_fetch", "web_search" }, tools.EnumerateArray().Select(tool => tool.GetProperty("toolId").GetString()));
            Assert.All(tools.EnumerateArray(), tool => { Assert.True(tool.GetProperty("isReadOnly").GetBoolean()); Assert.True(tool.GetProperty("requiresNetwork").GetBoolean()); });
            foreach (var tool in new[] { "web_fetch", "web_search" })
            {
                Assert.Equal("ready", (await Call("get-readiness", ToolReadiness(tool, new(null, null)))).GetProperty("status").GetString());
                Assert.Equal(JsonValueKind.Null, (await Call("permission", Request(tool, new { }))).ValueKind);
                Assert.Equal(JsonValueKind.Null, (await Call("preflight", Request(tool, new { }))).ValueKind);
            }
            Assert.Equal(JsonValueKind.Null, (await Call("get-readiness", ToolReadiness("unknown", new(null, null)))).ValueKind);
            Assert.Equal("tool-not-found", (await Execute("unknown", new { })).ErrorCode);
            Assert.Empty(peer.Fetches); Assert.Empty(peer.Searches);
            foreach (var (args, code) in new (object, string)[]
            {
                (new { url = "https://public.example/", timeoutSeconds = 0 }, "web-fetch-args"),
                (new { url = "https://public.example/", format = "pdf" }, "web-fetch-format-invalid"),
                (new { url = "file:///tmp/fixture" }, "web-fetch-url-invalid"),
            }) Assert.Equal(code, (await Execute("web_fetch", args)).ErrorCode);
            Assert.Empty(peer.Fetches);
            foreach (var url in new[] { "http://127.0.0.1/", "http://169.254.169.254/", "http://[::1]/", "http://metadata.google.internal/", "https://mixed.example/", "https://empty.example/" })
                Assert.Equal("web-fetch-http", (await Execute("web_fetch", new { url })).ErrorCode);
            Assert.Empty(peer.Fetches);
            Assert.DoesNotContain("metadata.google.internal", peer.Hosts);
            var fetched = await Execute("web_fetch", new { url = "https://public.example/start", timeoutSeconds = "5" });
            Assert.False(fetched.IsError, fetched.Content); Assert.StartsWith("# Artifact Page", fetched.Content!, StringComparison.Ordinal);
            Assert.DoesNotContain("do-not-render", fetched.Content!, StringComparison.Ordinal);
            Assert.Equal("https://public.example/final", Assert.Single(fetched.Sources!).Url);
            Assert.Equal("http", fetched.BackendId);
            Assert.Equal("https://public.example/final", JsonSerializer.Deserialize<JsonElement>(fetched.StructuredPayloadJson!).GetProperty("finalUrl").GetString());
            var plain = await Execute("web_fetch", new { url = "https://public.example/final", format = "text" });
            Assert.DoesNotContain("<p>", plain.Content!, StringComparison.Ordinal); Assert.Contains("Hello & world", plain.Content!, StringComparison.Ordinal);
            var html = await Execute("web_fetch", new { url = "https://public.example/final", format = "html" });
            Assert.Contains("<script>", html.Content!, StringComparison.Ordinal);
            var fetchPresentation = await Call("presentation", new AgentToolPresentationRequest("web_fetch", "{\"url\":\"https://public.example/final\"}", fetched.Summary, fetched.Content, fetched.StructuredPayloadJson, null, false, null, fetched.BackendId));
            Assert.Contains("public.example", fetchPresentation.GetProperty("detailMarkdown").GetString()!, StringComparison.Ordinal);
            foreach (var url in new[] { "https://public.example/private", "https://public.example/scheme", "https://rebind.example/start" })
            {
                var before = peer.Fetches.Count;
                Assert.Equal("web-fetch-http", (await Execute("web_fetch", new { url })).ErrorCode);
                Assert.Equal(before + 1, peer.Fetches.Count);
            }
            var redirects = peer.Fetches.Count;
            Assert.Equal("web-fetch-http", (await Execute("web_fetch", new { url = "https://public.example/loop" })).ErrorCode);
            Assert.Equal(redirects + 6, peer.Fetches.Count);
            var large = await Execute("web_fetch", new { url = "https://public.example/large", format = "text" });
            Assert.False(large.IsError, large.Content); Assert.True(large.WasTruncated); Assert.Equal(20000, large.Content!.Length);
            Assert.True(peer.LastLargeStream!.WasDisposed); Assert.InRange(peer.LastLargeStream.FinalPosition, 5 * 1024 * 1024, 5 * 1024 * 1024 + 8192);
            Assert.Equal(backend.FetchClients, backend.DisposedFetchClients);

            await context.Settings.SetValueAsync("search.maxResults.default", "7");
            var search = await Execute("web_search", new { query = "artifact query" });
            Assert.False(search.IsError, search.Content); Assert.Equal("exa", search.BackendId); Assert.Equal(2, search.Sources!.Count);
            Assert.Equal(new[] { "initialize", "notifications/initialized", "tools/list", "tools/list", "tools/call" }, peer.Searches.Select(item => item.Method));
            Assert.Equal(7, peer.Searches.Last().Body.GetProperty("params").GetProperty("arguments").GetProperty("numResults").GetInt32());
            Assert.Empty(peer.Searches.Last().Uri.Query);
            await context.Secrets.SetSecretAsync("search.exa.apiKey", "exa-secret-canary");
            var limited = await Execute("web_search", new { query = "bounded", maxResults = 100 }); Assert.False(limited.IsError);
            Assert.Equal(10, peer.Searches.Last().Body.GetProperty("params").GetProperty("arguments").GetProperty("numResults").GetInt32());
            Assert.Contains("exa-secret-canary", peer.Searches.Last().Uri.Query, StringComparison.Ordinal);
            Assert.DoesNotContain("exa-secret-canary", JsonSerializer.Serialize(limited, MemoryProviderJson), StringComparison.Ordinal);
            peer.SearchMode = "large-display";
            var bounded = await Execute("web_search", new { query = "large" });
            Assert.True(bounded.WasTruncated); Assert.Equal(32 * 1024, bounded.Content!.Length);
            Assert.InRange(JsonSerializer.Deserialize<JsonElement>(bounded.StructuredPayloadJson!).GetProperty("content").GetString()!.Length, 1, 64 * 1024);
            foreach (var mode in new[] { "redirect", "malformed", "wrong-id", "tool-error", "large-raw" })
            {
                peer.SearchMode = mode;
                var failure = await Execute("web_search", new { query = "controlled failure" });
                Assert.True(failure.IsError); Assert.Equal("web-search-http", failure.ErrorCode);
                Assert.DoesNotContain("exa-secret-canary", JsonSerializer.Serialize(failure, MemoryProviderJson), StringComparison.Ordinal);
                Assert.DoesNotContain("do-not-echo", failure.Content ?? "", StringComparison.Ordinal);
            }
            Assert.True(peer.LastLargeStream!.WasDisposed); Assert.InRange(peer.LastLargeStream.FinalPosition, 1, 2 * 1024 * 1024 + 1);
            peer.SearchMode = "normal";
            foreach (var stage in new[] { "dns", "fetch", "search" })
            {
                var gate = peer.BlockNext(stage);
                using var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var pending = stage == "search" ? Execute("web_search", new { query = "cancel" }, cancelled.Token)
                    : Execute("web_fetch", new { url = "https://public.example/cancel" }, cancelled.Token);
                await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            var timeoutGate = peer.BlockNext("fetch");
            var timedOut = await Execute("web_fetch", new { url = "https://public.example/timeout", timeoutSeconds = 1 });
            Assert.Equal("web-fetch-http", timedOut.ErrorCode); Assert.True(timeoutGate.Cancelled.Task.IsCompleted);
            var retirementGate = peer.BlockNext("search");
            var retiring = Execute("web_search", new { query = "retire" });
            await retirementGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var retirement = state.RetireAsync(registration.Handler);
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => retiring)).Error.Kind);
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
            state.Add(context.PackageId, descriptor, registration.Handler, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => Call("describe", new { }))).Error.Kind);
            endpoint = Assert.Single((await rpc.DiscoverAsync(descriptor.ContractId)).Providers).Endpoint;
            Assert.False((await Execute("web_fetch", new { url = "https://public.example/final" })).IsError);
            var stopFetch = peer.BlockNext("fetch"); var stopSearch = peer.BlockNext("search");
            var fetching = Execute("web_fetch", new { url = "https://public.example/stop" });
            var searching = Execute("web_search", new { query = "stop" });
            await Task.WhenAll(stopFetch.Started.Task, stopSearch.Started.Task).WaitAsync(TimeSpan.FromSeconds(5));
            var shutdown = backend.DisposeAsync().AsTask();
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, (await Assert.ThrowsAsync<SunderRpcException>(() => fetching)).Error.Kind);
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, (await Assert.ThrowsAsync<SunderRpcException>(() => searching)).Error.Kind);
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5)); await backend.DisposeAsync();
            Assert.Equal(backend.FetchClients, backend.DisposedFetchClients); Assert.Equal(1, backend.DisposedSearchClients);

            async Task<JsonElement> Call(string method, object request, CancellationToken token = default)
            {
                await using var scope = await rpc.CreateCallScopeAsync(new(DeadlineUtc: DateTimeOffset.UtcNow.AddSeconds(15)), token);
                return await scope.InvokeAsync(endpoint, "tool-source", method, JsonSerializer.SerializeToElement(request, MemoryProviderJson), cancellationToken: token).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }
            JsonElement Request(string tool, object args) => RegressionTestRpcValues.ToolInvocation(new(null), new(tool, JsonSerializer.Serialize(args)));
            async Task<AgentToolResult> Execute(string tool, object args, CancellationToken token = default)
                => (await Call("execute", Request(tool, args), token)).Deserialize<AgentToolResult>(MemoryProviderJson)!;
        }
        finally { load.Unload(); }
    }

    private static async Task VerifyWebGeneratedComposition(WebBackend backend, RegressionTestPackageScope package)
    {
        var secrets = new WebShutdownSecrets();
        var context = new TestPackageContext("sunder.package.agent.tools.web", package.RootPath, version: "2.0.0",
            storage: package.Context.Storage, secrets: secrets);
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var options = backend.CreateGenerated(context, new RegressionTestRpcClient(state, context.PackageId));
        Assert.Null(options.Composition);
        var stop = Assert.IsType<Func<SunderWorkerShutdownContext, CancellationToken, ValueTask>>(options.OnShutdown);
        var reason = (SunderWorkerShutdownContext)Activator.CreateInstance(typeof(SunderWorkerShutdownContext), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: ["generated-web-shutdown"], culture: null)!;
        Assert.Equal(JsonSerializer.Serialize(WebWorker::Sunder.Package.Agent.Tools.Web.WebToolsConfiguration.Schema), JsonSerializer.Serialize(options.SettingsSchema));
        Assert.False(secrets.Started.Task.IsCompleted);
        var registration = Assert.Single(options.Providers);
        Assert.Equal("web.tools", registration.ProviderId);
        var descriptor = RegressionTestRpcDescriptors.Get(AgentRpcContractIds.ToolSource);
        Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
        Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
        state.Add(context.PackageId, descriptor, registration.Handler, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        var endpoint = Assert.Single((await rpc.DiscoverAsync(descriptor.ContractId)).Providers).Endpoint;
        await using var scope = await rpc.CreateCallScopeAsync(new(DeadlineUtc: DateTimeOffset.UtcNow.AddSeconds(15)));
        var listed = await scope.InvokeAsync(endpoint, "tool-source", "list-tools",
            JsonSerializer.SerializeToElement(ToolSourceContext(new(null, null)), MemoryProviderJson));
        Assert.Equal(new[] { "web_fetch", "web_search" }, listed.EnumerateArray().Select(tool => tool.GetProperty("toolId").GetString()));
        var pending = scope.InvokeAsync(endpoint, "tool-source", "get-readiness",
            JsonSerializer.SerializeToElement(ToolReadiness("web_search", new(null, null)), MemoryProviderJson)).AsTask();
        Task shutdown;
        try
        {
            await secrets.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            shutdown = stop(reason, CancellationToken.None).AsTask();
            await secrets.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(shutdown, stop(reason, CancellationToken.None).AsTask());
            Assert.False(shutdown.IsCompleted, "Generated composition must drain the in-flight source call before disposing its dependencies.");
        }
        finally { secrets.Release.TrySetResult(); }
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, (await Assert.ThrowsAsync<SunderRpcException>(() => pending)).Error.Kind);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        await stop(reason, CancellationToken.None);
    }

    private sealed class WebShutdownSecrets : IPackageSecrets
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
        {
            Assert.Equal("search.exa.apiKey", key);
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return null; }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                await Release.Task;
                throw;
            }
        }
        public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
