using System.IO.Compression;
using System.Reflection;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static void AssertExecutionToolSemanticOwnership(Assembly worker)
    {
        Assert.DoesNotContain(worker.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Protocol" or "Sunder.Agent.Execution.Common");
        foreach (var type in new[] { "Sunder.Package.Agent.Contracts.Models.AgentToolExecutionContext", "Sunder.Package.Agent.Contracts.Models.AgentResourceOperationContext",
            "Sunder.Package.Agent.Contracts.Models.AgentPromptContextReceipt", "Sunder.Package.Agent.Protocol.AgentRpcProviderHandle", "Sunder.Package.Agent.Protocol.AgentExecutionFacetIds" })
            Assert.Same(worker, worker.GetType(type, true)!.Assembly);
        Assert.Null(worker.GetType("Sunder.Agent.Execution.Common.HostSecurePathEngine"));
        Assert.Null(worker.GetType("Sunder.Agent.Execution.Common.LocalGlobMatcher"));
        Assert.Null(worker.GetType("Sunder.Agent.Execution.Common.BoundedProcessRunner"));
    }

    private static async Task AssertArchiveHasNoCentralExecutionAssemblies(ZipArchive archive)
    {
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("/Sunder.Package.Agent.Protocol.dll", StringComparison.Ordinal)
            || entry.FullName.EndsWith("/Sunder.Agent.Execution.Common.dll", StringComparison.Ordinal));
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".deps.json", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(entry.Open()); var text = await reader.ReadToEndAsync();
            Assert.DoesNotContain("Sunder.Package.Agent.Protocol", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Sunder.Agent.Execution.Common", text, StringComparison.Ordinal);
        }
    }
}
