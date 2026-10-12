using System.IO.Compression;
using System.Reflection;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private const string ImportedModels = "Sunder.Generated.Packages.Sunder_Package_Agent_Models.";
    private const string ImportedServices = "Sunder.Generated.Packages.Sunder_Package_Agent_Services.";

    private static Type AssertImportedRoleModel(Assembly assembly, string name)
    {
        var type = assembly.GetType(ImportedModels + name, true)!;
        Assert.Same(assembly, type.Assembly);
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Contracts.Models." + name));
        return type;
    }

    private static void AssertProviderSemanticOwnership(Assembly worker, Assembly app)
    {
        Assert.DoesNotContain(worker.GetReferencedAssemblies(), reference => reference.Name is "CommunityToolkit.Mvvm" or "Sunder.Sdk.Avalonia"
            || reference.Name!.StartsWith("Avalonia", StringComparison.Ordinal));
        foreach (var assembly in new[] { worker, app })
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name == "Sunder.Package.Agent.Protocol");
            AssertImportedRoleModel(assembly, "AgentModelDescriptor");
            AssertImportedRoleModel(assembly, "AgentChatStreamEvent");
            Assert.Same(assembly, assembly.GetType(ImportedServices + "IChatProviderService", true)!.Assembly);
            Assert.Null(assembly.GetType("Sunder.Package.Agent.Contracts.Contracts.IAgentChatProvider"));
            Assert.Null(assembly.GetType("Sunder.Package.Agent.Protocol.AgentChatStreamEvent"));
        }
        Assert.NotSame(worker.GetType(ImportedModels + "AgentModelDescriptor", true), app.GetType(ImportedModels + "AgentModelDescriptor", true));
    }

    private static async Task AssertProviderArchiveHasNoCentralProtocol(ZipArchive archive)
    {
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("/Sunder.Package.Agent.Protocol.dll", StringComparison.Ordinal));
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".deps.json", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(entry.Open());
            Assert.DoesNotContain("Sunder.Package.Agent.Protocol", await reader.ReadToEndAsync(), StringComparison.Ordinal);
        }
    }
}
