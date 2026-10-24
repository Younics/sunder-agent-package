using System.Text.Json;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static class CurrentPackagePolicyAssertions
{
    internal static void AssertManifest(JsonElement manifest, Func<string, byte[]> readDescriptor, JsonElement inventory)
    {
        CurrentContractInventory.AssertPackageInventory(inventory);
        var package = CurrentContractInventory.Owner(Text(manifest, "id"));
        Assert.Equal(CurrentContractInventory.PackageVersion, Text(manifest, "version"));
        var uses = CurrentContractInventory.Uses(package);
        var publicIds = CurrentPublicRpcDescriptors.ContractIds;
        var owned = CurrentContractInventory.Contracts.Where(value => value.PackageId == package.PackageId).Select(value => value.ContractId);
        var expectedBundles = owned.Concat(package.Providers.Values).Concat(uses.Select(value => value.ContractId))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var bundles = manifest.GetProperty("contractBundles").EnumerateArray().ToArray();
        Assert.Equal(expectedBundles, bundles.Select(value => Text(value, "contractId")).Order(StringComparer.Ordinal));
        foreach (var bundle in bundles)
        {
            var id = Text(bundle, "contractId");
            var platform = id == "sunder.stack.contributor";
            var descriptor = platform ? SunderStackContributorRpc.Descriptor : CurrentContractRecords.Get(id);
            var authority = platform ? "sunder.core" : CurrentContractInventory.Find(id).PackageId;
            Assert.Equal(platform ? "platform" : "package", Text(bundle, "authorityKind"));
            Assert.Equal(authority, Text(bundle, "authorityId"));
            Assert.Equal(authority == package.PackageId ? "owned" : "imported", Text(bundle, "source"));
            Assert.Equal(platform || publicIds.Contains(id) ? "public" : "package", Text(bundle, "exposure"));
            Assert.Equal(descriptor.Version, Text(bundle, "version"));
            Assert.Equal(descriptor.Sha256, Text(bundle, "descriptorSha256"));
            Assert.Equal(descriptor.AbiSha256, Text(bundle, "abiSha256"));
            Assert.Equal(descriptor.GetCanonicalUtf8(), readDescriptor(Text(bundle, "descriptorPath")));
        }

        var providers = manifest.GetProperty("provides").EnumerateArray().ToArray();
        Assert.Equal(package.Providers.Keys.Order(StringComparer.Ordinal), providers.Select(value => Text(value, "providerId")).Order(StringComparer.Ordinal));
        foreach (var provider in providers)
        {
            var id = package.Providers[Text(provider, "providerId")];
            Assert.Equal(id, Text(provider, "contractId"));
            Assert.Equal("runtime", Text(provider, "role"));
            Assert.Equal(id == "sunder.stack.contributor" || publicIds.Contains(id) ? "public" : "owner", Text(provider, "audience"));
            var bundle = Assert.Single(bundles, value => Text(value, "contractId") == id);
            Assert.Equal(Text(bundle, "version"), Text(provider, "contractVersion"));
            foreach (var field in new[] { "descriptorSha256", "abiSha256" }) Assert.Equal(Text(bundle, field), Text(provider, field));
        }

        var actualUses = manifest.GetProperty("usesContracts").EnumerateArray().ToArray();
        Assert.Equal(uses.Select(value => (value.Role, value.ContractId)).Order(), actualUses.Select(value => (Text(value, "role"), Text(value, "contractId"))).Order());
        foreach (var use in actualUses)
        {
            var expected = Assert.Single(uses, value => value.Role == Text(use, "role") && value.ContractId == Text(use, "contractId"));
            var contract = CurrentContractRecords.Get(expected.ContractId);
            Assert.Equal(contract.Version, Text(use, "contractVersion"));
            Assert.Equal(contract.AbiSha256, Text(use, "abiSha256"));
            Assert.Equal(expected.Required, use.GetProperty("required").GetBoolean());
            Assert.Equal(expected.Actions, use.GetProperty("actions").EnumerateArray().Select(value => value.GetString()));
        }

        var dependencies = manifest.GetProperty("dependsOn").EnumerateArray().ToArray();
        if (package.PackageId == CurrentContractInventory.AgentId) Assert.Empty(dependencies);
        else
        {
            var dependency = Assert.Single(dependencies);
            Assert.Equal(CurrentContractInventory.AgentId, Text(dependency, "packageId"));
            Assert.Equal(">=2.0.0 <3.0.0", Text(dependency, "versionRange"));
            Assert.Equal(package.PackageId == "younics.sunder.package.maker" ? "optional" : "required", Text(dependency, "presence"));
        }
        var apiImports = Items(manifest, "packageApiImports");
        if (package.PackageApi)
        {
            var api = Assert.Single(apiImports);
            Assert.Equal(CurrentContractInventory.AgentId, Text(api, "packageId"));
            Assert.Equal(package.PackageId == "younics.sunder.package.maker" ? "optional" : "required", Text(api, "presence"));
            foreach (var field in new[] { "bundleRefs", "useRefs" })
                Assert.Equal(publicIds.Order(StringComparer.Ordinal), api.GetProperty(field).EnumerateArray().Select(value => Text(value, "contractId")).Order(StringComparer.Ordinal));
            Assert.All(api.GetProperty("useRefs").EnumerateArray(), value => Assert.Equal("runtime", Text(value, "role")));
        }
        else Assert.Empty(apiImports);
        var importedProviders = package.PackageId == CurrentContractInventory.AgentId ? []
            : package.Providers.Values.Where(publicIds.Contains).Distinct(StringComparer.Ordinal).ToArray();
        var providerImports = Items(manifest, "providerImports");
        Assert.Equal(importedProviders.Order(StringComparer.Ordinal), providerImports.Select(value => Text(value, "contractId")).Order(StringComparer.Ordinal));
        Assert.All(providerImports, value =>
        {
            Assert.Equal(CurrentContractInventory.AgentId, Text(value, "packageId"));
            Assert.Equal(package.PackageId == "younics.sunder.package.maker" ? "optional" : "required",
                value.TryGetProperty("presence", out var presence) ? presence.GetString() : "required");
        });
        var stacks = package.Providers.Where(value => value.Value == "sunder.stack.contributor").Select(value => value.Key).ToArray();
        var platformImports = Items(manifest, "platformImports");
        if (stacks.Length == 0) Assert.Empty(platformImports);
        else
        {
            var platform = Assert.Single(platformImports);
            Assert.Equal("platform", Text(platform, "authorityKind"));
            Assert.Equal("sunder.core", Text(platform, "authorityId"));
            Assert.Equal("sunder.stack.contributor", Text(platform, "contractId"));
            Assert.Equal(stacks.Order(StringComparer.Ordinal), platform.GetProperty("providerRefs").EnumerateArray().Select(value => Text(value, "providerId")).Order(StringComparer.Ordinal));
        }

        var targets = manifest.GetProperty("targets").EnumerateArray().ToArray();
        Assert.Equal(package.HasApp ? new[] { "app", "runtime" } : ["runtime"], targets.Select(value => Text(value, "role")).Order(StringComparer.Ordinal));
        Assert.All(targets, target =>
        {
            Assert.Equal(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, Text(target, "rid"));
            Assert.Equal(Text(target, "role") == "app" ? "avalonia" : "worker", Text(target, "kind"));
        });
        if (package.PackageId == "younics.ai.agent")
        {
            var runtime = Assert.Single(targets, target => Text(target, "role") == "runtime");
            Assert.Contains("rpc.call-ownership.v1", runtime.GetProperty("requiredHostCapabilities").EnumerateArray().Select(value => value.GetString()));
        }
        if (package.PackageId == "younics.sunder.package.maker")
        {
            var app = Assert.Single(targets, target => Text(target, "role") == "app");
            var runtime = Assert.Single(targets, target => Text(target, "role") == "runtime");
            // Union equality alone cannot catch an App-only observation capability
            // accidentally moving onto the independently admitted Worker target.
            foreach (var capability in new[] { "app-activation-services.v1", "package-development-log-queries.v1", "package-development-logs.v1", "package-development-reload.v1", "package-development-revisions.v1", "package-development-views.v1", "worker-execution-observations.v1" })
            {
                Assert.Contains(capability, app.GetProperty("requiredHostCapabilities").EnumerateArray().Select(value => value.GetString()));
                Assert.DoesNotContain(capability, runtime.GetProperty("requiredHostCapabilities").EnumerateArray().Select(value => value.GetString()));
            }
        }
        Assert.Equal(inventory.GetProperty("capabilities").GetProperty(package.PackageId).EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal),
            targets.SelectMany(value => value.GetProperty("requiredHostCapabilities").EnumerateArray()).Select(value => value.GetString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    private static string Text(JsonElement value, string field) => value.GetProperty(field).GetString()!;
    private static JsonElement[] Items(JsonElement value, string field) => value.TryGetProperty(field, out var items) ? items.EnumerateArray().ToArray() : [];
}
