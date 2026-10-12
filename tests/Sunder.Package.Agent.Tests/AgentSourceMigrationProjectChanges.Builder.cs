using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeBuilderRelocation(string path, string text)
    {
        if (!path.Replace('\\', '/').Contains("/src/Sunder.Package.Agent.Builder/", StringComparison.Ordinal)) return text;
        if (Path.GetFileName(path) == "BuilderRpcContracts.cs")
        {
            const string role = "[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]\n";
            Assert.Equal(7, text.Split(role, StringSplitOptions.None).Length);
            text = text.Replace(role, "", StringComparison.Ordinal);
        }
        if (Path.GetFileName(path) == "BuilderRpcProvider.cs")
        {
            const string marker = "internal sealed class BuilderRpcDomainException";
            Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
            var operations = ReadMigrationSourceText(Path.Combine(Path.GetDirectoryName(path)!, "Runtime/BuilderRuntimeOperations.cs"));
            Assert.Equal(2, operations.Split(marker, StringSplitOptions.None).Length);
            text = text.TrimEnd() + "\n\n" + operations[operations.IndexOf(marker, StringComparison.Ordinal)..];
        }
        if (Path.GetFileName(path) == "BuilderWorkspaceExecutionService.cs")
        {
            const string declaration = "public sealed partial class BuilderWorkspaceExecutionService";
            Assert.Equal(2, text.Split(declaration, StringSplitOptions.None).Length);
            text = text.Replace(declaration, "public sealed class BuilderWorkspaceExecutionService", StringComparison.Ordinal);
            var legacy = ReadMigrationSourceText(Path.Combine(Path.GetDirectoryName(path)!, "App/BuilderWorkspaceExecutionService.Legacy.cs"));
            Assert.Contains("    [ActivatorUtilitiesConstructor]\n    public BuilderWorkspaceExecutionService(ISunderRpcClient rpcClient) : this(new BuilderAppRpcGateway(rpcClient)) { }\n", legacy, StringComparison.Ordinal);
            const string constructor = "    public BuilderWorkspaceExecutionService()\n";
            Assert.Equal(2, text.Split(constructor, StringSplitOptions.None).Length);
            text = text.Replace(constructor, "    public BuilderWorkspaceExecutionService(ISunderRpcClient rpcClient)\n"
                + "        : this(new BuilderAppRpcGateway(rpcClient))\n    {\n    }\n\n" + constructor, StringComparison.Ordinal);
        }
        if (Path.GetFileName(path) == "BuilderView.axaml.cs")
        {
            const string view = "// The constructor assigns the activation-owned Builder model to DataContext.\n"
                + "[Sunder.Sdk.Packaging.SunderPackageView(\"sunder.package.agent.builder\", \"Package Builder\",\n"
                + "    IconPath = \"Assets/builder-icon.png\", DefaultPlacement = PackageViewPlacement.Middle)]\n";
            Assert.Equal(2, text.Split(view, StringSplitOptions.None).Length);
            text = text.Replace(view, "", StringComparison.Ordinal);
        }
        if (Path.GetFileName(path) == "PackageModule.cs")
        {
            // Reconcile the old explicit factories against the frozen source oracle.
            // The production constructors now own these same business dependencies.
            var replacements = new (string Current, string Original)[]
            {
                ("using Sunder.Package.Agent.Shared.Composition;\n", "using Sunder.Package.Agent.Shared.Composition;\nusing Sunder.Package.Agent.Shared.Presentation;\n"),
                ("using Sunder.Sdk.Avalonia;\n", "using Sunder.Sdk.Avalonia;\nusing Sunder.Sdk.Notifications;\nusing Sunder.Sdk.Rpc;\n"),
                ("        services.AddSingleton<BuilderWorkspaceExecutionService>();", "        services.AddSingleton(provider => new BuilderWorkspaceExecutionService(\n            provider.GetRequiredService<ISunderRpcClient>()));"),
                ("        services.AddSingleton<BuilderViewModel>();\n        services.AddTransient<BuilderView>();", """
                        services.AddSingleton(provider => new BuilderViewModel(
                            provider.GetRequiredService<BuilderProjectApplicationService>(),
                            provider.GetRequiredService<BuilderOperationQueue>(),
                            provider.GetRequiredService<BuilderProjectPersistence>(),
                            provider.GetRequiredService<BuilderPathService>(),
                            provider.GetRequiredService<IBuilderUiDispatcher>(),
                            new PresentationOperationObserver(
                                context,
                                provider.GetRequiredService<IPackageNotificationService>(),
                                "Package Builder")));
                        services.AddTransient(provider => new BuilderView(
                            provider.GetRequiredService<BuilderViewModel>(),
                            context,
                            provider.GetRequiredService<IPackageNotificationService>()));
                """),
            };
            foreach (var (current, original) in replacements)
            {
                Assert.Equal(2, text.Split(current, StringSplitOptions.None).Length);
                text = text.Replace(current, original, StringComparison.Ordinal);
            }
        }
        return text;
    }
}
