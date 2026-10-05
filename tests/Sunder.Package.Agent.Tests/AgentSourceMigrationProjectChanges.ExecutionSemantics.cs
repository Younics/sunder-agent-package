using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal const string ExecutionFacetSource = "src/Sunder.Package.Agent.Shared/Execution/AgentExecutionFacetIds.cs";

    internal static string BeforeExecutionFacetSeparation(string path, string text)
    {
        if (!path.Replace('\\', '/').EndsWith("/src/Sunder.Package.Agent.Contracts/Rpc/AgentExecutionTargetRpcBinding.cs", StringComparison.Ordinal)) return text;
        var source = File.ReadAllText(Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, ExecutionFacetSource)).Replace("\r\n", "\n", StringComparison.Ordinal);
        const string shared = "[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]\n";
        Assert.Equal(2, source.Split(shared, StringSplitOptions.None).Length);
        source = source.Replace(shared, "", StringComparison.Ordinal);
        const string header = "namespace Sunder.Package.Agent.Protocol;\n\n";
        Assert.StartsWith(header, source, StringComparison.Ordinal);
        Assert.DoesNotContain("public static class AgentExecutionFacetIds", text, StringComparison.Ordinal);
        const string target = "public static class AgentExecutionTargetRpc";
        Assert.Equal(2, text.Split(target, StringSplitOptions.None).Length);
        return text.Replace(target, source[header.Length..] + "\n" + target, StringComparison.Ordinal);
    }
}
