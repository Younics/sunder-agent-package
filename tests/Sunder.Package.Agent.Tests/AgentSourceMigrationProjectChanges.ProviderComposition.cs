using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeProviderComposition(string path, string text)
    {
        if (!path.Replace('\\', '/').Contains("/src/Sunder.Package.Agent.Provider.", StringComparison.Ordinal)) return text;
        var type = Path.GetFileNameWithoutExtension(path);
        const string shared = "[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]\n";
        var sharedCount = type is "ProviderModelCatalog" or "LMStudioConnectionOptions" ? 2 : type is "GeminiModelCatalog" or "AnthropicModelCatalog"
            or "GeminiProviderConfiguration" or "AnthropicProviderConfiguration" or "LMStudioProviderConfiguration"
            or "ProviderConfigurationSections" or "ProviderModelId" ? 1 : 0;
        if (sharedCount != 0)
        {
            Assert.Equal(sharedCount + 1, text.Split(shared, StringSplitOptions.None).Length);
            text = text.Replace(shared, "", StringComparison.Ordinal);
        }
        const string attribute = "    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]\n";
        var addition = type switch
        {
            "GeminiAgentProvider" or "GeminiEmbeddingProvider" or "AnthropicAgentProvider" or "LMStudioConnection"
                => attribute + $"    internal {type}(IPackageContext packageContext, ProviderSettings settings)\n        : this(packageContext, settings.Credentials) {{ }}\n\n",
            "LMStudioAgentProvider" or "LMStudioEmbeddingProvider" or "GeminiSettingsViewModel" or "AnthropicSettingsViewModel" or "LMStudioSettingsViewModel"
                or "GeminiSettingsView.axaml" or "AnthropicSettingsView.axaml" or "LMStudioSettingsView.axaml" => attribute,
            _ => null,
        };
        if (addition is null) return text;
        Assert.Equal(2, text.Split(addition, StringSplitOptions.None).Length);
        text = text.Replace(addition, "", StringComparison.Ordinal);
        if (type.EndsWith("SettingsView.axaml", StringComparison.Ordinal))
        {
            var view = type[..^".axaml".Length];
            var declaration = $"[Sunder.Sdk.Packaging.SunderPackageSettingsView(ViewModel = typeof({view}Model))]\n";
            Assert.Equal(2, text.Split(declaration, StringSplitOptions.None).Length);
            text = text.Replace(declaration, "", StringComparison.Ordinal);
        }
        return text;
    }
}
