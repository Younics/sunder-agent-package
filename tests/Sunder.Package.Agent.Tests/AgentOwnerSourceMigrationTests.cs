using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentOwnerSourceMigrationTests
{
    internal const string AppSourcePath = "src/Sunder.Package.Agent/Rpc/Owner/OwnerRuntimeGateway.cs";
    internal const string StackSourcePath = "src/Sunder.Package.Agent/Rpc/Owner/AgentStackProviders.cs";
    internal static readonly string[] SourcePaths =
    [
        "src/Sunder.Package.Agent/Rpc/Owner/OwnerValues.cs",
        "src/Sunder.Package.Agent/Rpc/Owner/OwnerWorkspaceModels.cs",
        "src/Sunder.Package.Agent/Rpc/Owner/OwnerTranscriptModels.cs",
        "src/Sunder.Package.Agent/Rpc/Owner/OwnerCatalogModels.cs",
        "src/Sunder.Package.Agent/Rpc/Owner/OwnerPermissionModels.cs",
        "src/Sunder.Package.Agent/Rpc/Owner/OwnerCommandModels.cs",
        "src/Sunder.Package.Agent/Rpc/Owner/OwnerProjectionModels.cs",
        "src/Sunder.Package.Agent/Rpc/Owner/OwnerAttachmentModels.cs",
        "src/Sunder.Package.Agent/Rpc/Owner/OwnerHistoryModels.cs",
        "src/Sunder.Package.Agent/Rpc/Owner/OwnerValueMapper.cs",
        "src/Sunder.Package.Agent/Rpc/Owner/OwnerRuntimeService.cs",
        AppSourcePath,
        StackSourcePath,
    ];

    [AgentPublicSourceMigrationFact]
    public async Task OwnerSource_PreservesFrozenContractAndRoleIsolatedAppHandoff()
    {
        var repository = RegressionTestRpcDescriptors.RepositoryRoot;
        using var roles = new CanonicalAgentRoles();
        var baselinePath = Path.Combine(CanonicalAgentRoles.SourceRoot, "sunder-contracts.owned.json");
        var baseline = File.ReadAllBytes(baselinePath);
        using var receipt = CanonicalAgentRoles.Receipt();
        var owned = JsonNode.Parse(baseline)!["contracts"]!.AsObject();
        var entry = Assert.Single(owned, entry => entry.Value!["current"]!["exposure"]!.GetValue<string>() == "package").Value!;
        var descriptor = SunderRpcContractDescriptor.Parse(System.Text.Encoding.UTF8.GetBytes(entry["current"]!["canonicalDescriptor"]!.GetValue<string>()));
        var original = RegressionTestRpcDescriptors.Get("sunder.agent.owner.runtime");
        AgentPublicSourceMigrationTests.AssertFrozenBytes(original.GetCanonicalUtf8(), descriptor.GetCanonicalUtf8(), "Agent owner descriptor");
        Assert.Equal(original.GetCanonicalAbiProjectionUtf8(), descriptor.GetCanonicalAbiProjectionUtf8());
        Assert.Equal(original.Sha256, descriptor.Sha256);
        Assert.Equal(original.AbiSha256, descriptor.AbiSha256);
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
        Assert.Equal(20, contracts.Count);
        foreach (var entry in contracts)
        {
            var current = entry.Value!["current"]!;
            var descriptor = SunderRpcContractDescriptor.Parse(System.Text.Encoding.UTF8.GetBytes(current["canonicalDescriptor"]!.GetValue<string>()));
            var original = RegressionTestRpcDescriptors.Get(descriptor.ContractId);
            Assert.Equal(original.GetCanonicalUtf8(), descriptor.GetCanonicalUtf8());
            Assert.Equal(original.GetCanonicalAbiProjectionUtf8(), descriptor.GetCanonicalAbiProjectionUtf8());
            Assert.Equal(original.Sha256, descriptor.Sha256);
            Assert.Equal(original.AbiSha256, descriptor.AbiSha256);
            Assert.Equal("release-frozen", current["state"]!.GetValue<string>());
            if (descriptor.ContractId == owner.ContractId) Assert.Equal(owner.GetCanonicalUtf8(), descriptor.GetCanonicalUtf8());
        }
        Assert.Equal(21, receipt.GetProperty("bundles").GetArrayLength());
        var providers = receipt.GetProperty("providers").EnumerateArray().ToArray();
        Assert.Equal(11, providers.Length);
        Assert.Single(providers, provider => provider.GetProperty("audience").GetString() == "owner");
        Assert.Equal(10, providers.Count(provider => provider.GetProperty("audience").GetString() == "public"));
        AgentRuntimeUsePolicyAssertions.AssertWorkerReceipt(repository, receipt);
    }
}
