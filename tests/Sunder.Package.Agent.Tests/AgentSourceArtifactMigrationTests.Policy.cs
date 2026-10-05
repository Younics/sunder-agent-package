using System.IO.Compression;
using System.Text.Json;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static void VerifyFamilyPolicies(string root, string repository, string core)
    {
        using var oracle = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repository, "compatibility/agent-rpc-migration-baseline.json")));
        using var stack = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(core, "eng/compatibility/stack-contributor-rpc-migration-baseline.json")));
        using var inventory = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repository, "packages.json")));
        var baseline = oracle.RootElement;
        var descriptors = baseline.GetProperty("descriptors").EnumerateArray().ToDictionary(value => Text(value, "contractId"), StringComparer.Ordinal);
        var publicIds = descriptors.Values.Where(value => Text(value, "exposure") == "public").Select(value => Text(value, "contractId")).ToArray();
        Assert.Equal(19, publicIds.Length);
        var declarations = descriptors.Values.SelectMany(value => value.GetProperty("providers").EnumerateArray())
            .Concat(baseline.GetProperty("externalContractProviders").EnumerateArray()).ToArray();
        var useDeclarations = descriptors.Values.SelectMany(value => value.GetProperty("uses").EnumerateArray())
            .Concat(baseline.GetProperty("externalContractUses").EnumerateArray()).ToArray();
        descriptors.Add(Text(stack.RootElement, "contractId"), stack.RootElement);
        var expectedPackages = baseline.GetProperty("buildTopology").GetProperty("projects").EnumerateArray()
            .Where(project => Text(project, "role") == "worker").Select(project => Text(project, "packageId")).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(15, expectedPackages.Length);
        var actualPackages = new List<string>();
        Assert.All(Directory.EnumerateFiles(root, "agent-source.sunderpkg", SearchOption.AllDirectories).Order(StringComparer.Ordinal), path =>
        {
            using var archive = ZipFile.OpenRead(path);
            using var manifestStream = archive.GetEntry("manifest/sunder-package.json")!.Open();
            using var manifest = JsonDocument.Parse(manifestStream);
            var value = manifest.RootElement;
            var packageId = Text(value, "id");
            actualPackages.Add(packageId);
            Assert.Contains(packageId, expectedPackages);
            Assert.Equal(Text(inventory.RootElement, "releaseVersion"), Text(value, "version"));
            var providers = declarations.Where(item => Text(item, "ownerPackageId") == packageId).ToArray();
            var uses = useDeclarations.Where(item => Text(item, "ownerPackageId") == packageId).ToArray();
            var packageApi = packageId != "sunder.package.agent" && uses.Any(item => Text(item.GetProperty("metadata"), "Role") == "runtime");
            VerifyArtifactUses(value, uses, descriptors, packageApi ? publicIds : []);
            VerifyArtifactProviders(value, providers);
            VerifyArtifactImports(value, providers, publicIds, packageApi);

            var expectedBundles = providers.Select(item => Text(item.GetProperty("metadata"), "ContractId"))
                .Concat(uses.Select(item => Text(item, "include")))
                .Concat(packageApi || packageId == "sunder.package.agent" ? publicIds : []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            var bundles = value.GetProperty("contractBundles").EnumerateArray().ToArray();
            Assert.Equal(expectedBundles, bundles.Select(bundle => Text(bundle, "contractId")).Order(StringComparer.Ordinal));
            foreach (var bundle in bundles)
            {
                var id = Text(bundle, "contractId");
                var original = descriptors[id];
                Assert.Equal(Text(original, "version"), Text(bundle, "version"));
                foreach (var name in new[] { "descriptorSha256", "abiSha256", "exposure" }) Assert.Equal(Text(original, name), Text(bundle, name));
                var authority = id == "sunder.stack.contributor" ? "sunder.core" : Text(original, "exposure") == "public" ? "sunder.package.agent" : packageId;
                Assert.Equal(authority, Text(bundle, "authorityId"));
                Assert.Equal(id == "sunder.stack.contributor" ? "platform" : "package", Text(bundle, "authorityKind"));
                Assert.Equal(authority == packageId ? "owned" : "imported", Text(bundle, "source"));
                var entry = archive.GetEntry("payload/shared/" + Text(bundle, "descriptorPath"));
                Assert.NotNull(entry);
                using var descriptorStream = entry.Open();
                using var content = new MemoryStream();
                descriptorStream.CopyTo(content);
                var descriptor = SunderRpcContractDescriptor.Parse(content.ToArray());
                Assert.Equal(Convert.FromBase64String(Text(original, "canonicalDescriptorBase64")), descriptor.GetCanonicalUtf8());
            }
            var capabilities = value.GetProperty("targets").EnumerateArray()
                .SelectMany(target => target.GetProperty("requiredHostCapabilities").EnumerateArray()).Select(item => item.GetString()!)
                .ToHashSet(StringComparer.Ordinal);
            var expectedCapabilities = inventory.RootElement.GetProperty("capabilities").GetProperty(packageId).EnumerateArray()
                .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
            Assert.True(expectedCapabilities.SetEquals(capabilities),
                $"{packageId}: missing capabilities [{string.Join(", ", expectedCapabilities.Except(capabilities).Order(StringComparer.Ordinal))}]; "
                + $"unexpected capabilities [{string.Join(", ", capabilities.Except(expectedCapabilities).Order(StringComparer.Ordinal))}].");
        });
        Assert.Equal(expectedPackages, actualPackages.Order(StringComparer.Ordinal));
    }

    private static void VerifyArtifactUses(JsonElement manifest, JsonElement[] declarations, IReadOnlyDictionary<string, JsonElement> descriptors, string[] importedIds)
    {
        var expected = declarations.ToDictionary(item => (Text(item.GetProperty("metadata"), "Role"), Text(item, "include")), item =>
        {
            var metadata = item.GetProperty("metadata");
            return new ArtifactUsePolicy(Text(metadata, "ContractVersion"), bool.Parse(Text(metadata, "Required")), Text(metadata, "Actions").Split(';'));
        });
        // Package-wide API selection intentionally adds optional Runtime uses for
        // the rest of the public inventory. It must not weaken reviewed uses or
        // create App/platform grants or required-provider predicates by inference.
        foreach (var id in importedIds)
        {
            var methods = descriptors[id].GetProperty("services").EnumerateArray().SelectMany(service => service.GetProperty("methods").EnumerateArray()).ToArray();
            var actions = new List<string> { "discover" };
            if (methods.Any(method => Text(method, "kind") == "unary")) actions.Add("invoke");
            if (methods.Any(method => Text(method, "kind") == "server-stream")) actions.Add("subscribe");
            expected.TryAdd(("runtime", id), new(Text(descriptors[id], "version"), false, actions.ToArray()));
        }
        var actual = manifest.GetProperty("usesContracts").EnumerateArray().ToArray();
        Assert.Equal(expected.Keys.Order(), actual.Select(use => (Text(use, "role"), Text(use, "contractId"))).Order());
        foreach (var use in actual)
        {
            var id = Text(use, "contractId");
            var policy = expected[(Text(use, "role"), id)];
            Assert.Equal(policy.Version, Text(use, "contractVersion"));
            Assert.Equal(Text(descriptors[id], "abiSha256"), Text(use, "abiSha256"));
            Assert.True(policy.Required == use.GetProperty("required").GetBoolean(),
                $"{Text(manifest, "id")}: {Text(use, "role")} use '{id}' must retain required={policy.Required}.");
            Assert.Equal(policy.Actions, use.GetProperty("actions").EnumerateArray().Select(action => action.GetString()));
        }
    }

    private static void VerifyArtifactProviders(JsonElement manifest, JsonElement[] declarations)
    {
        var actual = manifest.GetProperty("provides").EnumerateArray().ToArray();
        Assert.Equal(declarations.Select(item => Text(item, "include")).Order(StringComparer.Ordinal), actual.Select(item => Text(item, "providerId")).Order(StringComparer.Ordinal));
        foreach (var provider in actual)
        {
            var metadata = Assert.Single(declarations, item => Text(item, "include") == Text(provider, "providerId")).GetProperty("metadata");
            foreach (var (authored, generated) in new[] { ("ContractId", "contractId"), ("ContractVersion", "contractVersion"), ("Role", "role"), ("Audience", "audience") })
                Assert.Equal(Text(metadata, authored), Text(provider, generated));
            var bundle = Assert.Single(manifest.GetProperty("contractBundles").EnumerateArray(), item => Text(item, "contractId") == Text(provider, "contractId"));
            foreach (var name in new[] { "descriptorSha256", "abiSha256" }) Assert.Equal(Text(bundle, name), Text(provider, name));
        }
    }

    private static void VerifyArtifactImports(JsonElement manifest, JsonElement[] providers, string[] publicIds, bool packageApi)
    {
        var owner = Text(manifest, "id") == "sunder.package.agent";
        var dependencies = manifest.GetProperty("dependsOn").EnumerateArray().ToArray();
        if (owner) Assert.Empty(dependencies);
        else
        {
            var dependency = Assert.Single(dependencies);
            Assert.Equal("sunder.package.agent", Text(dependency, "packageId"));
            Assert.Equal(">=2.0.0 <3.0.0", Text(dependency, "versionRange"));
            Assert.Equal("required", Text(dependency, "presence"));
        }
        var apiImports = Items(manifest, "packageApiImports");
        if (packageApi)
        {
            var api = Assert.Single(apiImports);
            Assert.Equal("sunder.package.agent", Text(api, "packageId"));
            Assert.Equal("required", Text(api, "presence"));
            foreach (var references in new[] { "bundleRefs", "useRefs" })
                Assert.Equal(publicIds.Order(StringComparer.Ordinal), api.GetProperty(references).EnumerateArray().Select(item => Text(item, "contractId")).Order(StringComparer.Ordinal));
            Assert.All(api.GetProperty("useRefs").EnumerateArray(), use => Assert.Equal("runtime", Text(use, "role")));
        }
        else Assert.Empty(apiImports);
        var expectedProviderImports = owner ? [] : providers.Select(item => Text(item.GetProperty("metadata"), "ContractId"))
            .Where(id => publicIds.Contains(id, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        var providerImports = Items(manifest, "providerImports");
        Assert.Equal(expectedProviderImports.Order(StringComparer.Ordinal), providerImports.Select(item => Text(item, "contractId")).Order(StringComparer.Ordinal));
        Assert.All(providerImports, item => Assert.Equal("sunder.package.agent", Text(item, "packageId")));
        var stacks = providers.Where(item => Text(item.GetProperty("metadata"), "ContractId") == "sunder.stack.contributor").ToArray();
        var platformImports = Items(manifest, "platformImports");
        if (stacks.Length == 0) Assert.Empty(platformImports);
        else
        {
            var platform = Assert.Single(platformImports);
            Assert.Equal("platform", Text(platform, "authorityKind"));
            Assert.Equal("sunder.core", Text(platform, "authorityId"));
            Assert.Equal("sunder.stack.contributor", Text(platform, "contractId"));
            Assert.Equal(stacks.Select(item => Text(item, "include")).Order(StringComparer.Ordinal),
                platform.GetProperty("providerRefs").EnumerateArray().Select(item => Text(item, "providerId")).Order(StringComparer.Ordinal));
        }
    }

    private static string Text(JsonElement value, string property) => value.GetProperty(property).GetString()!;
    private static JsonElement[] Items(JsonElement value, string property) => value.TryGetProperty(property, out var items) ? items.EnumerateArray().ToArray() : [];
    private sealed record ArtifactUsePolicy(string Version, bool Required, string[] Actions);
}
