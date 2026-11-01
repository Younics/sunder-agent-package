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
        await using var runtime = RegressionTestRuntime.Create(scope.Context,
            new RegressionTestRpcClient(rpcState, "younics.ai.agent"));
        var options = runtime.Options;
        var services = runtime;
        var providers = options.Providers;
        Assert.Equal(12, providers.Count);
        Assert.Empty(Directory.EnumerateFiles(scope.RootPath, "*.db", SearchOption.AllDirectories));
        foreach (var registration in providers)
        {
            var original = registration.ContractId == "sunder.stack.contributor" ? Sunder.Sdk.Stacks.SunderStackContributorRpc.Descriptor
                : registration.ContractId == "younics.ai.agent.agent.profile"
                    ? ProfileDescriptor() : CurrentPrivateRpcDescriptors.Get(registration.ContractId);
            Assert.Equal(original.ContractId, registration.ContractId);
            Assert.Equal(original.Version, registration.ContractVersion);
            Assert.Equal(original.Sha256, registration.DescriptorSha256);
            Assert.Equal(original.AbiSha256, registration.AbiSha256);
            foreach (var service in original.Services)
                foreach (var method in service.Methods)
                    Assert.Equal(method.Errors, registration.DeclaredErrors[service.ServiceId][method.MethodId]);
        }
        var lifecycle = services.GetRequiredService<AgentWorkerLifecycle>();
        var generation = new PackageRuntimeGeneration(Guid.NewGuid(), 1);
        await lifecycle.PrepareCandidateAsync();
        try
        {
            if (failActivation)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.ActivateAsync(
                    static (_, _, _) => ValueTask.CompletedTask, default).AsTask());
                Assert.Contains("must receive an exact Runtime generation", error.Message, StringComparison.Ordinal);
                Assert.Null(services.GetRequiredService<AgentLocalStore>().OwnedRuntimeGeneration);
                Assert.False(services.GetRequiredService<AgentBackgroundWorkService>().IsRunning);
            }
            else
            {
                lifecycle.CommitGeneration(generation);
                await lifecycle.ActivateAsync(static (_, _, _) => ValueTask.CompletedTask, default).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(generation.ActivationId, services.GetRequiredService<AgentLocalStore>().OwnedRuntimeGeneration);
                Assert.True(services.GetRequiredService<AgentBackgroundWorkService>().IsRunning);
                foreach (var registration in providers)
                {
                    var descriptor = registration.ContractId == "sunder.stack.contributor" ? Sunder.Sdk.Stacks.SunderStackContributorRpc.Descriptor
                        : registration.ContractId == "younics.ai.agent.agent.profile" ? ProfileDescriptor()
                        : CurrentPrivateRpcDescriptors.Get(registration.ContractId);
                    rpcState.Add("younics.ai.agent", descriptor, registration.Handler, registration.Handler, Noop.Instance,
                        registration.ProviderId, "2.0.0");
                }
                var stack = providers.Single(provider => provider.ProviderId == "agent.stack.profiles");
                var metadata = await stack.Handler.InvokeUnaryAsync(null!, "stack-contributor", "metadata", JsonSerializer.SerializeToElement(new { }));
                Assert.Equal("younics.ai.agent.profiles", metadata.GetProperty("contributorId").GetString());
            }
        }
        finally { await lifecycle.StopAsync(default); }

        static SunderRpcContractDescriptor ProfileDescriptor()
        {
            using var receipt = CanonicalAgentRoles.Receipt();
            var value = Assert.Single(receipt.RootElement.GetProperty("contracts").EnumerateArray(),
                contract => contract.GetProperty("contractId").GetString() == "younics.ai.agent.agent.profile");
            Assert.Equal("2.0.0", value.GetProperty("contractVersion").GetString());
            var descriptor = SunderRpcContractDescriptor.Parse(System.Text.Encoding.UTF8.GetBytes(value.GetProperty("canonicalDescriptor").GetString()!));
            Assert.Equal(15, descriptor.Services.Single().Methods.Count);
            return descriptor;
        }
    }
}
