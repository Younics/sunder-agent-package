using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeFilesBindings(string path, string text)
    {
        if (!path.Replace('\\', '/').Contains("/src/Sunder.Package.Agent.Tools.Files/", StringComparison.Ordinal)) return text;
        var name = Path.GetFileName(path);
        if (name is "FilesToolSource.cs" or "FilePermissionPlanner.cs")
        {
            const string validation = "FileSearchRequestValidation.TryValidateRequest";
            Assert.Equal(3, text.Split(validation, StringSplitOptions.None).Length);
            text = text.Replace(validation, "HostSecureFileSearch.TryValidateRequest", StringComparison.Ordinal);
        }
        var facets = name switch
        {
            "FilesToolSource.cs" => 5, "FileToolInfrastructure.cs" or "FilePermissionPlanner.cs" or "FilePatchHandler.cs" or "FileReadHandler.cs" => 1,
            "FileSearchHandler.cs" => 2, "ScopedInstructionContextService.cs" => 4, _ => 0,
        };
        if (facets > 0)
        {
            Assert.Equal(facets + 1, text.Split("FilesExecutionFacets.SupportsFacet", StringSplitOptions.None).Length);
            text = text.Replace("FilesExecutionFacets.SupportsFacet", "AgentExecutionTargetRpc.SupportsFacet", StringComparison.Ordinal);
        }
        if (name != "FilesToolSource.cs") return text;
        const string start = "    private static async ValueTask<FileExecutionLease?> AcquireTargetAsync(";
        const string end = "    private static bool IsMutation(string toolId)";
        Assert.Equal(2, text.Split(start, StringSplitOptions.None).Length); Assert.Equal(2, text.Split(end, StringSplitOptions.None).Length);
        var transport = File.ReadAllText(Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "tests/Sunder.Package.Agent.Tests/Fixtures/FilesToolSource.transport-before-source.cs.txt")).Replace("\r\n", "\n", StringComparison.Ordinal);
        text = text[..text.IndexOf(start, StringComparison.Ordinal)] + transport.TrimEnd() + "\n\n" + text[text.IndexOf(end, StringComparison.Ordinal)..];
        var replacements = new (string After, string Before, int Count)[]
        {
            ("public sealed partial class FilesToolSource", "public sealed class FilesToolSource\n"
                + "    : IAgentToolSource, IAgentPermissionAwareToolSource, IAgentPermissionSurface, IAgentPromptContextContributor,\n"
                + "        IAgentToolPresentationResolver, IAgentToolExecutionPreflightSource, IAgentSessionDataCleaner,\n"
                + "        IAgentPromptContextAcknowledgmentSink", 1),
            ("    internal async ValueTask<", "    public async ValueTask<", 5),
            ("        FileExecutionReference? selectedTarget,\n", "", 5),
            ("                selectedTarget,\n                context.ExecutionBinding,", "                context.ExecutionTargetReference,\n                context.ExecutionBinding,", 4),
            ("                selectedTarget,\n                request.ExecutionBinding,", "                request.ExecutionTargetReference,\n                request.ExecutionBinding,", 1),
            ("PreflightExecutionAsync(context, request, selectedTarget, invocationToken)", "PreflightExecutionAsync(context, request, invocationToken)", 1),
        };
        foreach (var (after, before, count) in replacements)
        {
            Assert.Equal(count + 1, text.Split(after, StringSplitOptions.None).Length);
            text = text.Replace(after, before, StringComparison.Ordinal);
        }
        return text;
    }
}
