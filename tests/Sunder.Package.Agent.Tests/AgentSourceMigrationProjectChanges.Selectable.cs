using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal const string ObserverCompatibilitySource = "src/Sunder.Package.Agent.Contracts/Extensions/AgentProfileSelectableCapabilityChangeObserver.Compatibility.cs";

    internal static string BeforeAgentSelectableBindings(string path, string text)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.EndsWith("/src/Sunder.Package.Agent/Models/Extensions/AgentProfileSelectableCapabilityChangeObserver.cs", StringComparison.Ordinal))
        {
            var compatibility = ReadMigrationSourceText(Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, ObserverCompatibilitySource))
                .Replace("\r\n", "\n", StringComparison.Ordinal);
            const string header = "using Sunder.Package.Agent.Protocol;\n\nnamespace Sunder.Package.Agent.Contracts.Services;\n\n"
                + "// Retained Protocol construction surface. The owning observer's business source\n"
                + "// takes an explicit role-local binding and does not depend on transport factories.\n"
                + "public sealed partial class AgentProfileSelectableCapabilityChangeObserver\n{\n";
            Assert.StartsWith(header, compatibility, StringComparison.Ordinal);
            Assert.EndsWith("}\n", compatibility, StringComparison.Ordinal);
            const string declaration = "public sealed partial class AgentProfileSelectableCapabilityChangeObserver";
            Assert.Equal(2, text.Split(declaration, StringSplitOptions.None).Length);
            text = text.Replace(declaration, "public sealed class AgentProfileSelectableCapabilityChangeObserver", StringComparison.Ordinal);
            const string constructor = "    internal AgentProfileSelectableCapabilityChangeObserver(\n";
            Assert.Equal(2, text.Split(constructor, StringSplitOptions.None).Length);
            // Consume the entire compatibility body when reconstructing the frozen
            // source: additions cannot disappear behind the split-file accounting.
            text = text.Replace(constructor, compatibility[header.Length..^2] + constructor, StringComparison.Ordinal);
            Assert.Equal(2, text.Split("e.IncludesContract(_service.ContractId)", StringSplitOptions.None).Length);
            text = text.Replace("e.IncludesContract(_service.ContractId)", "e.IncludesContract(AgentRpcContractIds.SelectableCapabilityProvider)", StringComparison.Ordinal);
            foreach (var addition in new[]
            {
                "    private readonly AgentRpcService<IAgentProfileSelectableCapabilityProvider> _service;\n",
                "        _service = service;\n",
            })
            {
                Assert.Equal(2, text.Split(addition, StringSplitOptions.None).Length);
                text = text.Replace(addition, "", StringComparison.Ordinal);
            }
            const string constructors = """
                    bool refreshProviderSubscriptions)
                    : this(catalog, refreshProviderSubscriptions, AgentRpcServices.SelectableCapabilityProviders)
                {
                }

                internal AgentProfileSelectableCapabilityChangeObserver(
                    AgentRpcCatalog catalog,
                    bool refreshProviderSubscriptions,
                    AgentRpcService<IAgentProfileSelectableCapabilityProvider> service)
            """;
            Assert.Equal(2, text.Split(constructors, StringSplitOptions.None).Length);
            text = text.Replace(constructors, "        bool refreshProviderSubscriptions)", StringComparison.Ordinal);
            Assert.Equal(2, text.Split("_catalog.GetServiceReferences(_service)", StringSplitOptions.None).Length);
            return text.Replace("_catalog.GetServiceReferences(_service)", "_catalog.GetServiceReferences(AgentRpcServices.SelectableCapabilityProviders)", StringComparison.Ordinal);
        }
        if (normalized.EndsWith("/src/Sunder.Package.Agent/Services/AgentProfileService.cs", StringComparison.Ordinal))
        {
            const string injected = "            refreshProviderSubscriptions,\n            Sunder.Package.Agent.Runtime.Rpc.AgentSelectableBindings.Service);";
            Assert.Equal(2, text.Split(injected, StringSplitOptions.None).Length);
            text = text.Replace(injected, "            refreshProviderSubscriptions);", StringComparison.Ordinal);
        }
        if (normalized.EndsWith("/src/Sunder.Package.Agent/Services/AgentProfileService.cs", StringComparison.Ordinal)
            || normalized.EndsWith("/src/Sunder.Package.Agent/Services/AgentProfileStackContributor.cs", StringComparison.Ordinal))
        {
            const string selector = "Sunder.Package.Agent.Runtime.Rpc.AgentSelectableBindings.Service";
            Assert.Equal(2, text.Split(selector, StringSplitOptions.None).Length);
            text = text.Replace(selector, "AgentRpcServices.SelectableCapabilityProviders", StringComparison.Ordinal);
        }
        return text;
    }
}
