using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    private static string BeforeMcpAppComposition(string path, string text)
    {
        var file = Path.GetFileName(path);
        const string shared = "[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]\n";
        var count = file switch
        {
            "McpManagementModels.cs" => 9,
            "McpManagementServiceModels.cs" or "McpConfigurationImportPipeline.cs" => 3,
            "McpConfigurationDocument.cs" or "McpConnectionStatus.cs" => 2,
            "ConfiguredMcpServerRecord.cs" or "ConfiguredMcpTransportType.cs" or "McpCatalogDiagnostic.cs" or "McpTransportSecurity.cs"
                or "McpServerName.cs" or "McpOAuthConstants.cs" or "McpManagementContent.cs" => 1,
            _ => 0,
        };
        if (count != 0)
        {
            Assert.Equal(count + 1, text.Split(shared, StringSplitOptions.None).Length);
            text = text.Replace(shared, "", StringComparison.Ordinal);
        }
        if (file == "McpAppRpcGateway.cs") Replace("internal sealed partial class McpAppRuntimeGateway", "internal sealed class McpAppRuntimeGateway");
        if (file == "AgentMcpSettingsView.axaml.cs")
        {
            Replace("[Sunder.Sdk.Packaging.SunderPackageSettingsView(ViewModel = typeof(AgentMcpSettingsViewModel))]\n", "");
            Replace("    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]\n    public AgentMcpSettingsView(\n        AgentMcpSettingsViewModel viewModel,\n"
                + "        IPackageContext packageContext,\n        IPackageNotificationService notifications)\n        : this(viewModel)",
                "    internal void ConfigureOperationObserver(\n        IPackageContext packageContext,\n        IPackageNotificationService notifications)");
        }
        if (file == "PackageModule.cs")
            Replace("        services.AddTransient(provider => new AgentMcpSettingsView(\n            provider.GetRequiredService<AgentMcpSettingsViewModel>(),\n"
                + "            context,\n            provider.GetRequiredService<IPackageNotificationService>()));",
                "        services.AddTransient(provider =>\n        {\n            var view = new AgentMcpSettingsView(\n                provider.GetRequiredService<AgentMcpSettingsViewModel>());\n"
                + "            view.ConfigureOperationObserver(\n                context,\n                provider.GetRequiredService<IPackageNotificationService>());\n            return view;\n        });");
        return text;

        void Replace(string current, string previous)
        {
            Assert.Equal(2, text.Split(current, StringSplitOptions.None).Length);
            text = text.Replace(current, previous, StringComparison.Ordinal);
        }
    }
}
