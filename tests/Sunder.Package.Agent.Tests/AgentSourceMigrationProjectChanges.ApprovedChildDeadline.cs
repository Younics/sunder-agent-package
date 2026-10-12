using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    // Owner guard86 ee143e15cb7befad7c8d537c01845844be373566239ecc760912e4a1d3342f54:
    // policy-only insertion, with no descriptor/ABI/version or historical capture change.
    private static string BeforeApprovedChildDeadline(string relative, string current)
    {
        current = BeforeApprovedSubagentCancellation(relative, current);
        if (relative != "src/Sunder.Package.Agent/Rpc/Public/ChildRunExecutor.cs") return current;
        AssertProjectHash("19a57356e5439ec06cb8ba45cb3261d3c4de0cc8537cd64d46ddc18b3cc866fa", current, relative);
        const string annotation = "    [SunderMethod(TimeoutSeconds = 2100)]\n";
        Assert.Equal(2, current.Split(annotation, StringSplitOptions.None).Length);
        var before = current.Replace(annotation, "", StringComparison.Ordinal);
        AssertProjectHash("d37cd0e7585d6355fb0831d019c42c846cd854c530815c190207fe5936262f76", before, relative);
        return before;
    }
}
