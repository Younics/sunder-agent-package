using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static class AgentRuntimeUsePolicyAssertions
{
    internal static void AssertWorkerReceipt(string repository, JsonElement receipt)
    {
        var project = AgentSourceMigrationProjectChanges.ReadMsBuildHistoryDocument(repository, Path.Combine(repository, "Directory.Build.targets"));
        var group = project.Root!.Elements("ItemGroup").Single(value => (string?)value.Attribute("Condition")
            == "'$(SunderPackageFamilyId)' == 'sunder.package.agent' and '$(SunderPackageRole)' == 'worker'");
        var authored = group.Elements("SunderUsesContract").ToArray();
        var uses = receipt.GetProperty("uses").EnumerateArray().ToArray();
        var contracts = receipt.GetProperty("contracts").EnumerateArray().ToDictionary(value => value.GetProperty("contractKey").GetString()!);
        Assert.Equal(16, authored.Length);
        Assert.Equal(authored.Length, uses.Length);
        Assert.Equal(new[] { "sunder.agent.behavior.loop", "sunder.agent.run.control", "sunder.agent.runtime.catalog" },
            uses.Where(use => use.GetProperty("required").GetBoolean())
                .Select(use => contracts[use.GetProperty("contractKey").GetString()!].GetProperty("contractId").GetString()).Order(StringComparer.Ordinal));
        foreach (var use in uses)
        {
            Assert.Equal("runtime", use.GetProperty("role").GetString());
            var contract = contracts[use.GetProperty("contractKey").GetString()!];
            var declaration = Assert.Single(authored, item => item.Attribute("Include")!.Value == contract.GetProperty("contractId").GetString());
            // The preserved declaration supplies the reviewed permission policy;
            // current public identity is qualified by the owning successor record.
            Assert.Equal(CurrentPublicRpcDescriptors.Get(contract.GetProperty("contractId").GetString()!).Version,
                contract.GetProperty("contractVersion").GetString());
            Assert.Equal(bool.Parse(declaration.Attribute("Required")!.Value), use.GetProperty("required").GetBoolean());
            Assert.Equal(declaration.Attribute("Actions")!.Value.Split(';'), use.GetProperty("actions").EnumerateArray().Select(action => action.GetString()));
            var policy = contract.GetProperty("runtimeUse");
            Assert.Equal(use.GetProperty("required").GetBoolean(), policy.GetProperty("required").GetBoolean());
            Assert.True(JsonElement.DeepEquals(use.GetProperty("actions"), policy.GetProperty("actions")));
        }
        Assert.Equal(16, contracts.Values.Count(contract => contract.TryGetProperty("runtimeUse", out _)));
    }
}
