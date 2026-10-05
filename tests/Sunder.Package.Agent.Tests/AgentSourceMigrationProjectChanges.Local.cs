using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeLocalRelocation(string path, string text)
    {
        text = BeforeExecutionAppComposition(path, text);
        if (path.Replace('\\', '/').Contains("/src/Sunder.Package.Agent.Execution.Docker/", StringComparison.Ordinal))
            return BeforeDockerRelocation(path, text);
        if (!path.Replace('\\', '/').Contains("/src/Sunder.Package.Agent.Execution.Local/", StringComparison.Ordinal)) return text;
        if (Path.GetFileName(path) == "LocalExecutionTarget.cs")
        {
            foreach (var addition in new[]
            {
                ", Sunder.Sdk.Worker.ISunderWorkerSettingsSchemaProvider",
                "    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]\n",
                "    public Sunder.Sdk.Settings.PackageSettingsSchema SettingsSchema => LocalExecutionConfiguration.Schema;\n\n",
            })
            {
                Assert.Equal(2, text.Split(addition, StringSplitOptions.None).Length);
                text = text.Replace(addition, "", StringComparison.Ordinal);
            }
        }
        if (Path.GetFileName(path) == "LocalExecutionPresentationRpc.cs")
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, ".."));
            var models = File.ReadAllText(Path.Combine(root, "Models/LocalExecutionPresentationModels.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
            var defaults = File.ReadAllText(Path.Combine(root, "Runtime/LocalExecutionPresentationDefaults.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
            models = BeforeExecutionAppComposition(Path.Combine(root, "Models/LocalExecutionPresentationModels.cs"), models);
            defaults = BeforeExecutionAppComposition(Path.Combine(root, "Runtime/LocalExecutionPresentationDefaults.cs"), defaults);
            const string prefix = "namespace Sunder.Package.Agent.Execution.Local;\n\n";
            const string marker = "public static class LocalExecutionPresentationRpc";
            const string enumeration = "public enum LocalExecutionOperationKind";
            Assert.StartsWith(prefix, models, StringComparison.Ordinal); Assert.StartsWith(prefix, defaults, StringComparison.Ordinal);
            Assert.Equal(2, text.Split(marker, StringSplitOptions.None).Length);
            text = text.Replace(marker, models[prefix.Length..models.IndexOf(enumeration, StringComparison.Ordinal)].TrimEnd() + "\n\n" + marker, StringComparison.Ordinal);
            text = text.TrimEnd() + "\n\n" + defaults[prefix.Length..].TrimEnd() + "\n\n" + models[models.IndexOf(enumeration, StringComparison.Ordinal)..];
        }
        if (Path.GetFileName(path) == "AppPackageModule.cs")
        {
            Assert.Equal(2, text.Split("LocalAppBindings.Create(", StringSplitOptions.None).Length);
            text = text.Replace("LocalAppBindings.Create(", "new LocalExecutionPresentationRpcClient(", StringComparison.Ordinal);
        }
        return text;
    }

    private static string BeforeDockerRelocation(string path, string text)
    {
        if (Path.GetFileName(path) == "DockerExecutionPresentationRpc.cs")
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, ".."));
            var models = File.ReadAllText(Path.Combine(root, "Models/DockerExecutionPresentationModels.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
            var defaults = File.ReadAllText(Path.Combine(root, "Runtime/DockerExecutionPresentationDefaults.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
            models = BeforeExecutionAppComposition(Path.Combine(root, "Models/DockerExecutionPresentationModels.cs"), models);
            defaults = BeforeExecutionAppComposition(Path.Combine(root, "Runtime/DockerExecutionPresentationDefaults.cs"), defaults);
            const string prefix = "namespace Sunder.Package.Agent.Execution.Docker;\n\n";
            const string marker = "public static class DockerExecutionPresentationRpc";
            const string enumeration = "public enum DockerExecutionOperationKind";
            Assert.StartsWith(prefix, models, StringComparison.Ordinal); Assert.StartsWith(prefix, defaults, StringComparison.Ordinal);
            Assert.Equal(2, text.Split(marker, StringSplitOptions.None).Length);
            text = text.Replace(marker, models[prefix.Length..models.IndexOf(enumeration, StringComparison.Ordinal)].TrimEnd() + "\n\n" + marker, StringComparison.Ordinal);
            text = text.TrimEnd() + "\n\n" + defaults[prefix.Length..].TrimEnd() + "\n\n" + models[models.IndexOf(enumeration, StringComparison.Ordinal)..];
        }
        if (Path.GetFileName(path) == "AppPackageModule.cs")
        {
            Assert.Equal(2, text.Split("DockerAppBindings.Create(", StringSplitOptions.None).Length);
            text = text.Replace("DockerAppBindings.Create(", "new DockerExecutionPresentationRpcClient(", StringComparison.Ordinal);
        }
        return text;
    }
}
