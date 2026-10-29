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
        Assert.Equal(new[] { "cancel-job", "complete-development-command", "connect-development-app", "discover", "get-backend-id", "get-job", "get-toolchains", "inspect", "read-development-command", "start-job" },
            Assert.Single(descriptor.Services).Methods.Select(method => method.MethodId).Order(StringComparer.Ordinal));
        using (var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(CanonicalPackageRoles.Analysis(worker), "authoring-receipt.v1.json"))))
        {
            var uses = receipt.RootElement.GetProperty("uses").EnumerateArray().ToArray();
            Assert.Equal(CurrentPublicRpcDescriptors.ContractIds.Order(StringComparer.Ordinal),
                uses.Select(use => use.GetProperty("contractKey").GetString()).Order(StringComparer.Ordinal));
            Assert.All(uses, use =>
            {
                Assert.Equal("runtime", use.GetProperty("role").GetString());
                Assert.False(use.GetProperty("required").GetBoolean());
            });
        }

        using var package = RegressionTestPackageScope.Create();
        var physical = OperatingSystem.IsMacOS() && package.RootPath.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + package.RootPath : package.RootPath;
        var context = new TestPackageContext("younics.sunder.package.maker", physical, version: "2.0.0")
            { DevelopmentToolchain = ReadDevelopmentToolchain() };
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var client = new RegressionTestRpcClient(state, context.PackageId);
        var composition = CreateGeneratedWorker(runtime, context, client);
        await using var lifetime = composition.Lifetime;
        Assert.Equal(0, state.DiscoveryCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(physical));
        Assert.Equal(new[] { "builder.runtime", "maker.permissions", "maker.tools" },
            composition.Options.Providers.Select(provider => provider.ProviderId).Order(StringComparer.Ordinal));
        var registration = Assert.Single(composition.Options.Providers, provider => provider.ProviderId == "builder.runtime");
        foreach (var provider in composition.Options.Providers.Where(provider => provider.ProviderId != "builder.runtime"))
        {
            var expected = CurrentPublicRpcDescriptors.Get(provider.ProviderId == "maker.tools"
                ? "younics.ai.agent.tool.source" : "younics.ai.agent.permission.surface");
            Assert.Equal(expected.ContractId, provider.ContractId);
            Assert.Equal(expected.Version, provider.ContractVersion);
            Assert.Equal(expected.Sha256, provider.DescriptorSha256);
            Assert.Equal(expected.AbiSha256, provider.AbiSha256);
        }
        Assert.Equal("builder.runtime", registration.ProviderId);
        Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
        Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
        var service = Assert.Single(descriptor.Services);
        Assert.Equal(service.Methods.Count, registration.DeclaredErrors[service.ServiceId].Count);
        foreach (var method in service.Methods)
            Assert.Equal(method.Errors, registration.DeclaredErrors[service.ServiceId][method.MethodId]);
        state.Add(context.PackageId, descriptor, registration.Handler, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
        await ActivateGeneratedComposition(composition.Options);
        Assert.Equal(0, state.DiscoveryCount);
        await BuilderSourceMigrationTests.VerifyAppAsync(worker, runtime, presentation, context, descriptor, registration, lifetime, state, client);
    }

    private static Sunder.Sdk.Abstractions.SunderDevelopmentToolchain ReadDevelopmentToolchain()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(BuilderSourceMigrationTests.FindRepository(), "../sunder-core/eng/toolchains.json")));
        var policy = document.RootElement;
        return new(policy.GetProperty("dotnetSdk").GetString()!, policy.GetProperty("node").GetString()!, policy.GetProperty("npm").GetString()!);
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
