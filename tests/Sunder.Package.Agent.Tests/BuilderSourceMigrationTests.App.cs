using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class BuilderSourceMigrationTests
{
    private static async Task VerifyAppAsync(string root, Assembly assembly, IPackageContext context,
        SunderRpcContractDescriptor descriptor, SunderWorkerProviderRegistration registration,
        IAsyncDisposable workerLifetime, RegressionTestRpcState state, RegressionTestRpcClient client)
    {
        var exportPath = Path.Combine(CanonicalPackageRoles.Analysis(root), "package-export.v1.json");
        var exportBytes = File.ReadAllBytes(exportPath);
        using (var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(CanonicalPackageRoles.Analysis(root), "app-authoring-receipt.v1.json"))))
        {
            Assert.Empty(receipt.RootElement.GetProperty("providers").EnumerateArray());
            var use = Assert.Single(receipt.RootElement.GetProperty("uses").EnumerateArray());
            Assert.Equal("app", use.GetProperty("role").GetString());
            Assert.True(use.GetProperty("required").GetBoolean());
        }
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), value => value.Name is
            "Sunder.Package.Agent.Builder.Protocol" or "Sunder.Package.Agent.Builder" or "BuilderSourcePilot" or "Sunder.Sdk.Worker");
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Builder.BuilderRuntimeService"));
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Builder.BuilderRpcGateway"));
        var gatewayType = assembly.GetType("Sunder.Package.Agent.Builder.BuilderWorkspaceExecutionService", true)!;
        var runtimeType = assembly.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Builder_Services.IBuilderRuntimeService", true)!;
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { Converters = { new SunderRpcJsonConverterFactory() } };
        async Task<string> invoke(ISunderRpcClient rpc, CancellationToken token)
        {
            await using var services = AgentSourceArtifactMigrationTests.CreateGeneratedApp(assembly, context, rpc, new CompositionBackgroundProcessQueue());
            var gateway = services.GetRequiredService(gatewayType);
            var result = await AgentRoleBoundary.Await(AgentRoleBoundary.Call(gateway, "ListWorkspacesAsync", token)!);
            return JsonSerializer.Serialize(result, result!.GetType(), jsonOptions);
        }
        using (var missingState = new RegressionTestRpcState())
        {
            var missing = await Assert.ThrowsAsync<SunderRpcException>(() => invoke(
                new RegressionTestRpcClient(missingState, "sunder.package.agent.builder"), CancellationToken.None));
            Assert.Equal(SunderRpcErrorKind.NotFound, missing.Error.Kind);
            Assert.Equal("rpc.provider-not-found", missing.Error.Code);
        }
        var unavailable = await Assert.ThrowsAsync<SunderRpcException>(() => invoke(client, CancellationToken.None));
        Assert.Equal(SunderRpcErrorKind.Domain, unavailable.Error.Kind);
        Assert.Equal("agent.builder.workspace-execution-unavailable", unavailable.Error.Code);

        var now = DateTimeOffset.UtcNow;
        var resolver = new BuilderWorkspaceResolverProbe
        {
            List = _ => ValueTask.FromResult<IReadOnlyList<AgentWorkspaceRecord>>(
                [new("workspace.one", "Workspace 漢字", "A complete content result", now, now)]),
        };
        var fixtureRoot = Directory.CreateTempSubdirectory("sunder-agent-builder-resolver-").FullName;
        var resolverLoad = new AssemblyLoadContext("agent-builder-resolver", isCollectible: true);
        try
        {
            var resolverProvider = await AgentPublicSourceMigrationTests.CreateGeneratedExtensionProviderAsync(
                fixtureRoot, resolverLoad, "WorkspaceExecutionResolver", resolver.Invoke);
            await using var resolverReceiver = new GeneratedServiceTestScope(client.ForCaller("sunder.package.agent"));
            var resolverRegistration = resolverReceiver.Register(resolverProvider, RegressionTestBindings.WorkspaceExecutionResolver);
            state.Add("sunder.package.agent", CurrentPublicRpcDescriptors.Get(resolverRegistration.ContractId), resolverProvider,
                resolverRegistration.Handler, NoopDisposable.Instance, resolverRegistration.ProviderId, packageVersion: "2.0.0");
        state.Add("unrelated.package", descriptor, new object(), registration.Handler,
            NoopDisposable.Instance, "builder.unrelated", packageVersion: "2.0.0");
        var response = await invoke(client, CancellationToken.None);
        using (var json = JsonDocument.Parse(response))
        {
            var workspace = Assert.Single(json.RootElement.EnumerateArray());
            Assert.Equal("workspace.one", workspace.GetProperty("workspaceId").GetString());
            Assert.Equal("Workspace 漢字", workspace.GetProperty("displayName").GetString());
        }
        Assert.Equal(1, resolver.Calls);

        await using (var appServices = AgentSourceArtifactMigrationTests.CreateGeneratedApp(assembly, context, client, new CompositionBackgroundProcessQueue()))
        {
            var runtime = appServices.GetRequiredService(runtimeType);
            var execute = runtimeType.GetMethod("ExecuteProcessAsync")!;
            var oversized = JsonSerializer.SerializeToElement(new
            {
                fileName = new string('x', 1024 * 1024), arguments = Array.Empty<string>(),
                workingDirectory = (string?)null, timeoutSeconds = 600,
            }, jsonOptions).Deserialize(execute.GetParameters()[1].ParameterType, jsonOptions)!;
            var sizeError = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            {
                await using var unexpected = (Sunder.Sdk.Services.SunderContent)(await AgentRoleBoundary.Await(
                    AgentRoleBoundary.Call(runtime, "ExecuteProcessAsync", "workspace.one", oversized, CancellationToken.None)!))!;
            });
            Assert.Equal(SunderRpcErrorKind.Domain, sizeError.Error.Kind);
            Assert.Equal("agent.builder.request-too-large", sizeError.Error.Code);
        }
        Assert.Equal(1, resolver.Calls);

        var builderEndpoint = Assert.Single((await client.DiscoverAsync(descriptor.ContractId)).Providers,
            provider => provider.PackageId == "sunder.package.agent.builder").Endpoint;
        var invalid = await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync(builderEndpoint,
            "builder-runtime", "execute-process", JsonSerializer.SerializeToElement(new { })).AsTask());
        Assert.Equal(SunderRpcErrorKind.Validation, invalid.Error.Kind);
        Assert.Equal(1, resolver.Calls);

        state.FailNextResponseContentRegistration(new InvalidDataException("private content-store failure"));
        var contentError = await Assert.ThrowsAsync<SunderRpcException>(() => invoke(client, CancellationToken.None));
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, contentError.Error.Kind);
        Assert.DoesNotContain("private content-store failure", contentError.Message, StringComparison.Ordinal);

        using (var cancelled = new CancellationTokenSource())
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            resolver.List = async token => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return []; };
            var pending = invoke(client, cancelled.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        var retirementStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        resolver.List = async token => { retirementStarted.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return []; };
        var retiring = invoke(client, CancellationToken.None);
        await retirementStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await workerLifetime.DisposeAsync();
        var retirement = await Assert.ThrowsAsync<SunderRpcException>(() => retiring.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(retirement.Error.Kind == SunderRpcErrorKind.Domain,
            $"Builder retirement boundary: {retirement.Error.Kind}/{retirement.Error.Code}; provider failure={state.LastInvocationFailure}");
        Assert.Equal("agent.builder.workspace-execution-unavailable", retirement.Error.Code);

        state.Add("sunder.package.agent.builder", descriptor, new object(), registration.Handler,
            NoopDisposable.Instance, "builder.alternate", packageVersion: "2.0.0");
        var ambiguous = await Assert.ThrowsAsync<SunderRpcException>(() => invoke(client, CancellationToken.None));
        Assert.Equal("rpc.provider-ambiguous", ambiguous.Error.Code);
        Assert.Equal(exportBytes, File.ReadAllBytes(exportPath));
        }
        finally
        {
            resolverLoad.Unload();
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private sealed class BuilderWorkspaceResolverProbe
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { Converters = { new SunderRpcJsonConverterFactory() } };
        public required Func<CancellationToken, ValueTask<IReadOnlyList<AgentWorkspaceRecord>>> List { get; set; }
        public int Calls { get; private set; }
        public object Invoke(string method, Type resultType, object? request, CancellationToken token)
        {
            var reply = method == "ListWorkspacesAsync" ? nameof(Reply) : nameof(NotSupported);
            return typeof(BuilderWorkspaceResolverProbe).GetMethod(reply, BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(resultType).Invoke(this, [token])!;
        }

        private async ValueTask<T> Reply<T>(CancellationToken token)
        {
            Calls++;
            var list = await List(token);
            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(list, Json), Json)!;
        }

        private static ValueTask<T> NotSupported<T>(CancellationToken token)
            => ValueTask.FromException<T>(new NotSupportedException());
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}
