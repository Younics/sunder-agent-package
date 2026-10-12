using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

// Reviewed current-public expectations do not replace the immutable migration oracle.
// The tuple file is populated from the owning Accept/Freeze checkpoint, not a test run.
internal static class CurrentPublicRpcDescriptors
{
    internal const string EvidencePath = "compatibility/agent-service-public-successors.json";
    private const string PackageId = "sunder.package.agent";
    private const string SourceDirectory = "src/Sunder.Package.Agent";
    private static readonly Lazy<IReadOnlySet<string>> HistoricalContractIds = new(ReadHistoricalContractIds);
    private static readonly Lazy<IReadOnlyDictionary<string, JsonElement>> Successors = new(ReadSuccessors);
    private static readonly Lazy<IReadOnlyDictionary<string, SunderRpcContractDescriptor>> Descriptors = new(ReadDescriptors);

    internal static bool IsAgentPublicContract(string contractId) => HistoricalContractIds.Value.Contains(contractId);

    internal static SunderRpcContractDescriptor Get(string contractId)
        => Descriptors.Value.TryGetValue(contractId, out var descriptor) ? descriptor
            : throw new InvalidOperationException($"No reviewed current Agent public contract '{contractId}'.");

    internal static void AssertCurrent(SunderRpcContractDescriptor descriptor)
    {
        var expected = Successors.Value[descriptor.ContractId];
        Assert.Equal(expected.GetProperty("version").GetString(), descriptor.Version);
        Assert.Equal(expected.GetProperty("descriptorSha256").GetString(), descriptor.Sha256);
        Assert.Equal(expected.GetProperty("abiSha256").GetString(), descriptor.AbiSha256);
        var methods = expected.GetProperty("methods");
        Assert.Equal(methods.EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal),
            descriptor.Services.Select(value => value.ServiceId).Order(StringComparer.Ordinal));
        foreach (var service in descriptor.Services)
            Assert.Equal(methods.GetProperty(service.ServiceId).EnumerateArray().Select(value => value.GetString()),
                service.Methods.Select(value => value.MethodId).Order(StringComparer.Ordinal));
    }

    private static IReadOnlyDictionary<string, SunderRpcContractDescriptor> ReadDescriptors()
    {
        var root = RegressionTestRpcDescriptors.RepositoryRoot;
        using var owned = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, SourceDirectory, "sunder-contracts.owned.json")));
        Assert.Equal(1, owned.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("package", owned.RootElement.GetProperty("authorityKind").GetString());
        Assert.Equal(PackageId, owned.RootElement.GetProperty("authorityId").GetString());
        Assert.Equal(PackageId, owned.RootElement.GetProperty("packageId").GetString());
        var baseline = owned.RootElement.GetProperty("contracts").EnumerateObject()
            .Where(value => value.Value.GetProperty("current").GetProperty("exposure").GetString() == "public")
            .ToDictionary(value => value.Value.GetProperty("contractId").GetString()!,
                value => value.Value.GetProperty("current"), StringComparer.Ordinal);
        Assert.Equal(Successors.Value.Keys.Order(StringComparer.Ordinal), baseline.Keys.Order(StringComparer.Ordinal));

        var assembly = typeof(AgentProfileRecord).Assembly;
        var receiptPath = Path.Combine(root, SourceDirectory, "obj",
            assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration,
            "net10.0/sunder-analysis", RuntimeInformation.RuntimeIdentifier, "sunder-rpc/authoring-receipt.v1.json");
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(receiptPath));
        Assert.Equal(PackageId, receipt.RootElement.GetProperty("packageId").GetString());
        var generated = receipt.RootElement.GetProperty("contracts").EnumerateArray()
            .Where(value => value.GetProperty("exposure").GetString() == "public")
            .ToDictionary(value => value.GetProperty("contractId").GetString()!, StringComparer.Ordinal);
        var result = new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal);
        foreach (var (contractId, current) in baseline)
        {
            Assert.Equal("release-frozen", current.GetProperty("state").GetString());
            var release = current.GetProperty("packageRelease");
            Assert.Equal(PackageId, release.GetProperty("packageId").GetString());
            Assert.Equal(receipt.RootElement.GetProperty("packageVersion").GetString(), release.GetProperty("packageVersion").GetString());
            var bytes = Encoding.UTF8.GetBytes(current.GetProperty("canonicalDescriptor").GetString()!);
            var descriptor = SunderRpcContractDescriptor.Parse(bytes);
            Assert.Equal(contractId, descriptor.ContractId);
            Assert.Equal(current.GetProperty("contractVersion").GetString(), descriptor.Version);
            Assert.Equal(current.GetProperty("descriptorBytes").GetInt32(), bytes.Length);
            Assert.Equal(bytes, descriptor.GetCanonicalUtf8());
            Assert.Equal(current.GetProperty("descriptorSha256").GetString(), descriptor.Sha256);
            var abi = Encoding.UTF8.GetBytes(current.GetProperty("canonicalAbiProjection").GetString()!);
            Assert.Equal(current.GetProperty("abiProjectionBytes").GetInt32(), abi.Length);
            Assert.Equal(abi, descriptor.GetCanonicalAbiProjectionUtf8());
            Assert.Equal(current.GetProperty("abiSha256").GetString(), descriptor.AbiSha256);
            AssertCurrent(descriptor);

            Assert.True(generated.TryGetValue(contractId, out var contract), $"The current receipt is missing '{contractId}'.");
            var emitted = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(contract.GetProperty("canonicalDescriptor").GetString()!));
            AssertCurrent(emitted);
            Assert.Equal(bytes, emitted.GetCanonicalUtf8());
            result.Add(contractId, descriptor);
        }
        return result;
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadSuccessors()
    {
        var path = Path.Combine(RegressionTestRpcDescriptors.RepositoryRoot, EvidencePath);
        Assert.True(File.Exists(path), $"Missing reviewed public successor tuples: {EvidencePath}. Generate them from the actual owning Accept/Freeze checkpoint.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        AssertMembers(root, "schemaVersion", "purpose", "contracts");
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("purpose").GetString()));
        var entries = root.GetProperty("contracts").EnumerateArray().ToArray();
        Assert.Equal(19, entries.Length);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            AssertMembers(entry, "packageId", "sourceDirectory", "contractId", "version", "descriptorSha256", "abiSha256", "methods");
            Assert.Equal(PackageId, entry.GetProperty("packageId").GetString());
            Assert.Equal(SourceDirectory, entry.GetProperty("sourceDirectory").GetString());
            var contractId = entry.GetProperty("contractId").GetString()!;
            Assert.Contains(contractId, HistoricalContractIds.Value);
            Assert.True(SemanticVersion.TryParse(entry.GetProperty("version").GetString()!, out _));
            foreach (var key in new[] { "descriptorSha256", "abiSha256" })
            {
                var hash = entry.GetProperty(key).GetString()!;
                Assert.Equal(64, hash.Length);
                Assert.All(hash, character => Assert.True(character is >= '0' and <= '9' or >= 'a' and <= 'f'));
            }
            var services = entry.GetProperty("methods").EnumerateObject().ToArray();
            Assert.NotEmpty(services);
            Assert.Equal(services.Length, services.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count());
            foreach (var service in services)
            {
                Assert.False(string.IsNullOrWhiteSpace(service.Name));
                var methods = service.Value.EnumerateArray().Select(value => value.GetString()!).ToArray();
                Assert.NotEmpty(methods);
                Assert.All(methods, method => Assert.False(string.IsNullOrWhiteSpace(method)));
                Assert.Equal(methods.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), methods);
            }
            Assert.True(result.TryAdd(contractId, entry.Clone()), $"Duplicate public successor '{contractId}'.");
        }
        Assert.Equal(HistoricalContractIds.Value.Order(StringComparer.Ordinal), result.Keys.Order(StringComparer.Ordinal));
        return result;
    }

    private static IReadOnlySet<string> ReadHistoricalContractIds()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RegressionTestRpcDescriptors.RepositoryRoot,
            "compatibility/agent-rpc-migration-baseline.json")));
        var identities = document.RootElement.GetProperty("descriptors").EnumerateArray()
            .Where(value => value.GetProperty("exposure").GetString() == "public")
            .Select(value => value.GetProperty("contractId").GetString()!).ToArray();
        Assert.Equal(19, identities.Length);
        Assert.Equal(19, identities.Distinct(StringComparer.Ordinal).Count());
        return identities.ToHashSet(StringComparer.Ordinal);
    }

    private static void AssertMembers(JsonElement value, params string[] expected)
        => Assert.Equal(expected.Order(StringComparer.Ordinal), value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
}
