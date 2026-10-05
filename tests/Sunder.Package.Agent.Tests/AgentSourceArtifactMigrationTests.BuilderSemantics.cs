using System.Reflection;
using System.Text.Json.Serialization;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static void AssertBuilderSemanticOwnership(Assembly assembly, bool app)
    {
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Protocol" or "Sunder.Agent.Execution.Common");
        foreach (var name in new[] { "AgentWorkspaceRecord", "AgentWorkspacePathRecord", "AgentExecutionPathMapping", "AgentExecutionScopeDescriptor", "AgentResourceOperationContext" })
            Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Contracts.Models." + name, true)!.Assembly);
        var context = assembly.GetType("Sunder.Package.Agent.Contracts.Models.AgentExecutionTargetContext", true)!;
        foreach (var name in new[] { "ApprovedResourceCapabilities", "ResourceOperation", "CapturePostMutationResource", "ExpectedConfigurationGeneration" })
            Assert.Equal(JsonIgnoreCondition.Always, context.GetProperty(name)!.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition);
        Assert.Equal(JsonIgnoreCondition.Always, assembly.GetType("Sunder.Package.Agent.Contracts.Models.AgentResourceOperationContext", true)!
            .GetProperty("AuthorityActivationId")!.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition);
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Protocol.AgentRpcCatalog"));
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Protocol.AgentRpcProviderHandle"));
        Assert.Equal(app, assembly.GetType("Sunder.Package.Agent.Contracts.Contracts.IAgentProcessExecutionTarget") is not null);
        if (app)
        {
            Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Contracts.Models.AgentModelDescriptor", true)!.Assembly);
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Sdk.Worker" or "Microsoft.Extensions.AI.Abstractions");
        }
        else
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Sdk.Avalonia" or "CommunityToolkit.Mvvm"
                || reference.Name!.StartsWith("Avalonia", StringComparison.Ordinal));
    }
}
