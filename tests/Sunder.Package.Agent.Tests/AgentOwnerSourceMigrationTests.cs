using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentOwnerSourceMigrationTests
{
    internal const string AppSourcePath = "src/Sunder.Package.Agent/Runtime/AgentOwnerResources.cs";
    internal const string StackSourcePath = "src/Sunder.Package.Agent/Rpc/Owner/AgentStackProviders.cs";
    internal static readonly string[] SourcePaths =
    [
        "src/Sunder.Package.Agent/Runtime/AgentOwnerRuntimeProvider.cs",
        "src/Sunder.Package.Agent/Runtime/AgentRuntimeContracts.cs",
        "src/Sunder.Package.Agent/Runtime/IAgentOwnerResources.cs",
        "src/Sunder.Package.Agent/Services/AgentProfileService.cs",
        AppSourcePath,
        StackSourcePath,
    ];

    [AgentPublicSourceMigrationFact]
    public async Task OwnerSource_PreservesPublicBytesAndArchivedPrivateRecordWithCurrentRoleHandoff()
    {
        var repository = RegressionTestRpcDescriptors.RepositoryRoot;
        using var roles = new CanonicalAgentRoles();
        var baselinePath = Path.Combine(CanonicalAgentRoles.SourceRoot, "sunder-contracts.owned.json");
        var baseline = File.ReadAllBytes(baselinePath);
        using var receipt = CanonicalAgentRoles.Receipt();
        var document = JsonNode.Parse(baseline)!;
        var owned = document["contracts"]!.AsObject();
        var entry = Assert.Single(document["retiredPrivateContracts"]!.AsObject()).Value!;
        var historical = SunderRpcContractDescriptor.Parse(System.Text.Encoding.UTF8.GetBytes(entry["current"]!["canonicalDescriptor"]!.GetValue<string>()));
        var descriptor = CurrentPrivateRpcDescriptors.Get("sunder.agent.owner.runtime");
        var original = RegressionTestRpcDescriptors.Get("sunder.agent.owner.runtime");
        AgentPublicSourceMigrationTests.AssertFrozenBytes(original.GetCanonicalUtf8(), historical.GetCanonicalUtf8(), "Archived Agent owner descriptor");
        Assert.Equal(original.GetCanonicalAbiProjectionUtf8(), historical.GetCanonicalAbiProjectionUtf8());
        Assert.Equal("2.0.0", descriptor.Version);
        var provider = Assert.Single(receipt.RootElement.GetProperty("providers").EnumerateArray(), provider => provider.GetProperty("providerId").GetString() == "agent.owner.runtime");
        Assert.Equal("owner", provider.GetProperty("audience").GetString());
        VerifyOwnedContractsTogether(repository, owned, receipt.RootElement, descriptor);
        VerifyAppReceipt(repository);
        await VerifyBehavior(roles.Worker, roles.App, descriptor);
        await VerifyStackBehavior(roles.Worker);
        await VerifyDeferredComposition(roles.Worker, failActivation: false);
        await VerifyDeferredComposition(roles.Worker, failActivation: true);
        await VerifyStackEvidence(CanonicalAgentRoles.SourceRoot, receipt.RootElement);
        Assert.Equal(baseline, File.ReadAllBytes(baselinePath));
    }

    private static void VerifyOwnedContractsTogether(string repository, JsonObject contracts, JsonElement receipt, SunderRpcContractDescriptor owner)
    {
        Assert.Equal(19, contracts.Count);
        foreach (var entry in contracts)
        {
            var current = entry.Value!["current"]!;
            var descriptor = SunderRpcContractDescriptor.Parse(System.Text.Encoding.UTF8.GetBytes(current["canonicalDescriptor"]!.GetValue<string>()));
            var reviewed = CurrentPrivateRpcDescriptors.Get(descriptor.ContractId);
            Assert.Equal(reviewed.GetCanonicalUtf8(), descriptor.GetCanonicalUtf8());
            Assert.Equal(reviewed.GetCanonicalAbiProjectionUtf8(), descriptor.GetCanonicalAbiProjectionUtf8());
            Assert.Equal(reviewed.Sha256, descriptor.Sha256);
            Assert.Equal(reviewed.AbiSha256, descriptor.AbiSha256);
            Assert.Equal("release-frozen", current["state"]!.GetValue<string>());
            if (descriptor.ContractId == owner.ContractId) Assert.Equal(owner.GetCanonicalUtf8(), descriptor.GetCanonicalUtf8());
        }
        Assert.Equal(22, receipt.GetProperty("bundles").GetArrayLength());
        var providers = receipt.GetProperty("providers").EnumerateArray().ToArray();
        Assert.Equal(12, providers.Length);
        Assert.Equal(2, providers.Count(provider => provider.GetProperty("audience").GetString() == "owner"));
        Assert.Equal(10, providers.Count(provider => provider.GetProperty("audience").GetString() == "public"));
        AgentRuntimeUsePolicyAssertions.AssertWorkerReceipt(repository, receipt);
    }
}
