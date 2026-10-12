using System.Text;
using System.Text.Json;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    // Read the actual, already-built canonical development outputs. Historical
    // reconstruction must not make current descriptor/use/provider drift invisible.
    // This check never restores, builds, packs, or modifies selected/owned evidence.
    internal static void AssertCurrentCanonicalCompatibility(string root)
    {
        AssertRetirementCurrentTopology(root);
        using var oracle = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "compatibility/agent-rpc-migration-baseline.json")));
        using var stack = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "../sunder-core/eng/compatibility/stack-contributor-rpc-migration-baseline.json")));
        using var inventory = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "packages.json")));
        var descriptors = oracle.RootElement.GetProperty("descriptors").EnumerateArray().ToDictionary(item => Value(item, "contractId"), StringComparer.Ordinal);
        var publicIds = descriptors.Values.Where(item => Value(item, "exposure") == "public").Select(item => Value(item, "contractId")).ToArray();
        var providers = descriptors.Values.SelectMany(item => item.GetProperty("providers").EnumerateArray())
            .Concat(oracle.RootElement.GetProperty("externalContractProviders").EnumerateArray()).ToArray();
        var uses = descriptors.Values.SelectMany(item => item.GetProperty("uses").EnumerateArray()).ToArray();
        Assert.Equal(31, descriptors.Count);
        Assert.Equal(19, publicIds.Length);
        var historicalDescriptors = new Dictionary<string, JsonElement>(descriptors, StringComparer.Ordinal);
        foreach (var id in publicIds)
            descriptors[id] = CurrentDescriptorSnapshot(CurrentPublicRpcDescriptors.Get(id), "public");
        using var successors = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "compatibility/agent-ordinary-private-successors.json")));
        foreach (var successor in successors.RootElement.GetProperty("contracts").EnumerateArray())
        {
            var current = CurrentPrivateRpcDescriptors.Get(Value(successor, "contractId"));
            descriptors[current.ContractId] = CurrentDescriptorSnapshot(current, "package");
        }
        providers = providers.Append(JsonSerializer.SerializeToElement(new
        {
            ownerPackageId = "sunder.package.agent", include = "sunder.package.agent.agent.profile.provider",
            metadata = new { ContractId = "sunder.package.agent.agent.profile", ContractVersion = "2.0.0", Role = "runtime", Audience = "owner" },
        })).ToArray();
        descriptors.Add(Value(stack.RootElement, "contractId"), stack.RootElement);
        var packages = AgentPackageRepositoryInventory.GetRuntimePackageProjects();
        Assert.Equal(CanonicalProjectHistories.Select(item => item.Name).Order(StringComparer.Ordinal), packages.Select(item => item.Name).Order(StringComparer.Ordinal));
        var ownedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            var packageId = package.FamilyId!;
            using var owned = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(package.DirectoryPath, "sunder-contracts.owned.json")));
            Assert.Equal(packageId, Value(owned.RootElement, "authorityId"));
            Assert.Equal(packageId, Value(owned.RootElement, "packageId"));
            Assert.Equal("package", Value(owned.RootElement, "authorityKind"));
            var expectedOwned = historicalDescriptors.Values.Where(item => item.TryGetProperty("path", out var path)
                && (path.GetString()!.StartsWith("src/" + package.Name + "/", StringComparison.Ordinal)
                    || packageId == "sunder.package.agent" && path.GetString()!.StartsWith("src/Sunder.Package.Agent.Contracts/", StringComparison.Ordinal)))
                .Select(item => Value(item, "contractId")).Order(StringComparer.Ordinal).ToArray();
            var ownedContracts = owned.RootElement.GetProperty("contracts").EnumerateObject().Select(item => item.Value)
                .Concat(owned.RootElement.TryGetProperty("retiredPrivateContracts", out var retired)
                    ? retired.EnumerateObject().Select(item => item.Value) : []).ToArray();
            Assert.Equal(expectedOwned, ownedContracts.Select(item => Value(item, "contractId")).Order(StringComparer.Ordinal));
            foreach (var contract in ownedContracts)
            {
                var id = Value(contract, "contractId");
                Assert.True(ownedIds.Add(id), $"More than one canonical owner claims {id}.");
                var current = contract.GetProperty("current");
                // Public owned records advanced through the reviewed release.
                // Retired private records still retain their exact original bytes.
                var original = publicIds.Contains(id, StringComparer.Ordinal) ? descriptors[id] : historicalDescriptors[id];
                Assert.Equal("release-frozen", Value(current, "state"));
                Assert.Equal(Value(original, "version"), Value(current, "contractVersion"));
                foreach (var field in new[] { "exposure", "descriptorSha256", "abiSha256" }) Assert.Equal(Value(original, field), Value(current, field));
                Assert.Equal(Convert.FromBase64String(Value(original, "canonicalDescriptorBase64")), Encoding.UTF8.GetBytes(Value(current, "canonicalDescriptor")));
                Assert.Equal(Convert.FromBase64String(Value(original, "canonicalAbiProjectionBase64")), Encoding.UTF8.GetBytes(Value(current, "canonicalAbiProjection")));
            }

            Assert.True(File.Exists(package.ManifestPath), $"Canonical output is required: {package.ManifestPath}");
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(package.ManifestPath));
            var actual = manifest.RootElement;
            Assert.Equal(packageId, Value(actual, "id"));
            Assert.Equal(Value(inventory.RootElement, "releaseVersion"), Value(actual, "version"));
            if (packageId == "sunder.package.agent")
            {
                var receiptPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(package.ProgramPath)!, "../../authoring-receipt.v1.json"));
                using var receipt = JsonDocument.Parse(File.ReadAllBytes(receiptPath));
                AgentRuntimeUsePolicyAssertions.AssertWorkerReceipt(root, receipt.RootElement);
            }
            var expectedProviders = providers.Where(item => Value(item, "ownerPackageId") == packageId).ToArray();
            var expectedUses = uses.Where(item => Value(item, "ownerPackageId") == packageId).ToArray();
            var packageApi = packageId is not ("sunder.package.agent" or "sunder.package.agent.tools.web")
                && expectedUses.Any(item => Value(item.GetProperty("metadata"), "Role") == "runtime");
            if (packageId == "sunder.package.agent.tools.web")
                expectedUses = [JsonSerializer.SerializeToElement(new
                {
                    include = "sunder.agent.execution.target",
                    metadata = new { Role = "runtime", Required = "false", Actions = "discover" },
                })];
            var expectedBundles = expectedProviders.Select(item => Value(item.GetProperty("metadata"), "ContractId"))
                .Concat(expectedUses.Select(item => Value(item, "include")))
                .Concat(packageApi || packageId == "sunder.package.agent" ? publicIds : []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            var bundles = actual.GetProperty("contractBundles").EnumerateArray().ToArray();
            Assert.Equal(expectedBundles, bundles.Select(item => Value(item, "contractId")).Order(StringComparer.Ordinal));
            foreach (var bundle in bundles)
            {
                var id = Value(bundle, "contractId");
                var original = descriptors[id];
                foreach (var field in new[] { "version", "descriptorSha256", "abiSha256", "exposure" }) Assert.Equal(Value(original, field), Value(bundle, field));
                var authority = id == "sunder.stack.contributor" ? "sunder.core" : Value(original, "exposure") == "public" ? "sunder.package.agent" : packageId;
                Assert.Equal(authority, Value(bundle, "authorityId"));
                Assert.Equal(id == "sunder.stack.contributor" ? "platform" : "package", Value(bundle, "authorityKind"));
                Assert.Equal(authority == packageId ? "owned" : "imported", Value(bundle, "source"));
                var content = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(package.ManifestPath)!, "../payload/shared", Value(bundle, "descriptorPath"))));
                Assert.Equal(Convert.FromBase64String(Value(original, "canonicalDescriptorBase64")), content.GetCanonicalUtf8());
            }
            var actualProviders = actual.GetProperty("provides").EnumerateArray().ToArray();
            Assert.Equal(expectedProviders.Select(item => Value(item, "include")).Order(StringComparer.Ordinal), actualProviders.Select(item => Value(item, "providerId")).Order(StringComparer.Ordinal));
            foreach (var provider in actualProviders)
            {
                var expected = Assert.Single(expectedProviders, item => Value(item, "include") == Value(provider, "providerId")).GetProperty("metadata");
                Assert.Equal(Value(descriptors[Value(provider, "contractId")], "version"), Value(provider, "contractVersion"));
                foreach (var (authored, generated) in new[] { ("ContractId", "contractId"), ("Role", "role"), ("Audience", "audience") })
                    Assert.Equal(Value(expected, authored), Value(provider, generated));
                foreach (var field in new[] { "descriptorSha256", "abiSha256" }) Assert.Equal(Value(descriptors[Value(provider, "contractId")], field), Value(provider, field));
            }
            var usePolicies = packageId == "sunder.package.agent"
                ? new Dictionary<(string Role, string ContractId), (string Version, bool Required, string[] Actions)>()
                : expectedUses.ToDictionary(item => (Value(item.GetProperty("metadata"), "Role"), Value(item, "include")), item =>
                {
                    var policy = item.GetProperty("metadata");
                    return (Version: Value(descriptors[Value(item, "include")], "version"), Required: bool.Parse(Value(policy, "Required")), Actions: Value(policy, "Actions").Split(';'));
                });
            if (packageId == "sunder.package.agent")
            {
                void AddUse(string role, string id, bool required, string[] actions)
                    => usePolicies.Add((role, id), (Value(descriptors[id], "version"), required, actions));

                foreach (var (id, required, actions) in new[]
                {
                    ("sunder.agent.child.run.executor", false, new[] { "discover", "invoke" }),
                    ("sunder.agent.owner.runtime", true, new[] { "discover", "invoke", "subscribe" }),
                    ("sunder.agent.runtime.catalog", false, new[] { "discover", "invoke", "subscribe" }),
                    ("sunder.agent.workspace.editor", false, new[] { "discover", "invoke" }),
                    ("sunder.package.agent.agent.profile", true, new[] { "discover", "invoke" }),
                })
                    AddUse("app", id, required, actions);

                foreach (var (id, required, actions) in new[]
                {
                    ("sunder.agent.behavior.loop", true, new[] { "discover", "invoke" }),
                    ("sunder.agent.chat.provider", false, new[] { "discover", "invoke", "subscribe" }),
                    ("sunder.agent.durable.lifecycle.observer", false, new[] { "discover", "invoke" }),
                    ("sunder.agent.embedding.provider", false, new[] { "discover", "invoke" }),
                    ("sunder.agent.execution.target", false, new[] { "discover", "invoke" }),
                    ("sunder.agent.permission.surface", false, new[] { "discover", "invoke" }),
                    ("sunder.agent.profile.capability.consumer", false, new[] { "discover", "invoke" }),
                    ("sunder.agent.prompt.context.contributor", false, new[] { "discover", "invoke" }),
                    ("sunder.agent.run.control", true, new[] { "discover", "invoke", "subscribe" }),
                    ("sunder.agent.runtime.catalog", true, new[] { "discover", "invoke", "subscribe" }),
                    ("sunder.agent.selectable.capability.provider", false, new[] { "discover", "invoke", "subscribe" }),
                    ("sunder.agent.session.cleaner", false, new[] { "discover", "invoke" }),
                    ("sunder.agent.system.prompt.contributor", false, new[] { "discover", "invoke" }),
                    ("sunder.agent.tool.execution.observed", false, new[] { "discover", "invoke", "subscribe" }),
                    ("sunder.agent.tool.source", false, new[] { "discover", "invoke" }),
                    ("sunder.agent.workspace.path.migrator", false, new[] { "discover", "invoke" }),
                })
                    AddUse("runtime", id, required, actions);
            }
            if (packageApi)
                foreach (var id in publicIds)
                {
                    var methods = descriptors[id].GetProperty("services").EnumerateArray().SelectMany(service => service.GetProperty("methods").EnumerateArray()).ToArray();
                    var actions = new List<string> { "discover" };
                    if (methods.Any(method => Value(method, "kind") == "unary")) actions.Add("invoke");
                    if (methods.Any(method => Value(method, "kind") == "server-stream")) actions.Add("subscribe");
                    usePolicies.TryAdd(("runtime", id), (Value(descriptors[id], "version"), false, actions.ToArray()));
                }
            var actualUses = actual.GetProperty("usesContracts").EnumerateArray().ToArray();
            Assert.Equal(usePolicies.Keys.Order(), actualUses.Select(item => (Value(item, "role"), Value(item, "contractId"))).Order());
            foreach (var use in actualUses)
            {
                var id = Value(use, "contractId");
                var policy = usePolicies[(Value(use, "role"), id)];
                Assert.Equal(policy.Version, Value(use, "contractVersion"));
                Assert.Equal(policy.Required, use.GetProperty("required").GetBoolean());
                Assert.Equal(policy.Actions, use.GetProperty("actions").EnumerateArray().Select(item => item.GetString()));
                Assert.Equal(Value(descriptors[id], "abiSha256"), Value(use, "abiSha256"));
            }
            var dependencies = actual.GetProperty("dependsOn").EnumerateArray().ToArray();
            if (packageId == "sunder.package.agent") Assert.Empty(dependencies);
            else
            {
                var dependency = Assert.Single(dependencies);
                Assert.Equal("sunder.package.agent", Value(dependency, "packageId"));
                Assert.Equal(">=2.0.0 <3.0.0", Value(dependency, "versionRange"));
                Assert.Equal("required", Value(dependency, "presence"));
            }
            var apiImports = Items(actual, "packageApiImports");
            if (packageApi)
            {
                var api = Assert.Single(apiImports);
                Assert.Equal("sunder.package.agent", Value(api, "packageId"));
                Assert.Equal("required", Value(api, "presence"));
                foreach (var references in new[] { "bundleRefs", "useRefs" })
                    Assert.Equal(publicIds.Order(StringComparer.Ordinal), api.GetProperty(references).EnumerateArray().Select(item => Value(item, "contractId")).Order(StringComparer.Ordinal));
                Assert.All(api.GetProperty("useRefs").EnumerateArray(), use => Assert.Equal("runtime", Value(use, "role")));
            }
            else Assert.Empty(apiImports);
            var expectedProviderImports = packageId == "sunder.package.agent" ? [] : expectedProviders.Select(item => Value(item.GetProperty("metadata"), "ContractId"))
                .Where(id => publicIds.Contains(id, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
            var providerImports = Items(actual, "providerImports");
            Assert.Equal(expectedProviderImports.Order(StringComparer.Ordinal), providerImports.Select(item => Value(item, "contractId")).Order(StringComparer.Ordinal));
            Assert.All(providerImports, item => Assert.Equal("sunder.package.agent", Value(item, "packageId")));
            var stackProviders = expectedProviders.Where(item => Value(item.GetProperty("metadata"), "ContractId") == "sunder.stack.contributor").ToArray();
            var platformImports = Items(actual, "platformImports");
            if (stackProviders.Length == 0) Assert.Empty(platformImports);
            else
            {
                var platform = Assert.Single(platformImports);
                Assert.Equal("platform", Value(platform, "authorityKind"));
                Assert.Equal("sunder.core", Value(platform, "authorityId"));
                Assert.Equal("sunder.stack.contributor", Value(platform, "contractId"));
                Assert.Equal(stackProviders.Select(item => Value(item, "include")).Order(StringComparer.Ordinal),
                    platform.GetProperty("providerRefs").EnumerateArray().Select(item => Value(item, "providerId")).Order(StringComparer.Ordinal));
            }
            var expectedRoles = CanonicalProjectHistories.Single(item => item.PackageId == packageId).HasApp ? new[] { "app", "runtime" } : ["runtime"];
            var targets = actual.GetProperty("targets").EnumerateArray().ToArray();
            Assert.Equal(expectedRoles, targets.Select(item => Value(item, "role")).Order(StringComparer.Ordinal));
            Assert.All(targets, target =>
            {
                Assert.Equal(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, Value(target, "rid"));
                Assert.Equal(Value(target, "role") == "app" ? "avalonia" : "worker", Value(target, "kind"));
            });
            Assert.Equal(inventory.RootElement.GetProperty("capabilities").GetProperty(packageId).EnumerateArray().Select(item => item.GetString()).Order(StringComparer.Ordinal),
                targets.SelectMany(target => target.GetProperty("requiredHostCapabilities").EnumerateArray()).Select(item => item.GetString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        }
        Assert.Equal(31, ownedIds.Count);

        static string Value(JsonElement value, string property) => value.GetProperty(property).GetString()!;
        static JsonElement[] Items(JsonElement value, string property) => value.TryGetProperty(property, out var items) ? items.EnumerateArray().ToArray() : [];
    }

    internal static JsonElement CurrentDescriptorSnapshot(SunderRpcContractDescriptor descriptor, string exposure)
        => JsonSerializer.SerializeToElement(new
        {
            contractId = descriptor.ContractId,
            version = descriptor.Version,
            exposure,
            descriptorSha256 = descriptor.Sha256,
            abiSha256 = descriptor.AbiSha256,
            canonicalDescriptorBase64 = Convert.ToBase64String(descriptor.GetCanonicalUtf8()),
            canonicalAbiProjectionBase64 = Convert.ToBase64String(descriptor.GetCanonicalAbiProjectionUtf8()),
            services = descriptor.Services.Select(service => new
            {
                serviceId = service.ServiceId,
                methods = service.Methods.Select(method => new
                {
                    methodId = method.MethodId,
                    kind = method.Kind == SunderRpcMethodKind.ServerStream ? "server-stream" : "unary",
                }).ToArray(),
            }).ToArray(),
        });
}
