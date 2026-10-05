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
        var has = profileType.GetMethod("HasProfileCapabilityConsumers")!.CreateDelegate<Func<string, bool>>(profile);
        Assert.False(has("embedding")); Assert.False(has(" "));
        var capability = new CapabilityProbe("Embedding", [new("embedding", "Embeddings", "Index semantic memory")]);
        var serviceType = memory.GetType("Sunder.Package.Agent.Memory.Semantic.Rpc.ProfileCapabilityProvider", true)!;
        var service = Activator.CreateInstance(serviceType, RoleLocal(capability))!;
        var registration = (SunderWorkerProviderRegistration)serviceType.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(service, null)!;
        state.Add("sunder.package.agent.memory.semantic", RegressionTestRpcDescriptors.Get(registration.ContractId), registration.Handler,
            registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
        Assert.True(has("EMBEDDING")); Assert.False(has("chat"));
        var snapshot = Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.ProfileCapabilityConsumer)).Providers);
        var observed = new FilesRpcObserver(rpc, "describe");
        var clientType = owner.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedProfileCapabilityClient", true)!;
        IAgentProfileCapabilityConsumer Create() => AgentRoleBoundary.As<IAgentProfileCapabilityConsumer>(Activator.CreateInstance(clientType, observed, snapshot)!);
        var client = Create(); await using var lifetime = (IAsyncDisposable)client;
        Assert.Equal(capability.ConsumerId, client.ConsumerId); Assert.Equal(capability.DisplayName, client.DisplayName);
        Assert.Equal(capability.Capabilities, client.ListConsumedCapabilities());
        observed.Transform = value => value with { DescriptorSha256 = new string('d', 64) };
        var stale = Create(); await using var staleLifetime = (IAsyncDisposable)stale;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, Assert.Throws<SunderRpcException>(() => stale.ListConsumedCapabilities()).Error.Kind);
        observed.Transform = null;
        observed.InvalidateAfterInvoke = true;
        var late = Create(); await using var lateLifetime = (IAsyncDisposable)late;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, Assert.Throws<SunderRpcException>(() => late.ListConsumedCapabilities()).Error.Kind);
        observed.Transform = null; observed.InvalidateAfterInvoke = false;
        await state.RetireAsync(registration.Handler);
        Assert.False(has("embedding"));
        await lifetime.DisposeAsync();
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, Assert.Throws<SunderRpcException>(() => client.ConsumerId).Error.Kind);
        var replacement = new CapabilityProbe("Replacement", [new("chat", "Chat", "New activation")]);
        var replacementService = Activator.CreateInstance(serviceType, RoleLocal(replacement))!;
        var next = (SunderWorkerProviderRegistration)serviceType.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(replacementService, null)!;
        state.Add("sunder.package.agent.memory.semantic", RegressionTestRpcDescriptors.Get(next.ContractId), next.Handler,
            next.Handler, Noop.Instance, next.ProviderId, "2.0.0");
        Assert.True(has("chat")); Assert.False(has("embedding"));
        Assert.Equal(0, observed.UnscopedLookups); Assert.All(observed.Scopes, scope => Assert.True(scope.Disposed));

        object RoleLocal(CapabilityProbe value) => Activator.CreateInstance(probes.GetType("RoleLocalProfileCapabilityProbe", true)!,
            value.ConsumerId, value.DisplayName, JsonSerializer.Serialize(value.Capabilities, MemoryProviderJson))!;
    }

    private sealed class CapabilityProbe(string name, IReadOnlyList<AgentProfileCapabilityConsumerDescriptor> capabilities) : IAgentProfileCapabilityConsumer
    {
        public string ConsumerId => "fixture.capability";
        public string DisplayName => name;
        internal IReadOnlyList<AgentProfileCapabilityConsumerDescriptor> Capabilities => capabilities;
        public IReadOnlyList<AgentProfileCapabilityConsumerDescriptor> ListConsumedCapabilities() => capabilities;
    }
}
