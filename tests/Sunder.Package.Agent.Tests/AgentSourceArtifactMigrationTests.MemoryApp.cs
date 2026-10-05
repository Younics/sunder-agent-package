using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias MemoryWorker;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Runtime;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal const string MemoryAppCompositionSource = "src/Sunder.Package.Agent.Memory.Semantic/Rpc/AppComposition.cs";

    internal static readonly string[] MemoryAppTransportSources =
    [
        "src/Sunder.Package.Agent.Memory.Semantic/App/MemoryAppRuntimeGateway.Transport.Legacy.cs",
        "src/Sunder.Package.Agent.Memory.Semantic/Rpc/MemoryAppRuntimeGateway.Transport.cs",
    ];

    internal static readonly string[] MemoryAppGatewaySources =
    [
        "src/Sunder.Package.Agent.Memory.Semantic/App/MemoryAppRuntimeGateway.cs",
        MemoryAppTransportSources[1],
        "src/Sunder.Package.Agent.Shared/Presentation/OwnerStreamObserver.cs",
        "src/Sunder.Package.Agent.Shared/Presentation/PresentationExceptionPolicy.cs",
    ];

    private static async Task VerifyFullMemoryApp(string worker)
    {
        var load = new AssemblyLoadContext("memory-full-presentation", isCollectible: true);
        try
        {
            using var workerStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(worker, "worker"), "Sunder.Package.Agent.Memory.Semantic.dll"));
            var workerAssembly = load.LoadFromStream(workerStream);
            var probes = await BuildRoleProbes(worker, workerAssembly, "MemoryRoleLocalProbes.cs.txt", output: SingleProjectRoleOutput(worker, "worker"));
            using var appStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(worker, "app"), "Sunder.Package.Agent.Memory.Semantic.App.dll"));
            var appAssembly = load.LoadFromStream(appStream);
            using var agentStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(Path.Combine(Path.GetDirectoryName(worker)!, "Worker"), "worker"), "Sunder.Package.Agent.dll"));
            var agentOwner = load.LoadFromStream(agentStream);
            Assert.DoesNotContain(appAssembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Memory.Semantic" or "Sunder.Sdk.Worker");
            await VerifyRoleLocalMemoryModels(workerAssembly, appAssembly, agentOwner);
            await VerifyAgentCleanupConsumers(agentOwner, workerAssembly, probes);
            await VerifyAgentCleanupDispatchers(agentOwner, workerAssembly, probes);
        }
        finally { load.Unload(); }
    }

    private static void VerifyMemoryProviderApp(string worker, Assembly assembly)
    {
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(CanonicalPackageRoles.Analysis(worker), "app-authoring-receipt.v1.json")));
        Assert.Equal("app", receipt.RootElement.GetProperty("role").GetString());
        Assert.Single(receipt.RootElement.GetProperty("contracts").EnumerateArray());
        Assert.Empty(receipt.RootElement.GetProperty("providers").EnumerateArray());
        Assert.Empty(receipt.RootElement.GetProperty("providerImports").EnumerateArray());
        Assert.Empty(receipt.RootElement.GetProperty("packageArtifactImports").EnumerateArray());
        var use = Assert.Single(receipt.RootElement.GetProperty("uses").EnumerateArray());
        Assert.True(use.GetProperty("required").GetBoolean());
        Assert.Equal(new[] { "discover", "invoke", "subscribe" }, use.GetProperty("actions").EnumerateArray().Select(value => value.GetString()));
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Memory.Semantic" or "Sunder.Sdk.Worker");
        Assert.NotNull(assembly.GetType("Sunder.Generated.Package.AppModule"));
    }
}
