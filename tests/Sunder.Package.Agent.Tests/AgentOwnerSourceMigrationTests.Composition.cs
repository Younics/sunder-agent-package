using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentOwnerSourceMigrationTests
{
    private static async Task VerifyDeferredComposition(Assembly assembly, bool failActivation)
    {
        using var scope = RegressionTestPackageScope.Create();
        using var rpcState = new RegressionTestRpcState();
        var generated = AgentSourceArtifactMigrationTests.CreateGeneratedWorker(typeof(AgentWorkerLifecycle).Assembly, scope.Context,
            new RegressionTestRpcClient(rpcState, "sunder.package.agent"));
        await using var generatedLifetime = generated.Lifetime;
        var services = generated.Services;
        var resolved = 0;
        var providers = new List<DeferredAgentRpcServiceHandler>();
        Add("Public.RuntimeCatalogService", sp => [sp.GetRequiredService<AgentRuntimeCatalog>()]);
        Add("Public.WorkspaceExecutionResolverService", sp => [sp.GetRequiredService<AgentWorkspaceExecutionResolver>()]);
        Add("Public.ChildRunExecutorService", sp => [sp.GetRequiredService<AgentRunCoordinator>()]);
        Add("Public.SessionCleanerService", sp => [sp.GetRequiredService<AgentAttachmentService>()]);
        Add("Public.HistorySessionCleanerProvider", sp => [sp.GetRequiredService<HistorySearchIndexingService>()]);
        Add("Public.RunControlService", sp => [sp.GetRequiredService<AgentRunControlRegistry>()]);
        Add("Public.BehaviorLoopService", sp => [sp.GetRequiredService<DefaultAgentBehaviorLoop>(), sp.GetRequiredService<AgentRpcCatalog>()]);
        Add("Public.PromptContextContributorService", sp => [sp.GetRequiredService<WorkspaceDocumentationContextService>(), sp.GetRequiredService<AgentRpcCatalog>()]);
        Add("Owner.AgentProfileStackProvider", sp => [sp.GetRequiredService<AgentProfileStackContributor>()]);
        Add("Owner.AgentWorkspaceStackProvider", sp => [sp.GetRequiredService<AgentWorkspaceStackContributor>()]);
        Add("Owner.OwnerRuntimeService", sp => [sp.GetRequiredService<AgentOwnerRuntimeProvider>()]);

        Assert.Equal(0, resolved);
        Assert.Equal(11, providers.Count);
        Assert.All(providers, provider => Assert.False(provider.IsActive));
        Assert.Empty(Directory.EnumerateFiles(scope.RootPath, "*.db", SearchOption.AllDirectories));
        foreach (var provider in providers)
        {
            var registration = provider.Registration;
            var original = registration.ContractId == "sunder.stack.contributor" ? Sunder.Sdk.Stacks.SunderStackContributorRpc.Descriptor
                : RegressionTestRpcDescriptors.Get(registration.ContractId);
            Assert.Equal(original.ContractId, registration.ContractId);
            Assert.Equal(original.Version, registration.ContractVersion);
            Assert.Equal(original.Sha256, registration.DescriptorSha256);
            Assert.Equal(original.AbiSha256, registration.AbiSha256);
            foreach (var service in original.Services)
                foreach (var method in service.Methods)
                    Assert.Equal(method.Errors, registration.DeclaredErrors[service.ServiceId][method.MethodId]);
        }
        var lifecycle = new AgentWorkerLifecycle(services, providers);
        var generation = new PackageRuntimeGeneration(Guid.NewGuid(), 1);
        var stack = providers.Single(provider => provider.Registration.ProviderId == "agent.stack.profiles");
        var pending = failActivation ? null : stack.Registration.Handler.InvokeUnaryAsync(null!, "stack-contributor", "metadata",
            JsonSerializer.SerializeToElement(new { })).AsTask();
        if (pending is not null) Assert.False(pending.IsCompleted);
        await lifecycle.PrepareCandidateAsync();
        lifecycle.CommitGeneration(generation);
        Assert.Equal(0, resolved);
        try
        {
            if (failActivation)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.ActivateAsync(
                    static (_, _, _) => ValueTask.CompletedTask, default).AsTask());
                Assert.Contains("does not match its generated declaration", error.Message, StringComparison.Ordinal);
                Assert.All(providers, provider => Assert.False(provider.IsActive));
                Assert.Null(services.GetRequiredService<AgentLocalStore>().OwnedRuntimeGeneration);
                Assert.False(services.GetRequiredService<AgentBackgroundWorkService>().IsRunning);
            }
            else
            {
                await lifecycle.ActivateAsync(static (_, _, _) => ValueTask.CompletedTask, default).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(11, resolved);
                Assert.All(providers, provider => Assert.True(provider.IsActive));
                Assert.Equal(generation.ActivationId, services.GetRequiredService<AgentLocalStore>().OwnedRuntimeGeneration);
                Assert.True(services.GetRequiredService<AgentBackgroundWorkService>().IsRunning);
                Assert.Equal("sunder.package.agent.profiles", (await pending!.WaitAsync(TimeSpan.FromSeconds(5))).GetProperty("contributorId").GetString());
            }
        }
        finally { await lifecycle.StopAsync(default); }
        Assert.All(providers, provider => Assert.False(provider.IsActive));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stack.Registration.Handler.InvokeUnaryAsync(null!, "stack-contributor", "metadata",
            JsonSerializer.SerializeToElement(new { })).AsTask());

        void Add(string name, Func<IServiceProvider, object[]> arguments)
        {
            var type = assembly.GetType("Sunder.Package.Agent.Rpc." + name, true)!;
            var declare = type.GetMethod("DeclareSunderRpcProvider", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)!
                .CreateDelegate<Func<ISunderRpcServiceHandler, SunderWorkerProviderRegistration>>();
            providers.Add(new(declare, sp =>
            {
                resolved++;
                var instance = AgentRoleBoundary.Create(type, arguments(sp));
                var registration = Assert.IsType<SunderWorkerProviderRegistration>(type.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(instance, null));
                return failActivation && name == "Owner.OwnerRuntimeService"
                    ? new(registration.ProviderId, registration.ContractId, registration.ContractVersion, new string('f', 64), registration.AbiSha256, registration.Handler)
                    : registration;
            }));
        }
    }
}
