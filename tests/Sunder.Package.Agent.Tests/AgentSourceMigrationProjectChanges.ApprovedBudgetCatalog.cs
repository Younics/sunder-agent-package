using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    // Prior approved budget/catalog owner evidence: before db76583efa3418c2334439fdf212b4d745bb88e3b97189c4e1e5aa76c9e4b57f,
    // after d0ace3bf44621d95897922099cc8bb9ad9ada0dc4560cbc1060a33f347e81815.
    // These exact inverses preserve the original natural-service before-images.
    private static string BeforeApprovedBudgetCatalog(string relative, string current)
    {
        if (relative == "src/Sunder.Package.Agent/Models/Extensions/AgentPromptContextPlan.cs")
        {
            AssertProjectHash("dc3db79522b215e7d9822dd7f60d291b78ba499443d32debefd3d27276d69758", current, relative);
            const string annotation = "    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = \"0\")]\n";
            Assert.Equal(3, current.Split(annotation, StringSplitOptions.None).Length);
            current = current.Replace(annotation, "", StringComparison.Ordinal);
            AssertProjectHash("e531c809131812a0fa12f8e72ec96e45d6a94723520fa948be82aee94db15bcb", current, relative);
        }
        else if (relative == "src/Sunder.Package.Agent.Subagents/Runtime/SubagentRuntimeChangeStream.cs")
        {
            AssertProjectHash("e21a71fbf8819e91cf10264c7f0aae703d78f115c21328a64d3b612a1d710c0e", current, relative);
            const string method = "    private void PublishRuntimeCatalogChanged()\n    {\n"
                + "        // Runtime availability changes invalidate both data and catalog views. The\n"
                + "        // independent capability watcher may already have emitted its Catalog event.\n"
                + "        lock (_gate)\n        {\n"
                + "            Publish(new(0, SubagentChangeKind.ResnapshotRequired));\n"
                + "            Publish(new(0, SubagentChangeKind.Catalog));\n        }\n    }\n\n";
            Assert.Equal(2, current.Split(method, StringSplitOptions.None).Length);
            current = current.Replace(method, "", StringComparison.Ordinal);
            const string invocation = "PublishRuntimeCatalogChanged();";
            Assert.Equal(3, current.Split(invocation, StringSplitOptions.None).Length);
            current = current.Replace(invocation, "Publish(new(0, SubagentChangeKind.ResnapshotRequired));", StringComparison.Ordinal);
            AssertProjectHash("1a5d541303f9a4f2d7a3588d7100f53f9720c2851a178fb5829630afa9f73006", current, relative);
        }
        return current;
    }
}
