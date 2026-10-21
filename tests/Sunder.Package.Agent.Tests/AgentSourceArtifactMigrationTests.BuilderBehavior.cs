using System.Reflection;
using System.Text.Json;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.TestSupport;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static async Task VerifyBuilderRoles(string worker)
    {
        using var roles = new CanonicalPackageRoles();
        var runtime = roles.Load("Builder", "worker", worker);
        var presentation = roles.Load("Builder", "app", worker);
        AssertBuilderSemanticOwnership(runtime, app: false);
        AssertBuilderSemanticOwnership(presentation, app: true);
        foreach (var name in new[] { "BuilderProjectKind", "BuilderProjectInspection", "BuilderJobRequest", "BuilderJobSnapshot", "BuilderToolchainStatus" })
            Assert.NotSame(runtime.GetType("Sunder.Package.Agent.Builder." + name, true), presentation.GetType("Sunder.Package.Agent.Builder." + name, true));
        var descriptor = CanonicalPackageRoles.OwnedDescriptor(worker, "younics.sunder.package.maker.runtime");
        Assert.Equal(new[] { "cancel-job", "discover", "get-backend-id", "get-job", "get-toolchains", "inspect", "start-job" },
            Assert.Single(descriptor.Services).Methods.Select(method => method.MethodId).Order(StringComparer.Ordinal));
        using (var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(CanonicalPackageRoles.Analysis(worker), "authoring-receipt.v1.json"))))
            Assert.Empty(receipt.RootElement.GetProperty("uses").EnumerateArray());

        using var package = RegressionTestPackageScope.Create();
        var physical = OperatingSystem.IsMacOS() && package.RootPath.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + package.RootPath : package.RootPath;
        var context = new TestPackageContext("younics.sunder.package.maker", physical, version: "2.0.0");
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var client = new RegressionTestRpcClient(state, context.PackageId);
        var composition = CreateGeneratedWorker(runtime, context, client);
        await using var lifetime = composition.Lifetime;
        Assert.Equal(0, state.DiscoveryCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(physical));
        var registration = Assert.Single(composition.Options.Providers);
        Assert.Equal("builder.runtime", registration.ProviderId);
        Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
        Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
        var service = Assert.Single(descriptor.Services);
        Assert.Equal(service.Methods.Count, registration.DeclaredErrors[service.ServiceId].Count);
        foreach (var method in service.Methods)
            Assert.Equal(method.Errors, registration.DeclaredErrors[service.ServiceId][method.MethodId]);
        state.Add(context.PackageId, descriptor, registration.Handler, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
        await ActivateGeneratedComposition(composition.Options);
        await BuilderSourceMigrationTests.VerifyAppAsync(worker, runtime, presentation, context, descriptor, registration, lifetime, state, client);
    }

    private static async Task<object?> Call(object instance, string method, params object?[] args)
    {
        object? pending;
        try { pending = AgentRoleBoundary.Call(instance, method, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        var task = pending as Task ?? (Task)pending!.GetType().GetMethod("AsTask")!.Invoke(pending, null)!;
        await task.WaitAsync(TimeSpan.FromSeconds(20));
        return AgentRoleBoundary.AssertionValue(task.GetType().GetProperty("Result")?.GetValue(task));
    }
}
