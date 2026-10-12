using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Stacks;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyAgentExecutionSelection(Assembly owner, RegressionTestRpcState state, AgentWorkspaceRecord workspace)
    {
        using var package = RegressionTestPackageScope.Create();
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        await using var host = new AgentPromptHost(owner, package.Context, rpc, workspace, "LOCAL");
        var service = host.Services.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentExecutionTargetService", true)!);
        var listed = await AgentRoleBoundary.As<ValueTask<IReadOnlyList<AgentExecutionTargetDescriptor>>>(
            AgentRoleBoundary.Call(service, "ListTargetsAsync", CancellationToken.None)!);
        Assert.Equal("local", Assert.Single(listed).TargetId); Assert.Equal(9, listed[0].Facets.Count);
        Task<object?> Select(AgentWorkspaceBindingRecord? binding, CancellationToken token = default)
            => AgentRoleBoundary.Await(AgentRoleBoundary.Call(service, "ResolveTargetAsync", binding, token)!);
        Assert.Null(await Select(null)); Assert.Null(await Select(host.Binding! with { IsEnabled = false }));
        Assert.Null(await Select(host.Binding! with { ContributionId = "missing" }));
        var reference = (await Select(host.Binding))!;
        var locator = ReferenceJson(reference.GetType().GetProperty("Reference")!.GetValue(reference)!);
        var again = (await Select(host.Binding))!;
        Assert.True(JsonElement.DeepEquals(locator, ReferenceJson(again.GetType().GetProperty("Reference")!.GetValue(again)!)));
        Assert.Same(owner, reference.GetType().GetProperty("Service")!.GetValue(reference)!.GetType().Assembly);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Select(host.Binding, cancelled.Token));
        }
        var warmup = host.Services.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentExecutionTargetWarmupService", true)!);
        var warmed = (Task)SurfaceCall(warmup, "WarmWorkspaceAsync", host.Workspace, CancellationToken.None)!;
        await warmed.WaitAsync(TimeSpan.FromSeconds(10));
        var result = warmed.GetType().GetProperty("Result")!.GetValue(warmed)!;
        Assert.Equal("Ready", result.GetType().GetProperty("Status")!.GetValue(result)!.ToString());
        var resolver = host.Services.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentWorkspaceExecutionResolver", true)!);
        var resolved = (await AgentRoleBoundary.Await(AgentRoleBoundary.Call(resolver, "ResolveAsync", host.Workspace.WorkspaceId, CancellationToken.None)!))!;
        Assert.True(JsonElement.DeepEquals(locator, ReferenceJson(resolved.GetType().GetProperty("ExecutionTarget")!.GetValue(resolved)!)));
        var scope = resolved.GetType().GetProperty("Scope")!.GetValue(resolved)!;
        Assert.Contains((IEnumerable<string>)scope.GetType().GetProperty("WorkspacePaths")!.GetValue(scope)!, path => path == workspace.Paths[0].HostPath);
        var stack = (IPackageStackExporter)host.Services.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentWorkspaceStackContributor", true)!);
        Assert.Contains((await stack.ExportAsync(new([new(host.Workspace.WorkspaceId)]))).PackageRequirements,
             requirement => requirement.PackageId == "sunder.package.agent.execution.local");

        static JsonElement ReferenceJson(object value)
        {
            // Observe identity only; never reconstruct a live reference across CLR roles.
            var snapshot = (Sunder.Sdk.Rpc.SunderRpcProviderSnapshot)value.GetType()
                .GetProperty("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
            var json = JsonSerializer.SerializeToElement(new
            {
                snapshot.PackageId, snapshot.PackageVersion, snapshot.ProviderId,
                snapshot.ContractId, snapshot.ContractVersion, snapshot.DescriptorSha256,
                snapshot.AbiSha256, snapshot.ActivationId, snapshot.ActivationEpoch,
                snapshot.SessionGeneration, snapshot.Endpoint,
            }, MemoryProviderJson);
            Assert.Equal(11, json.EnumerateObject().Count());
            return json;
        }
    }
}
