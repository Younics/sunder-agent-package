using System.Reflection;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static void AssertBuilderSemanticOwnership(Assembly assembly, bool app)
    {
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name!.StartsWith("Sunder.Package.Agent", StringComparison.Ordinal) || reference.Name == "Sunder.Agent.Execution.Common");
        Assert.DoesNotContain(assembly.GetTypes(), type => type.Namespace?.StartsWith("Sunder.Generated.Packages.Younics_Ai_Agent_", StringComparison.Ordinal) == true);
        foreach (var name in new[] { "BuilderProjectKind", "BuilderProjectInspection", "BuilderJobRequest", "BuilderJobSnapshot", "BuilderToolchainStatus", "BuilderContent" })
            Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Builder." + name, true)!.Assembly);
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Builder.BuilderWorkspaceExecutionService"));
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Builder.BuilderRpcExecutor"));
        if (app)
        {
            Assert.NotNull(assembly.GetType("Sunder.Package.Agent.Builder.BuilderBackendGateway", true));
            Assert.NotNull(assembly.GetType("Sunder.Package.Agent.Builder.BuilderSettingsView", true));
            Assert.Null(assembly.GetType("Sunder.Package.Agent.Builder.BuilderRuntimeService"));
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Sdk.Worker" or "Microsoft.Extensions.AI.Abstractions");
        }
        else
        {
            var constructor = Assert.Single(assembly.GetType("Sunder.Package.Agent.Builder.BuilderRuntimeService", true)!.GetConstructors());
            Assert.Equal(typeof(IPackageContext), Assert.Single(constructor.GetParameters()).ParameterType);
            Assert.Null(assembly.GetType("Sunder.Package.Agent.Builder.BuilderViewModel"));
            Assert.Null(assembly.GetType("Sunder.Package.Agent.Builder.BuilderBackendGateway"));
            Assert.Contains(assembly.GetReferencedAssemblies(), reference => reference.Name == "Microsoft.CodeAnalysis.CSharp");
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Sdk.Avalonia" or "CommunityToolkit.Mvvm"
                || reference.Name!.StartsWith("Avalonia", StringComparison.Ordinal));
        }
    }
}
