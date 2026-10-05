using System.Reflection;
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
        var gatewayType = assembly.GetType("Sunder.Package.Agent.Builder.BuilderRpcGateway", true)!;
        var requestType = assembly.GetType("Sunder.Package.Agent.Builder.BuilderRpcRequest", true)!;
        var jsonOptions = (JsonSerializerOptions)assembly.GetType("Sunder.Package.Agent.Builder.BuilderRpcPayloadLimits", true)!
            .GetField("JsonOptions", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;
        async Task<string> invoke(ISunderRpcClient rpc, string requestJson, CancellationToken token)
        {
            await using var services = AgentSourceArtifactMigrationTests.CreateGeneratedApp(assembly, context, rpc, new CompositionBackgroundProcessQueue());
            var gateway = services.GetRequiredService(gatewayType);
            var requestValue = JsonSerializer.Deserialize(requestJson, requestType, jsonOptions)!;
            var result = await AgentRoleBoundary.Await(AgentRoleBoundary.Call(gateway, "InvokeAsync", requestValue, token)!);
            return JsonSerializer.Serialize(result, result!.GetType(), jsonOptions);
        }
        const string request = "{\"kind\":0,\"timeoutSeconds\":600}";
        using (var missingState = new RegressionTestRpcState())
        {
            var missing = await Assert.ThrowsAsync<SunderRpcException>(() => invoke(
                new RegressionTestRpcClient(missingState, "sunder.package.agent.builder"), request, CancellationToken.None));
            Assert.Equal(SunderRpcErrorKind.Unavailable, missing.Error.Kind);
            Assert.Equal("agent.builder.workspace-execution-unavailable", missing.Error.Code);
        }
        var unavailable = await Assert.ThrowsAsync<SunderRpcException>(() => invoke(client, request, CancellationToken.None));
        Assert.Equal("agent.builder.workspace-execution-unavailable", unavailable.Error.Code);

        var now = DateTimeOffset.UtcNow;
        var resolver = new PilotWorkspaceResolver
        {
            List = _ => ValueTask.FromResult<IReadOnlyList<AgentWorkspaceRecord>>(
                [new("workspace.one", "Workspace 漢字", "A complete content result", now, now)]),
        };
        state.AddGenerated("sunder.package.agent", resolver,
            new Rpc.Public.WorkspaceExecutionResolverService(resolver).CreateSunderRpcProviderRegistration(), NoopDisposable.Instance, "agent.workspace.execution.resolver");
        state.Add("unrelated.package", descriptor, new object(), registration.Handler,
            NoopDisposable.Instance, "builder.unrelated", packageVersion: "2.0.0");
        var response = await invoke(client, request, CancellationToken.None);
        using (var json = JsonDocument.Parse(response))
        {
            var workspace = Assert.Single(json.RootElement.GetProperty("workspaces").EnumerateArray());
            Assert.Equal("workspace.one", workspace.GetProperty("workspaceId").GetString());
            Assert.Equal("Workspace 漢字", workspace.GetProperty("displayName").GetString());
        }
        Assert.Equal(1, resolver.Calls);

        var oversized = JsonSerializer.Serialize(new { kind = 2, timeoutSeconds = 600, fileName = new string('x', 1024 * 1024) });
        var sizeError = await Assert.ThrowsAsync<InvalidOperationException>(() => invoke(client, oversized, CancellationToken.None));
        Assert.Contains("exceeds the host transport limit", sizeError.Message, StringComparison.Ordinal);
        Assert.Equal(1, resolver.Calls);

        var invalid = await Assert.ThrowsAsync<SunderRpcException>(() => invoke(client, "{\"kind\":0,\"timeoutSeconds\":0}", CancellationToken.None));
        Assert.Equal(SunderRpcErrorKind.Validation, invalid.Error.Kind);
        Assert.Equal(1, resolver.Calls);

        state.FailNextResponseContentRegistration(new InvalidDataException("private content-store failure"));
        var contentError = await Assert.ThrowsAsync<SunderRpcException>(() => invoke(client, request, CancellationToken.None));
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, contentError.Error.Kind);
        Assert.DoesNotContain("private content-store failure", contentError.Message, StringComparison.Ordinal);

        using (var cancelled = new CancellationTokenSource())
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            resolver.List = async token => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return []; };
            var pending = invoke(client, request, cancelled.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        var retirementStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        resolver.List = async token => { retirementStarted.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return []; };
        var retiring = invoke(client, request, CancellationToken.None);
        await retirementStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await workerLifetime.DisposeAsync();
        var retirement = await Assert.ThrowsAsync<SunderRpcException>(() => retiring.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(SunderRpcErrorKind.Domain, retirement.Error.Kind);
        Assert.Equal("agent.builder.workspace-execution-unavailable", retirement.Error.Code);

        state.Add("sunder.package.agent.builder", descriptor, new object(), registration.Handler,
            NoopDisposable.Instance, "builder.alternate", packageVersion: "2.0.0");
        var ambiguous = await Assert.ThrowsAsync<SunderRpcException>(() => invoke(client, request, CancellationToken.None));
        Assert.Equal("rpc.provider-ambiguous", ambiguous.Error.Code);
        Assert.Equal(exportBytes, File.ReadAllBytes(exportPath));
    }

    private sealed class PilotWorkspaceResolver : IAgentWorkspaceExecutionResolver
    {
        public required Func<CancellationToken, ValueTask<IReadOnlyList<AgentWorkspaceRecord>>> List { get; set; }
        public int Calls { get; private set; }
        public ValueTask<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return List(cancellationToken);
        }
        public ValueTask<AgentWorkspaceExecutionResolution> ResolveAsync(string workspaceId, CancellationToken cancellationToken = default)
            => ValueTask.FromException<AgentWorkspaceExecutionResolution>(new NotSupportedException());
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}
