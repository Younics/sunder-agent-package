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
            AssertImportedRoleModel(assembly, name);
        var context = AssertImportedRoleModel(assembly, "AgentExecutionTargetContext");
        foreach (var name in new[] { "ApprovedResourceCapabilities", "ResourceOperation", "CapturePostMutationResource", "ExpectedConfigurationGeneration" })
        {
            // The authored durable model excludes authority, while the reviewed
            // imported service DTO explicitly carries those invocation-only fields.
            Assert.Equal(JsonIgnoreCondition.Always, typeof(Sunder.Package.Agent.Contracts.Models.AgentExecutionTargetContext)
                .GetProperty(name)!.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition);
            Assert.Equal(char.ToLowerInvariant(name[0]) + name[1..],
                context.GetProperty(name)!.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name);
        }
        Assert.Equal(JsonIgnoreCondition.Always, typeof(Sunder.Package.Agent.Contracts.Models.AgentResourceOperationContext)
            .GetProperty("AuthorityActivationId")!.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition);
        Assert.Equal("authorityActivationId", AssertImportedRoleModel(assembly, "AgentResourceOperationContext")
            .GetProperty("AuthorityActivationId")!.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name);
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Protocol.AgentRpcCatalog"));
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Protocol.AgentRpcProviderHandle"));
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Contracts.Contracts.IAgentProcessExecutionTarget"));
        Assert.Same(assembly, assembly.GetType(ImportedServices + "IExecutionTargetService", true)!.Assembly);
        if (app)
        {
            AssertImportedRoleModel(assembly, "AgentModelDescriptor");
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Sdk.Worker" or "Microsoft.Extensions.AI.Abstractions");
        }
        else
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Sdk.Avalonia" or "CommunityToolkit.Mvvm"
                || reference.Name!.StartsWith("Avalonia", StringComparison.Ordinal));
    }
}
