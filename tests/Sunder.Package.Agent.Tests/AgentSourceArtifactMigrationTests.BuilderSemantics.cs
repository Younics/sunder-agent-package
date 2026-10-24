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
        // The optional API is generated into each role, never loaded from Agent's
        // implementation assembly. Receipt uses and startup probes fence authority.
        Assert.Same(assembly, assembly.GetType("Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentToolRequest", true)!.Assembly);
        foreach (var name in new[] { "BuilderProjectKind", "BuilderProjectInspection", "BuilderJobRequest", "BuilderJobSnapshot", "BuilderToolchainStatus", "BuilderContent" })
            Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Builder." + name, true)!.Assembly);
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Builder.BuilderWorkspaceExecutionService"));
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Builder.BuilderRpcExecutor"));
        if (app)
        {
            Assert.NotNull(assembly.GetType("Sunder.Package.Agent.Builder.BuilderBackendGateway", true));
            Assert.NotNull(assembly.GetType("Sunder.Package.Agent.Builder.BuilderSettingsView", true));
            Assert.Null(assembly.GetType("Sunder.Package.Agent.Builder.BuilderRuntimeService"));
            Assert.Null(assembly.GetType("Sunder.Package.Agent.Builder.BuilderToolSource"));
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Sdk.Worker" or "Microsoft.Extensions.AI.Abstractions");
        }
        else
        {
            var constructor = Assert.Single(assembly.GetType("Sunder.Package.Agent.Builder.BuilderRuntimeService", true)!.GetConstructors());
            Assert.Equal(typeof(IPackageContext), Assert.Single(constructor.GetParameters()).ParameterType);
            var tools = assembly.GetType("Sunder.Package.Agent.Builder.BuilderToolSource", true)!;
            Assert.Equal(assembly.GetType("Sunder.Package.Agent.Builder.BuilderRuntimeService", true),
                Assert.Single(Assert.Single(tools.GetConstructors()).GetParameters()).ParameterType);
            Assert.Contains(tools.GetInterfaces(), type => type.FullName == "Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Tool_Source.IToolSourceService");
            Assert.Contains(tools.GetInterfaces(), type => type.FullName == "Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Permission_Surface.IPermissionSurfaceService");
            Assert.Null(assembly.GetType("Sunder.Package.Agent.Builder.BuilderViewModel"));
            Assert.Null(assembly.GetType("Sunder.Package.Agent.Builder.BuilderBackendGateway"));
            Assert.Contains(assembly.GetReferencedAssemblies(), reference => reference.Name == "Microsoft.CodeAnalysis.CSharp");
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Sdk.Avalonia" or "CommunityToolkit.Mvvm"
                || reference.Name!.StartsWith("Avalonia", StringComparison.Ordinal));
        }
    }
}
