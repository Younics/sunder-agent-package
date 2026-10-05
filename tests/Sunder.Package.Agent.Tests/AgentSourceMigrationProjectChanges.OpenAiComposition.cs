using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeOpenAiComposition(string path, string text)
    {
        if (!path.Replace('\\', '/').Contains("/src/Sunder.Package.Agent.Provider.OpenAI/", StringComparison.Ordinal)) return text;
        var file = Path.GetFileName(path);
        const string shared = "[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]\n";
        var sharedCount = file is "OpenAiManagementModels.cs" or "OpenAiModelCatalog.cs" ? 2 : file is "OpenAiAuthMode.cs" or "OpenAiModelIds.cs"
            or "OpenAiProviderConfiguration.cs" or "OpenAiUtilityModelSelection.cs" ? 1 : 0;
        if (sharedCount != 0)
        {
            Assert.Equal(sharedCount + 1, text.Split(shared, StringSplitOptions.None).Length);
            text = text.Replace(shared, "", StringComparison.Ordinal);
        }
        var partialType = file switch
        {
            "OpenAiAppManagement.cs" => "OpenAiAuthPresentationService",
            "OpenAiCredentialOwnerClient.cs" => "ProviderCredentialOwnerClient",
            _ => null,
        };
        if (partialType is not null)
        {
            var declaration = "internal sealed partial class " + partialType;
            Assert.Equal(2, text.Split(declaration, StringSplitOptions.None).Length);
            text = text.Replace(declaration, "internal sealed class " + partialType, StringComparison.Ordinal);
        }
        if (Path.GetFileName(path) == "CodexConnectedTransport.cs")
        {
            const string original = "public sealed class CodexConnectedTransport(CodexConnectedAuthStrategy codexConnectedAuthStrategy, HttpClient httpClient)";
            const string current = original + " : IDisposable";
            Assert.Equal(2, text.Split(current, StringSplitOptions.None).Length);
            text = text.Replace(current, original, StringComparison.Ordinal);
        }
        const string constructor = "    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]\n";
        var additions = Path.GetFileName(path) switch
        {
            "ApiKeyAuthStrategy.cs" => new[]
            {
                "using Sunder.Sdk.Settings;\nusing Sunder.Sdk.Worker;\n", " : ISunderWorkerSettingsSchemaProvider", constructor,
                "    public PackageSettingsSchema SettingsSchema => OpenAiProviderConfiguration.Schema;\n\n",
            },
            "CodexConnectedAuthStrategy.cs" => [constructor],
            "OpenAiSettingsView.axaml.cs" =>
            [constructor, "[Sunder.Sdk.Packaging.SunderPackageSettingsView(ViewModel = typeof(OpenAiSettingsViewModel))]\n"],
            "OpenAiAgentProvider.cs" or "OpenAiEmbeddingProvider.cs" => ["[method: Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]\n"],
            "OpenAiRuntimeManagementClient.cs" =>
            [constructor + "    internal OpenAiRuntimeManagementClient(OpenAiAuthOperationHandler auth, Auth.ApiKeyAuthStrategy strategy) : this(auth, strategy.Credentials) { }\n\n"],
            "CodexConnectedTransport.cs" =>
            [
                "    private readonly bool _ownsHttpClient;\n\n" + constructor
                    + "    internal CodexConnectedTransport(CodexConnectedAuthStrategy auth) : this(auth, CodexHttpClientFactory.CreateBackendClient())\n"
                    + "        => _ownsHttpClient = true;\n\n"
                    + "    public void Dispose()\n    {\n        if (_ownsHttpClient) _httpClient.Dispose();\n    }\n",
            ],
            _ => [],
        };
        foreach (var addition in additions)
        {
            Assert.Equal(2, text.Split(addition, StringSplitOptions.None).Length);
            text = text.Replace(addition, "", StringComparison.Ordinal);
        }
        return text;
    }
}
