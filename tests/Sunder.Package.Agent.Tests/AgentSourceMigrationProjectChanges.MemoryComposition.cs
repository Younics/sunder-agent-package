using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeMemoryComposition(string path, string text)
    {
        if (!path.Replace('\\', '/').Contains("/src/Sunder.Package.Agent.Memory.Semantic/", StringComparison.Ordinal)) return text;
        var file = Path.GetFileName(path);
        const string shared = "[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]\n";
        var sharedCount = file switch
        {
            "MemoryInspectorContracts.cs" or "SemanticEmbeddingContext.cs" or "MemoryValueJson.cs" or "ProjectionContent.cs" => 1,
            "MemoryInspectorModels.cs" => 8,
            "MemoryStoreModels.cs" => 3,
            "SemanticMemoryInspectorModels.cs" => 6,
            "SemanticMemoryServiceModels.cs" => 2,
            _ => 0,
        };
        if (sharedCount != 0)
        {
            Assert.Equal(sharedCount + 1, text.Split(shared, StringSplitOptions.None).Length);
            text = text.Replace(shared, "", StringComparison.Ordinal);
        }
        if (file == "MemoryInspectorView.axaml.cs")
            foreach (var addition in new[]
            {
                "[Sunder.Sdk.Packaging.SunderPackageView(\"sunder.package.agent.memory.semantic.inspector\", \"Memory Inspector\",\n"
                    + "    ViewModel = typeof(MemoryInspectorViewModel), IconPath = \"assets/memory-icon.png\", DefaultPlacement = PackageViewPlacement.LeftBottom)]\n",
                "    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]\n",
            })
            {
                Assert.Equal(2, text.Split(addition, StringSplitOptions.None).Length);
                text = text.Replace(addition, "", StringComparison.Ordinal);
            }
        if (file != "MemorySemanticSettingsService.cs") return text;
        foreach (var addition in new[]
        {
            "using Sunder.Sdk.Settings;\nusing Sunder.Sdk.Worker;\n",
            " : ISunderWorkerSettingsSchemaProvider",
            "    public PackageSettingsSchema SettingsSchema => MemorySemanticPackageConfiguration.Schema;\n\n",
        })
        {
            Assert.Equal(2, text.Split(addition, StringSplitOptions.None).Length);
            text = text.Replace(addition, "", StringComparison.Ordinal);
        }
        return text;
    }
}
