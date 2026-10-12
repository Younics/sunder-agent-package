using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    // Parent-qualified 114 product handoff: authenticate the precise source-only
    // conversion fix before applying the unchanged historical migration inverses.
    private static string BeforeApprovedUnnamedProviderContent(string relative, string current)
    {
        if (relative != "src/Sunder.Package.Agent.Provider.Shared/ProviderChatValues.cs") return current;
        AssertProjectHash("82f027d5100318c1ac28e146b94b034e3e36df6a694d6a8b8c111cc90adf6434", current,
            relative + " (qualified unnamed binary conversion)");
        const string replacement = "Content = string.IsNullOrWhiteSpace(data.Name)\n"
            + "                    ? SunderContent.FromBytes(data.Data, data.MediaType)\n"
            + "                    : SunderContent.FromBytes(data.Data, data.MediaType, data.Name),";
        Assert.Equal(2, current.Split(replacement, StringSplitOptions.None).Length);
        var before = current.Replace(replacement,
            "Content = SunderContent.FromBytes(data.Data, data.MediaType, data.Name ?? string.Empty),",
            StringComparison.Ordinal);
        AssertProjectHash("81e1462f136f40c6a601e99fc0183132b5a717e54d9c2999df92f955f716d8c2", before,
            relative + " (preceding qualified source)");
        return before;
    }
}
