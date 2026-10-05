using System.Reflection;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static void AssertExecutionBackendOwnership(Assembly assembly, bool app)
    {
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Protocol" or "Sunder.Agent.Execution.Common");
        if (!app) Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "CommunityToolkit.Mvvm" or "Sunder.Sdk.Avalonia"
            or "Microsoft.Extensions.AI.Abstractions" || reference.Name!.StartsWith("Avalonia", StringComparison.Ordinal));
        foreach (var name in new[] { "Sunder.Agent.Execution.Common.HostSecurePathEngine", "Sunder.Agent.Execution.Common.HostApprovalLeaseStore`1",
            "Sunder.Agent.Execution.Common.HostMutationCoordinator", "Sunder.Agent.Execution.Common.BoundedProcessRunner" })
        {
            if (app) Assert.Null(assembly.GetType(name));
            else Assert.Same(assembly, assembly.GetType(name, true)!.Assembly);
        }
        Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Contracts.Models.AgentShellSyntaxKinds", true)!.Assembly);
        // The context is a shared value used by Builder's typed proxy; it does not
        // own any backend authority. The authority implementations above stay Worker-only.
        if (!app) Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Contracts.Models.AgentResourceOperationContext", true)!.Assembly);
        if (!app) Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Contracts.Contracts.IAgentExecutionTarget", true)!.Assembly);
        if (app)
            foreach (var family in new[] { "Local", "Docker" })
                Assert.Null(assembly.GetType($"Sunder.Package.Agent.Execution.{family}.{family}ExecutionTarget"));
        if (!app) Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Contracts.Models.AgentExecutionShellDescriptor", true)!.Assembly);
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Protocol.AgentRpcCatalog"));
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Protocol.AgentRpcServices"));
    }
}
