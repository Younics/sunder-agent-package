using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeTranscriptPresentationSeparation(string path, string text)
    {
        var normalized = path.Replace('\\', '/');
        const string owner = "src/Sunder.Package.Agent/Services/AgentToolPresentationService.cs";
        const string marker = "internal sealed class RpcToolSourcePresentationResolver(";
        if (normalized.EndsWith("/" + owner, StringComparison.Ordinal))
        {
            Assert.Equal(2, text.Split(marker, StringSplitOptions.None).Length);
            return text[..text.IndexOf(marker, StringComparison.Ordinal)].TrimEnd() + "\n";
        }
        if (normalized.EndsWith("/src/Sunder.Package.Agent.Shared/PackageViews/TranscriptToolPresentationService.cs", StringComparison.Ordinal))
        {
            var source = File.ReadAllText(Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, owner)).Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.Equal(2, source.Split(marker, StringSplitOptions.None).Length);
            Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
            const string before = "using Sunder.Package.Agent.Shared.Presentation;\n";
            Assert.Equal(2, text.Split(before, StringSplitOptions.None).Length);
            return text.Replace(before, "using Sunder.Package.Agent.Protocol;\n" + before, StringComparison.Ordinal).TrimEnd()
                + "\n\n" + source[source.IndexOf(marker, StringComparison.Ordinal)..];
        }
        if (normalized.EndsWith("/src/Sunder.Package.Agent.Subagents/PackageViews/SubagentsViewModel.cs", StringComparison.Ordinal))
        {
            const string before = "using Sunder.Package.Agent.Contracts.Models;\n";
            Assert.DoesNotContain("using Sunder.Package.Agent.Protocol;", text, StringComparison.Ordinal);
            Assert.Equal(2, text.Split(before, StringSplitOptions.None).Length);
            return text.Replace(before, before + "using Sunder.Package.Agent.Protocol;\n", StringComparison.Ordinal);
        }
        return text;
    }
}
