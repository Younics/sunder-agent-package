using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeWebComposition(string path, string text)
    {
        if (!path.Replace('\\', '/').Contains("/src/Sunder.Package.Agent.Tools.Web/", StringComparison.Ordinal)) return text;
        var replacements = Path.GetFileName(path) switch
        {
            "WebFetchService.cs" or "ExaWebSearchBackend.cs" => new (string Current, string Original)[]
            {
                ("    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]\n", ""),
            },
            "WebToolsSettingsService.cs" =>
            [
                ("using Sunder.Sdk.Settings;\nusing Sunder.Sdk.Worker;\n", ""),
                (" : ISunderWorkerSettingsSchemaProvider", ""),
                ("    public PackageSettingsSchema SettingsSchema => WebToolsConfiguration.Schema;\n\n", ""),
            ],
            _ => [],
        };
        foreach (var (current, original) in replacements)
        {
            Assert.Equal(2, text.Split(current, StringSplitOptions.None).Length);
            text = text.Replace(current, original, StringComparison.Ordinal);
        }
        return text;
    }
}
