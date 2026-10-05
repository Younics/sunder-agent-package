using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Stacks;
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
        var listed = (IReadOnlyList<AgentExecutionTargetDescriptor>)SurfaceCall(service, "ListTargets")!;
        Assert.Equal("local", Assert.Single(listed).TargetId); Assert.Equal(9, listed[0].Facets.Count);
        Func<AgentWorkspaceBindingRecord?, AgentRpcReference<IAgentExecutionTarget>?> select = binding =>
            AgentRoleBoundary.As<AgentRpcReference<IAgentExecutionTarget>?>(AgentRoleBoundary.Call(service, "ResolveTargetReference", binding)!);
        Func<AgentWorkspaceBindingRecord?, CancellationToken, ValueTask<AgentRpcReference<IAgentExecutionTarget>?>> selectAsync = (binding, token) =>
            AgentRoleBoundary.As<ValueTask<AgentRpcReference<IAgentExecutionTarget>?>>(AgentRoleBoundary.Call(service, "ResolveTargetReferenceAsync", binding, token)!);
        Assert.Null(select(null)); Assert.Null(select(host.Binding! with { IsEnabled = false }));
        Assert.Null(await selectAsync(host.Binding! with { ContributionId = "missing" }, default));
        var reference = select(host.Binding)!;
        Assert.Equal(reference.ToHandle(), (await selectAsync(host.Binding, default))!.ToHandle());
        Assert.True(reference.TryAcquire(out var lease));
        using (lease) Assert.Same(owner, AgentRoleBoundary.Unwrap(lease.Service).GetType().Assembly);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selectAsync(host.Binding, cancelled.Token).AsTask());
        }
        var warmup = host.Services.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentExecutionTargetWarmupService", true)!);
        var warmed = (Task)SurfaceCall(warmup, "WarmWorkspaceAsync", host.Workspace, CancellationToken.None)!;
        await warmed.WaitAsync(TimeSpan.FromSeconds(10));
        var result = warmed.GetType().GetProperty("Result")!.GetValue(warmed)!;
        Assert.Equal("Ready", result.GetType().GetProperty("Status")!.GetValue(result)!.ToString());
        var resolver = AgentRoleBoundary.As<IAgentWorkspaceExecutionResolver>(host.Services.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentWorkspaceExecutionResolver", true)!));
        var resolved = await resolver.ResolveAsync(host.Workspace.WorkspaceId);
        Assert.Same(owner, AgentRoleBoundary.Unwrap(resolved.ExecutionTarget).GetType().Assembly);
        Assert.Equal(reference.ToHandle(), resolved.ExecutionTargetHandle);
        Assert.Contains(resolved.Scope.WorkspacePaths, path => path == workspace.Paths[0].HostPath);
        var stack = (IPackageStackExporter)host.Services.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentWorkspaceStackContributor", true)!);
        Assert.Contains((await stack.ExportAsync(new([new(host.Workspace.WorkspaceId)]))).PackageRequirements,
            requirement => requirement.PackageId == "sunder.package.agent.execution.local");
    }
}
