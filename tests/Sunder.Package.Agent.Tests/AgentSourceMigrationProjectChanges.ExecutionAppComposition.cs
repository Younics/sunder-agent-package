using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    private static string BeforeExecutionAppComposition(string path, string text)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.Contains("/src/Sunder.Package.Agent.Shared/Presentation/", StringComparison.Ordinal))
        {
            var role = Path.GetFileName(path) == "PresentationExceptionPolicy.cs" ? "Shared" : "App";
            var expected = Path.GetFileName(path) switch
            {
                "LatestRequestCoordinator.cs" or "PresentationExceptionPolicy.cs" => 2,
                "KeyedAdaptiveListDetailState.cs" or "TimedStatusController.cs" or "AdaptiveMasterDetail.cs" => 1,
                _ => 0,
            };
            if (expected == 0) return text;
            var annotation = $"[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.{role})]\n";
            Assert.Equal(expected + 1, text.Split(annotation, StringSplitOptions.None).Length);
            return text.Replace(annotation, "", StringComparison.Ordinal);
        }
        if (!normalized.Contains("/src/Sunder.Package.Agent.Execution.Local/", StringComparison.Ordinal)
            && !normalized.Contains("/src/Sunder.Package.Agent.Execution.Docker/", StringComparison.Ordinal)) return text;
        var name = Path.GetFileName(path);
        const string shared = "[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]\n";
        var count = name switch
        {
            "LocalExecutionPresentationModels.cs" => 6,
            "DockerExecutionPresentationModels.cs" => 9,
            "LocalExecutionPresentationDefaults.cs" or "DockerExecutionPresentationDefaults.cs" or "ImageCatalogContent.cs" => 1,
            _ => 0,
        };
        if (count != 0)
        {
            Assert.Equal(count + 1, text.Split(shared, StringSplitOptions.None).Length);
            text = text.Replace(shared, "", StringComparison.Ordinal);
        }
        if (name is "LocalExecutionSettingsView.axaml.cs" or "DockerExecutionSettingsView.axaml.cs")
        {
            var view = name[..^".axaml.cs".Length];
            foreach (var addition in new[]
            {
                $"[Sunder.Sdk.Packaging.SunderPackageSettingsView(ViewModel = typeof({view}Model))]\n",
                "    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]\n",
            })
            {
                Assert.Equal(2, text.Split(addition, StringSplitOptions.None).Length);
                text = text.Replace(addition, "", StringComparison.Ordinal);
            }
        }
        return text;
    }
}
