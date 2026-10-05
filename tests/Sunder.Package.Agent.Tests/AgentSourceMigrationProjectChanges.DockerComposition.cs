using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeDockerComposition(string path, string text)
    {
        if (!path.Replace('\\', '/').Contains("/src/Sunder.Package.Agent.Execution.Docker/", StringComparison.Ordinal)) return text;
        var additions = Path.GetFileName(path) switch
        {
            "DockerImageCatalogService.cs" or "DockerExecutionWorkspaceConfigService.cs" or "DockerExecutionTarget.cs" or "DockerContainerLifecycleService.cs"
                => new[] { "    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]\n" },
            "DockerPackageStorageMigration.cs" =>
            [
                ", ISunderWorkerSettingsSchemaProvider",
                "    public Sunder.Sdk.Settings.PackageSettingsSchema SettingsSchema => DockerExecutionConfiguration.Schema;\n\n",
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
