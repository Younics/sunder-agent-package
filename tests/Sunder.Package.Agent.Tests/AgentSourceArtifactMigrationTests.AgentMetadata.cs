using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyAgentMetadataConsumers(string root, string core, string driver, string ownerArchive, string cache)
    {
        var repository = BuilderSourceMigrationTests.FindRepository();
        var fixture = Directory.CreateDirectory(Path.Combine(root, "SystemPromptProvider")).FullName;
        WriteImportProject(fixture, core, driver, cache, ownerArchive, provider: true, packageId: "example.agent.prompt", assemblyName: "AgentPromptProvider");
        File.Copy(Path.Combine(core, "specs/rpc/v1/authoring/conformance/valid/minimal-contract-lock.json"), Path.Combine(fixture, "sunder-contracts.lock.json"));
        File.WriteAllText(Path.Combine(fixture, "Source.cs"), "public sealed class Seed;");
        await Run(fixture, "restore", "Imported.csproj", "--disable-parallel");
        await Run(fixture, "msbuild", "Imported.csproj", "-t:FixtureOperation", "-p:Operation=Accept");
        await Run(fixture, "msbuild", "Imported.csproj", "-t:FixtureOperation", "-p:Operation=Freeze");
        await Run(fixture, "msbuild", "Imported.csproj", "-t:FixtureOperation", "-p:Operation=Implement", "-p:ContractId=sunder.agent.system.prompt.contributor", "-p:ProviderKey=systemPrompt");
        File.Copy(Path.Combine(repository, "tests/Sunder.Package.Agent.Tests/Fixtures/SystemPromptArtifactProvider.cs.txt"), Path.Combine(fixture, "Source.cs"), overwrite: true);
        var selected = File.ReadAllBytes(Path.Combine(fixture, "sunder-contracts.lock.json"));
        await Run(fixture, "build", "Imported.csproj", "--no-restore");
        Assert.Equal(selected, File.ReadAllBytes(Path.Combine(fixture, "sunder-contracts.lock.json")));
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture, "obj/Debug/net10.0/sunder-rpc/authoring-receipt.v1.json")));
        Assert.Single(receipt.RootElement.GetProperty("bundles").EnumerateArray());
        Assert.Single(receipt.RootElement.GetProperty("providers").EnumerateArray());
        Assert.Empty(receipt.RootElement.GetProperty("uses").EnumerateArray());
        Assert.Empty(receipt.RootElement.GetProperty("packageApiImports").EnumerateArray());
        Assert.Empty(receipt.RootElement.GetProperty("platformImports").EnumerateArray());
        var load = new AssemblyLoadContext("agent-metadata-consumers", isCollectible: true);
        try
        {
            var owner = Load(Path.Combine(SingleProjectRoleOutput(Path.Combine(root, "Worker"), "worker"), "Sunder.Package.Agent.dll"));
            var memory = Load(Path.Combine(SingleProjectRoleOutput(Path.Combine(root, "MemoryWorker"), "worker"), "Sunder.Package.Agent.Memory.Semantic.dll"));
            var memoryProbes = Load(Path.Combine(root, "MemoryWorkerProbes/bin/Debug/net10.0/Sunder.Package.Agent.Memory.Semantic.SemanticProbes.dll"));
            var prompts = Load(Path.Combine(fixture, "bin/Debug/net10.0/AgentPromptProvider.dll"));
            Assert.DoesNotContain(prompts.GetReferencedAssemblies(), reference => reference.Name!.StartsWith("Sunder.Package.Agent", StringComparison.Ordinal));
            await VerifyAgentProfileCapabilities(owner, memory, memoryProbes);
            await VerifyAgentSystemPrompts(owner, prompts);
        }
        finally { load.Unload(); }
        Assembly Load(string path) { using var stream = File.OpenRead(path); return load.LoadFromStream(stream); }
    }

    internal static async Task VerifyAgentProfileCapabilities(Assembly owner, Assembly memory, Assembly probes)
    {
        using var package = RegressionTestPackageScope.Create();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        var (_, packageLifetime, provider) = CreateGeneratedWorker(owner, package.Context, rpc);
        await using var owned = packageLifetime;
        var profileType = owner.GetType("Sunder.Package.Agent.Services.AgentProfileService", true)!;
        var profile = provider.GetRequiredService(profileType);
        var has = profileType.GetMethod("HasProfileCapabilityConsumersAsync")!
            .CreateDelegate<Func<string, CancellationToken, Task<bool>>>(profile);
        Assert.False(await has("embedding", default)); Assert.False(await has(" ", default));
        var capability = new CapabilityProbe("Embedding", [new("embedding", "Embeddings", "Index semantic memory")]);
        var handler = Add(capability);
        Assert.True(await has("EMBEDDING", default)); Assert.False(await has("chat", default));
        var observed = new FilesRpcObserver(rpc, "describe");
        await using var primaryReceiver = new GeneratedServiceTestScope(observed);
        var client = await SelectAsync(primaryReceiver);
        var snapshot = await client.DescribeAsync();
        Assert.Equal(capability.ConsumerId, snapshot.ConsumerId); Assert.Equal(capability.DisplayName, snapshot.DisplayName);
        Assert.Equal(capability.Capabilities, snapshot.Capabilities);
        observed.Transform = value => value with { DescriptorSha256 = new string('d', 64) };
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await DescribeWithFreshSelectionAsync()).Error.Kind);
        observed.Transform = null;
        observed.InvalidateAfterInvoke = true;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await DescribeWithFreshSelectionAsync()).Error.Kind);
        observed.Transform = null; observed.InvalidateAfterInvoke = false;
        await state.RetireAsync(handler);
        Assert.False(await has("embedding", default));
        await primaryReceiver.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.DescribeAsync().AsTask());
        var replacement = new CapabilityProbe("Replacement", [new("chat", "Chat", "New activation")]);
        handler = Add(replacement);
        Assert.True(await has("chat", default)); Assert.False(await has("embedding", default));
        await using var replacementReceiver = new GeneratedServiceTestScope(observed);
        var next = await SelectAsync(replacementReceiver);
        var replacementSnapshot = await next.DescribeAsync();
        Assert.Equal(replacement.ConsumerId, replacementSnapshot.ConsumerId);
        Assert.Equal(replacement.Capabilities, replacementSnapshot.Capabilities);
        handler.BlockMethod = "describe"; handler.Gate = new();
        var stopping = next.DescribeAsync().AsTask();
        await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = replacementReceiver.DisposeAsync().AsTask();
        var retired = await Assert.ThrowsAsync<SunderRpcException>(() => stopping);
        Assert.Equal(SunderRpcErrorKind.Unavailable, retired.Error.Kind);
        Assert.Equal("rpc.binding.retired", retired.Error.Code);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, retired.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, retired.Error.RetrySafety);
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => next.DescribeAsync().AsTask());
        observed.AssertScopedOperations(); Assert.All(observed.Scopes, scope => Assert.True(scope.Disposed));

        SurfaceHandler Add(CapabilityProbe value)
            => (SurfaceHandler)AddGeneratedPeer(state, "sunder.package.agent.memory.semantic",
                RegressionTestBindings.ProfileCapabilityConsumers, new RegressionTestProfileCapabilityProvider(value),
                "semantic.memory.profile.capabilities", inner => new SurfaceHandler(inner));

        async Task<AgentServices.IProfileCapabilityConsumerService> SelectAsync(GeneratedServiceTestScope receiver)
            => Assert.Single((await receiver.Providers<AgentServices.IProfileCapabilityConsumerService>().ListAsync()).Providers).Service;

        async Task<SunderRpcException> DescribeWithFreshSelectionAsync()
        {
            await using var receiver = new GeneratedServiceTestScope(observed);
            var selected = await SelectAsync(receiver);
            return await Assert.ThrowsAsync<SunderRpcException>(() => selected.DescribeAsync().AsTask());
        }
    }

    private sealed class CapabilityProbe(string name, IReadOnlyList<AgentProfileCapabilityConsumerDescriptor> capabilities) : IAgentProfileCapabilityConsumer
    {
        public string ConsumerId => "fixture.capability";
        public string DisplayName => name;
        internal IReadOnlyList<AgentProfileCapabilityConsumerDescriptor> Capabilities => capabilities;
        public IReadOnlyList<AgentProfileCapabilityConsumerDescriptor> ListConsumedCapabilities() => capabilities;
    }
}
