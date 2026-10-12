using System.Reflection;
using System.Text.Json;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Authentication;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static (SunderWorkerOptions Options, IAsyncDisposable Lifetime, IServiceProvider Services) CreateGeneratedWorker(
        Assembly worker, IPackageContext context, ISunderRpcClient rpc)
    {
        Assert.Equal("Sunder.Generated.Package.WorkerEntryPoint", worker.EntryPoint!.DeclaringType!.FullName);
        var entry = worker.GetType("Sunder.Generated.Package.WorkerEntryPoint", true)!;
        var options = (SunderWorkerOptions)entry.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(IPackageContext), typeof(ISunderRpcClient)])!.Invoke(null, [context, rpc])!;
        return GetGeneratedComposition(options);
    }

    private static (SunderWorkerOptions Options, IAsyncDisposable Lifetime, IServiceProvider Services) GetGeneratedComposition(SunderWorkerOptions options)
    {
        if (options.Composition is { } staged) return (options, staged, staged.Services);
        var shutdown = options.OnShutdown!;
        var services = (IServiceProvider)shutdown.Target!.GetType().GetProperty("Services", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shutdown.Target)!;
        return (options, new GeneratedShutdownLifetime(shutdown), services);
    }

    private sealed class GeneratedShutdownLifetime(Func<SunderWorkerShutdownContext, CancellationToken, ValueTask> shutdown) : IAsyncDisposable
    {
        private readonly SunderWorkerShutdownContext _reason = (SunderWorkerShutdownContext)Activator.CreateInstance(typeof(SunderWorkerShutdownContext),
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null, args: ["artifact-shutdown"], culture: null)!;
        public ValueTask DisposeAsync() => shutdown(_reason, CancellationToken.None);
    }

    private static async Task VerifyGeneratedMcpComposition(Assembly worker)
    {
        const string prefix = "Sunder.Package.Agent.Mcp.";
        Assert.Null(worker.GetType(prefix + "Worker.McpWorkerConfiguration"));
        Assert.Null(worker.GetType(prefix + "Worker.McpWorkerBindings"));
        using var package = RegressionTestPackageScope.Create();
        var context = new TestPackageContext("sunder.package.agent.mcp", package.RootPath, version: "2.0.0", storage: package.Context.Storage);
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var (options, lifetime, services) = CreateGeneratedWorker(worker, context, new RegressionTestRpcClient(state, context.PackageId));
        await using var owned = lifetime;
        Assert.IsType<SunderWorkerComposition>(options.Composition);
        Assert.Null(options.AuthHandler); Assert.Null(options.SettingsSchema);
        Assert.Null(options.OnActivated); Assert.Null(options.OnShutdown);
        var callback = Assert.Single(options.CallbackHandlers);
        Assert.Equal("mcp.oauth.v1", callback.CallbackHandlerId);
        Assert.Same(Service("Services.McpOAuthCallbackHandler"), callback);
        Assert.NotNull(Service("Worker.McpActivation"));
        var manager = Service("Services.McpClientConnectionManager");
        var coordinator = Service("Services.McpConfigurationCoordinator");
        Assert.Equal(0, state.DiscoveryCount);
        Assert.Empty(Directory.GetFiles(package.RootPath, "*", SearchOption.AllDirectories));
        await Assert.ThrowsAsync<InvalidOperationException>(() => callback.StartCallbackAsync(new("invalid", new Uri("https://callback.example/complete"))));
        await callback.CancelCallbackAsync(new("absent", PackageCallbackCancellationReason.CallerRequested));
        await lifetime.DisposeAsync();
        Assert.True((bool)manager.GetType().GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!);
        var disposed = Assert.Throws<TargetInvocationException>(() => coordinator.GetType().GetMethod("Start")!.Invoke(coordinator, null));
        Assert.IsType<ObjectDisposedException>(disposed.InnerException);

        object Service(string name) => services.GetService(worker.GetType(prefix + name, true)!)!;
    }

    private static async Task VerifyGeneratedOpenAiComposition(Assembly worker)
    {
        const string prefix = "Sunder.Package.Agent.Provider.OpenAI.";
        Assert.Null(worker.GetType(prefix + "Worker.OpenAiWorkerConfiguration"));
        Assert.Null(worker.GetType(prefix + "Worker.OpenAiWorkerBindings"));
        using var package = RegressionTestPackageScope.Create();
        var context = new TestPackageContext("sunder.package.agent.provider.openai", package.RootPath, version: "2.0.0", storage: package.Context.Storage);
        await context.Settings.SetValueAsync("auth.mode", "api-key");
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var (options, lifetime, services) = CreateGeneratedWorker(worker, context, new RegressionTestRpcClient(state, context.PackageId));
        await using var owned = lifetime;
        Assert.IsType<SunderWorkerComposition>(options.Composition);
        Assert.Null(options.OnActivated); Assert.Null(options.OnShutdown);
        Assert.Empty(options.CallbackHandlers);
        Assert.Equal(new[] { "openai.chat", "openai.continuation.cleaner", "openai.embedding", "openai.management" }, options.Providers.Select(value => value.ProviderId).Order(StringComparer.Ordinal));
        Assert.Same(worker.GetType(prefix + "OpenAiProviderConfiguration", true)!.GetProperty("Schema")!.GetValue(null), options.SettingsSchema);
        var auth = Assert.IsAssignableFrom<IPackageAuthHandler>(options.AuthHandler);
        Assert.Same(Service("OpenAiPackageAuthHandler"), auth);
        var strategy = Service("Auth.CodexConnectedAuthStrategy");
        Assert.Same(strategy, Field(auth, "_codexConnectedAuthStrategy"));
        var transport = Service("Transport.CodexConnectedTransport");
        Assert.Same(strategy, Field(transport, "_codexConnectedAuthStrategy"));
        Assert.Equal(true, Field(transport, "_ownsHttpClient"));
        var http = (HttpClient)Field(transport, "_httpClient");
        var apiKey = Service("Auth.ApiKeyAuthStrategy");
        var management = Service("OpenAiManagementService");
        var set = management.GetType().GetMethod("SetCredentialAsync")!.CreateDelegate<Func<string, CancellationToken, ValueTask<bool>>>(management);
        var clear = management.GetType().GetMethod("ClearCredentialAsync")!.CreateDelegate<Func<CancellationToken, ValueTask<bool>>>(management);
        var read = apiKey.GetType().GetMethod("GetApiKeyAsync")!.CreateDelegate<Func<CancellationToken, Task<string?>>>(apiKey);
        Assert.True(await set("  generated-openai-credential  ", default));
        Assert.Equal("generated-openai-credential", await read(default));
        Assert.False(await clear(default)); Assert.Null(await read(default));
        Assert.Equal(PackageAuthStatusKind.Unavailable, (await auth.GetStatusAsync()).Status);
        await context.Settings.SetValueAsync("auth.mode", "codex-connected");
        Assert.Equal(PackageAuthStatusKind.NotConnected, (await auth.GetStatusAsync()).Status);
        var started = await auth.StartAuthorizationAsync(new("generated-session", new Uri("http://localhost:1455/auth/callback")));
        Assert.NotNull(started); Assert.Equal("generated-session", started.AuthSessionId);
        var callback = (IPackageCallbackHandler)auth;
        Assert.Equal(PackageCallbackHandlerIds.Authentication, callback.CallbackHandlerId);
        await callback.CancelCallbackAsync(new("generated-session", PackageCallbackCancellationReason.CallerRequested));
        Assert.Equal(PackageAuthStatusKind.Failed, (await auth.CompleteAuthorizationAsync(new("generated-session", new Dictionary<string, string?>()))).Status);
        Assert.Equal(0, state.DiscoveryCount);
        await lifetime.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => http.Timeout = TimeSpan.FromSeconds(1));

        object Service(string name) => services.GetService(worker.GetType(prefix + name, true)!)!;
        static object Field(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    }

    private static async Task VerifyGeneratedDockerComposition(Assembly worker)
    {
        const string prefix = "Sunder.Package.Agent.Execution.Docker.";
        Assert.Null(worker.GetType(prefix + "DockerWorkerConfiguration"));
        Assert.Null(worker.GetType(prefix + "DockerWorkerBindings"));
        using var package = RegressionTestPackageScope.Create();
        var context = new TestPackageContext("sunder.package.agent.execution.docker", package.RootPath, version: "2.0.0", storage: package.Context.Storage);
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var (options, lifetime, services) = CreateGeneratedWorker(worker, context, new RegressionTestRpcClient(state, context.PackageId));
        await using var owned = lifetime;
        Assert.IsType<SunderWorkerComposition>(options.Composition);
        Assert.Null(options.OnActivated); Assert.Null(options.OnShutdown);
        Assert.Same(worker.GetType(prefix + "DockerExecutionConfiguration", true)!.GetProperty("Schema")!.GetValue(null), options.SettingsSchema);
        var migration = Service("DockerPackageStorageMigration");
        var runner = Service("DockerCliRunner");
        var catalog = Service("DockerImageCatalogService");
        var configuration = Service("DockerExecutionWorkspaceConfigService");
        var lifecycle = Service("DockerContainerLifecycleService");
        var target = Service("DockerExecutionTarget");
        Assert.Same(migration, Field(catalog, "_storageMigration"));
        Assert.Same(migration, Field(configuration, "_storageMigration"));
        Assert.Same(runner, Field(catalog, "_dockerCliRunner"));
        Assert.Same(runner, Field(target, "_dockerCliRunner"));
        Assert.Same(catalog, Field(configuration, "_imageCatalogService"));
        Assert.Same(catalog, Field(target, "_imageCatalogService"));
        Assert.Same(configuration, Field(target, "_configService"));
        Assert.Same(lifecycle, Field(target, "_lifecycleService"));
        Assert.Equal(prefix + "DockerMountIdentityVerifier", Field(target, "_mountIdentityVerifier").GetType().FullName);
        Assert.Equal(0, state.DiscoveryCount);
        await ((ISunderWorkerLifecycleService)migration).PrepareCandidateAsync();
        await ((ISunderWorkerLifecycleService)migration).ActivateAsync(null!);
        var registration = Assert.Single(options.Providers, value => value.ProviderId == "docker.execution.target");
        var description = await registration.Handler.InvokeUnaryAsync(null!, "execution-target", "describe", JsonSerializer.SerializeToElement(new { }));
        Assert.Equal("docker", description.GetProperty("targetId").GetString());
        Assert.Equal(8, description.GetProperty("facets").GetArrayLength());
        await lifetime.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => (Task)lifecycle.GetType().GetMethod("AcquireOperationAsync")!
            .Invoke(lifecycle, ["disposed", CancellationToken.None])!);

        object Service(string name) => services.GetService(worker.GetType(prefix + name, true)!)!;
        static object Field(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    }

    private static async Task VerifyGeneratedProviderComposition(Assembly worker, string family)
    {
        var prefix = "Sunder.Package.Agent.Provider." + family;
        var id = family.ToLowerInvariant();
        Assert.Null(worker.GetType(prefix + "." + family + "WorkerComposition"));
        Assert.Null(worker.GetType(prefix + "." + family + "WorkerBindings"));
        using var package = RegressionTestPackageScope.Create();
        var context = new TestPackageContext(prefix.ToLowerInvariant(), package.RootPath, version: "2.0.0", storage: package.Context.Storage);
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, context.PackageId);
        var (options, lifetime, services) = CreateGeneratedWorker(worker, context, rpc);
        await using var owned = lifetime;
        Assert.Null(options.Composition); Assert.Null(options.OnActivated);
        Assert.Same(worker.GetType(prefix + "." + family + "ProviderConfiguration", true)!.GetProperty("Schema")!.GetValue(null), options.SettingsSchema);
        var settings = services.GetService(worker.GetType(prefix + ".ProviderSettings", true)!)!;
        var credentials = settings.GetType().GetProperty("Credentials", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(settings)!;
        var chat = services.GetService(worker.GetType(prefix + "." + family + "AgentProvider", true)!)!;
        var embedding = family == "Anthropic" ? null : services.GetService(worker.GetType(prefix + "." + family + "EmbeddingProvider", true)!)!;
        HttpClient? connectionHttp = null;
        if (family == "LMStudio")
        {
            var connection = services.GetService(worker.GetType(prefix + ".LMStudioConnection", true)!)!;
            var catalog = services.GetService(worker.GetType(prefix + ".LMStudioModelCatalogService", true)!)!;
            Assert.Same(credentials, Field(connection, "_credentials"));
            Assert.Same(connection, Field(catalog, "_connection"));
            foreach (var provider in new[] { chat, embedding! })
            {
                Assert.Same(connection, Field(provider, "_connection"));
                Assert.Same(catalog, Field(provider, "_catalog"));
                Assert.Equal(false, Field(provider, "_ownsConnection"));
            }
            connectionHttp = (HttpClient)Field(connection, "_httpClient");
        }
        else
        {
            Assert.Same(credentials, Field(chat, "_credentials"));
            if (embedding is not null) Assert.Same(credentials, Field(embedding, "_credentials"));
        }
        var credentialDescriptor = CurrentPrivateRpcDescriptors.Get($"sunder.package.agent.provider.{family.ToLowerInvariant()}.credentials");
        foreach (var registration in options.Providers)
        {
            var descriptor = registration.ProviderId == id + ".credentials" ? credentialDescriptor : CurrentPublicRpcDescriptors.Get(registration.ContractId);
            Assert.Equal(descriptor.Sha256, registration.DescriptorSha256); Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
            state.Add(context.PackageId, descriptor, registration.Handler, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
        }
        var credentialEndpoint = Assert.Single((await rpc.DiscoverAsync(credentialDescriptor.ContractId)).Providers).Endpoint;
        Assert.False((await Command("get-status", new { })).GetBoolean());
        Assert.True((await Command("set", "  generated-credential  ")).GetBoolean());
        Assert.Equal("generated-credential", await context.Secrets.GetSecretAsync(family == "LMStudio" ? "connection.apiKey" : "auth.apiKey"));
        if (family != "LMStudio")
        {
            foreach (var contract in family == "Gemini" ? new[] { RegressionTestBindings.ChatProviders, RegressionTestBindings.EmbeddingProviders } : [RegressionTestBindings.ChatProviders])
            {
                var endpoint = Assert.Single((await rpc.DiscoverAsync(contract)).Providers).Endpoint;
                var result = await rpc.InvokeAsync(endpoint, contract == RegressionTestBindings.ChatProviders ? "chat-provider" : "embedding-provider", "get-readiness", JsonSerializer.SerializeToElement(new { }));
                Assert.Equal("ready", result.GetProperty("status").GetString());
            }
        }
        Assert.False((await Command("clear", new { })).GetBoolean());
        await lifetime.DisposeAsync();
        if (connectionHttp is not null) Assert.Throws<ObjectDisposedException>(() => connectionHttp.Timeout = TimeSpan.FromSeconds(1));
        Assert.Throws<ObjectDisposedException>(() => services.GetService(settings.GetType()));

        Task<JsonElement> Command(string method, object request)
            => rpc.InvokeAsync(credentialEndpoint, "credential-settings", method, JsonSerializer.SerializeToElement(request)).AsTask();
        static object Field(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    }
}
