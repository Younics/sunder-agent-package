using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    // Shared owner/import preparation happens once. Each selected canonical root
    // is built once; combined family assertions consume those exact outputs.
    private static async Task VerifySelectedPackageArtifacts(
        string root, string core, string driver, string archive, string cache,
        IReadOnlySet<string> selected)
    {
        var scenarios = new (string PackageId, Func<Task> Verify)[]
        {
            ("younics.ai.agent.tools.web", () => VerifyWebArtifact(root, core, driver, archive, cache)),
            ("younics.ai.agent.execution.local", () => VerifyLocalArtifact(root, core, driver, archive, cache)),
            ("younics.ai.agent.tools.files", () => VerifyFilesArtifact(root, core, driver, archive, cache)),
            ("younics.ai.agent.tools.shell", () => VerifyShellArtifact(root, core, driver, archive, cache)),
            ("younics.sunder.package.maker", () => VerifyBuilderArtifact(root, core, driver, archive, cache)),
            ("younics.ai.agent.subagents", () => VerifySubagentsArtifact(root, core, driver, archive, cache)),
            ("younics.ai.agent.skills", () => VerifySkillsArtifact(root, core, driver, archive, cache)),
            ("younics.ai.agent.memory.semantic", () => VerifyMemoryWorkerArtifact(root, core, driver, cache)),
            ("younics.ai.agent.mcp", () => VerifyMcpArtifact(root, core, driver, archive, cache)),
            ("younics.ai.agent.provider.openai", () => VerifyOpenAiArtifact(root, core, driver, archive, cache)),
            ("younics.ai.agent.execution.docker", () => VerifyDockerArtifact(root, core, driver, archive, cache)),
            ("younics.ai.agent.provider.gemini", () => VerifyGeminiArtifact(root, core, driver, archive, cache)),
            ("younics.ai.agent.provider.anthropic", () => VerifyAnthropicArtifact(root, core, driver, archive, cache)),
            ("younics.ai.agent.provider.lmstudio", () => VerifyLMStudioArtifact(root, core, driver, archive, cache)),
        };
        Assert.Equal(CurrentContractInventory.Packages.Select(package => package.PackageId).Order(StringComparer.Ordinal),
            scenarios.Select(scenario => scenario.PackageId).Append("younics.ai.agent").Order(StringComparer.Ordinal));
        Assert.Subset(scenarios.Select(scenario => scenario.PackageId).Append("younics.ai.agent").ToHashSet(StringComparer.Ordinal),
            selected.ToHashSet(StringComparer.Ordinal));
        var required = selected.ToHashSet(StringComparer.Ordinal);
        // Files and Shell behavior probes use the real Local execution boundary.
        if (required.Overlaps(["younics.ai.agent.tools.files", "younics.ai.agent.tools.shell"]))
            required.Add("younics.ai.agent.execution.local");
        foreach (var scenario in scenarios)
            if (required.Contains(scenario.PackageId)) await scenario.Verify();
    }
}
