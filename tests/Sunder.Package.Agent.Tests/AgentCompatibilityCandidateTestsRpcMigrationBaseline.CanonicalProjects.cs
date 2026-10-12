using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Sunder.Sdk.Rpc;
using Xunit;
using Xunit.Sdk;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentCompatibilityCandidateTestsRpcMigrationBaseline
{
    [Fact]
    public void CentralInterfaceRelocations_ReconcileExplicitMovesWithFrozenSourceHashes()
    {
        Assert.Equal(142, AgentSourceMigrationProjectChanges.CentralSemanticRelocations.Count);
        Assert.Equal(38, AgentSourceMigrationProjectChanges.CentralInterfaceRelocations.Count);
        Assert.Equal(180, AgentSourceMigrationProjectChanges.ReviewedCentralRelocations.Select(move => move.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void CanonicalProjectReplacements_ReconcileFrozenHistoryAndGuardCurrentInputs()
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        AgentSourceMigrationProjectChanges.AssertCanonicalProjectTopology(root);
        using var baseline = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, BaselineRelativePath)));
        var projects = baseline.RootElement.GetProperty("buildTopology").GetProperty("projects").EnumerateArray().ToArray();
        Assert.Equal(57, projects.Length);
        foreach (var history in AgentSourceMigrationProjectChanges.CanonicalProjectHistories)
        {
            var original = Assert.Single(projects, project => project.GetProperty("path").GetString() == history.Project);
            var path = Path.Combine(root, history.Project);
            var reconstructed = AgentSourceMigrationProjectChanges.BeforeAdditions(root, path,
                AgentSourceMigrationProjectChanges.ReadNormalizedProjectText(path));
            Assert.Equal(original.GetProperty("sha256").GetString(), AgentSourceMigrationProjectChanges.ProjectTextHash(reconstructed));
            var document = XDocument.Parse(reconstructed);
            Assert.Equal("worker", Assert.Single(document.Descendants("SunderPackageRole")).Value);
            Assert.Equal(history.PackageId, Assert.Single(document.Descendants("SunderPackageFamilyId")).Value);
        }
        // Output tuple/policy and dependency closure is exercised by the required
        // CompleteOwnerArtifact_DerivesConsumerAndProviderLocksFromFreshSourcePayload
        // lane, whose BuildCanonicalFamily builds these same roots. Reading historical
        // projects here cannot serve as acceptance of stale archives or role outputs.
    }

    [Fact]
    public void CanonicalOwnedContractClosure_PreservesArchivedPrivateAndReviewedPublicDescriptors()
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        AgentSourceMigrationProjectChanges.AssertCanonicalProjectTopology(root);
        using var baseline = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, BaselineRelativePath)));
        var descriptors = baseline.RootElement.GetProperty("descriptors").EnumerateArray().ToArray();
        Assert.Equal(31, descriptors.Length);
        Assert.Equal(19, descriptors.Count(descriptor => descriptor.GetProperty("exposure").GetString() == "public"));
        using var successors = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "compatibility/agent-service-public-successors.json")));
        var publicSuccessors = successors.RootElement.GetProperty("contracts").EnumerateArray()
            .ToDictionary(value => value.GetProperty("contractId").GetString()!, StringComparer.Ordinal);
        Assert.Equal(descriptors.Where(value => value.GetProperty("exposure").GetString() == "public")
                .Select(value => value.GetProperty("contractId").GetString()).Order(StringComparer.Ordinal),
            publicSuccessors.Keys.Order(StringComparer.Ordinal));
        var verified = new HashSet<string>(StringComparer.Ordinal);
        foreach (var history in AgentSourceMigrationProjectChanges.CanonicalProjectHistories)
        {
            var expected = descriptors.Where(descriptor =>
            {
                var path = descriptor.GetProperty("path").GetString()!;
                return path.StartsWith("src/" + history.Name + "/", StringComparison.Ordinal)
                    || history.Family == "Agent" && path.StartsWith("src/Sunder.Package.Agent.Contracts/", StringComparison.Ordinal);
            }).ToDictionary(descriptor => descriptor.GetProperty("contractId").GetString()!, StringComparer.Ordinal);
            using var owned = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, Path.GetDirectoryName(history.Project)!, "sunder-contracts.owned.json")));
            Assert.Equal("package", owned.RootElement.GetProperty("authorityKind").GetString());
            Assert.Equal(history.PackageId, owned.RootElement.GetProperty("authorityId").GetString());
            Assert.Equal(history.PackageId, owned.RootElement.GetProperty("packageId").GetString());
            var actual = owned.RootElement.GetProperty("contracts").EnumerateObject().Select(property => property.Value)
                .Concat(owned.RootElement.TryGetProperty("retiredPrivateContracts", out var retired)
                    ? retired.EnumerateObject().Select(property => property.Value) : []).ToArray();
            Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Select(contract => contract.GetProperty("contractId").GetString()).Order(StringComparer.Ordinal));
            foreach (var contract in actual)
            {
                var id = contract.GetProperty("contractId").GetString()!;
                Assert.True(verified.Add(id), $"Multiple canonical owners claimed frozen contract {id}.");
                var original = expected[id];
                var current = contract.GetProperty("current");
                Assert.Equal("release-frozen", current.GetProperty("state").GetString());
                Assert.Equal(history.PackageId, current.GetProperty("packageRelease").GetProperty("packageId").GetString());
                if (publicSuccessors.TryGetValue(id, out var reviewed))
                {
                    Assert.Equal("public", current.GetProperty("exposure").GetString());
                    Assert.Equal(reviewed.GetProperty("version").GetString(), current.GetProperty("contractVersion").GetString());
                    foreach (var field in new[] { "descriptorSha256", "abiSha256" })
                        Assert.Equal(reviewed.GetProperty(field).GetString(), current.GetProperty(field).GetString());
                    var bytes = Encoding.UTF8.GetBytes(current.GetProperty("canonicalDescriptor").GetString()!);
                    var descriptor = SunderRpcContractDescriptor.Parse(bytes);
                    Assert.Equal(bytes, descriptor.GetCanonicalUtf8());
                    Assert.Equal(reviewed.GetProperty("descriptorSha256").GetString(), descriptor.Sha256);
                    Assert.Equal(reviewed.GetProperty("abiSha256").GetString(), descriptor.AbiSha256);
                    Assert.Equal(descriptor.GetCanonicalAbiProjectionUtf8(), Encoding.UTF8.GetBytes(current.GetProperty("canonicalAbiProjection").GetString()!));
                    var methods = reviewed.GetProperty("methods");
                    Assert.Equal(methods.EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal),
                        descriptor.Services.Select(value => value.ServiceId).Order(StringComparer.Ordinal));
                    foreach (var service in descriptor.Services)
                        Assert.Equal(methods.GetProperty(service.ServiceId).EnumerateArray().Select(value => value.GetString()),
                            service.Methods.Select(value => value.MethodId).Order(StringComparer.Ordinal));
                    continue;
                }
                Assert.Equal(original.GetProperty("version").GetString(), current.GetProperty("contractVersion").GetString());
                foreach (var field in new[] { "exposure", "descriptorSha256", "abiSha256" })
                    Assert.Equal(original.GetProperty(field).GetString(), current.GetProperty(field).GetString());
                Assert.Equal(Convert.FromBase64String(original.GetProperty("canonicalDescriptorBase64").GetString()!),
                    Encoding.UTF8.GetBytes(current.GetProperty("canonicalDescriptor").GetString()!));
                Assert.Equal(Convert.FromBase64String(original.GetProperty("canonicalAbiProjectionBase64").GetString()!),
                    Encoding.UTF8.GetBytes(current.GetProperty("canonicalAbiProjection").GetString()!));
            }
        }
        Assert.Equal(31, verified.Count);
    }

    [Theory]
    [InlineData("<ItemGroup><PackageReference Include=\"Unexpected.Dependency\" /></ItemGroup>")]
    [InlineData("<ItemGroup><Compile Remove=\"Rpc/**/*.cs\" /></ItemGroup>")]
    [InlineData("<ItemGroup><ProjectReference Include=\"OwnerProtocol/Sunder.Package.Agent.OwnerProtocol.csproj\" /></ItemGroup>")]
    [InlineData("<ItemGroup><AvaloniaResource Include=\"Unexpected.axaml\" /></ItemGroup>")]
    [InlineData("<PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>")]
    [InlineData("<Import Project=\"Unexpected.props\" />")]
    public void CanonicalProjectReplacement_RejectsCurrentDriftBeforeReturningHistory(string addition)
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        var path = Path.Combine(root, "src/Sunder.Package.Agent/Sunder.Package.Agent.csproj");
        var current = AgentSourceMigrationProjectChanges.ReadNormalizedProjectText(path);
        // Establish a valid control so unrelated repository failures cannot make the
        // negative case pass. Mutate text only; never touch the real build inputs.
        var original = AgentSourceMigrationProjectChanges.BeforeAdditions(root, path, current);
        Assert.NotEqual(current, original);
        var drifted = current.Replace("</Project>", addition + "\n</Project>", StringComparison.Ordinal);
        Assert.ThrowsAny<XunitException>(() => AgentSourceMigrationProjectChanges.BeforeAdditions(root, path, drifted));
    }
}
