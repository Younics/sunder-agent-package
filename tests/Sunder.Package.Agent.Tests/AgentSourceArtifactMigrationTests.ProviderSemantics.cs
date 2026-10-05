using System.IO.Compression;
using System.Reflection;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static void AssertProviderSemanticOwnership(Assembly worker, Assembly app)
    {
        Assert.DoesNotContain(worker.GetReferencedAssemblies(), reference => reference.Name is "CommunityToolkit.Mvvm" or "Sunder.Sdk.Avalonia"
            || reference.Name!.StartsWith("Avalonia", StringComparison.Ordinal));
        foreach (var assembly in new[] { worker, app })
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name == "Sunder.Package.Agent.Protocol");
            Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Contracts.Models.AgentModelDescriptor", true)!.Assembly);
        }
        Assert.NotSame(worker.GetType("Sunder.Package.Agent.Contracts.Models.AgentModelDescriptor"), app.GetType("Sunder.Package.Agent.Contracts.Models.AgentModelDescriptor"));
        Assert.Same(worker, worker.GetType("Sunder.Package.Agent.Contracts.Contracts.IAgentChatProvider", true)!.Assembly);
        Assert.Same(worker, worker.GetType("Sunder.Package.Agent.Protocol.AgentChatStreamEvent", true)!.Assembly);
        Assert.Null(app.GetType("Sunder.Package.Agent.Protocol.AgentChatStreamEvent"));
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
