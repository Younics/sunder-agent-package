using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.Execution.Local.Tests;

internal static class LocalResourceAuthorityTestContext
{
    public static ApprovedLocalResource Approve(
        LocalExecutionRuntimeConfig config,
        string path,
        string actionId,
        int authorityUseCount = 1,
        int resourceIndex = 0,
        LocalResourceReference? resourceReferences = null)
    {
        var ownsResourceReferences = resourceReferences is null;
        resourceReferences ??= new LocalResourceReference();
        var now = DateTimeOffset.UtcNow;
        var workspaceId = Guid.NewGuid().ToString("N");
        var workspace = new AgentWorkspaceRecord(
            workspaceId,
            "Authority test workspace",
            null,
            now,
            now,
            config.WorkspacePaths.Select((root, index) => new AgentWorkspacePathRecord(
                Guid.NewGuid().ToString("N"),
                workspaceId,
                root,
                index == 0,
                index,
                now,
                now)).ToArray());
        var binding = new AgentWorkspaceBindingRecord(
            Guid.NewGuid().ToString("N"),
            workspaceId,
            AgentRpcContractIds.ExecutionTarget,
            "local",
            "primary-execution-target",
            true,
            0,
            now,
            now);
        var operation = new AgentResourceOperationContext(
            Guid.NewGuid(),
            1,
            "tool-call",
            actionId,
            resourceIndex,
            "workspace-generation",
            "binding-generation",
            "sunder.package.agent.tools.files",
            "sunder.package.agent.execution.local",
            Guid.NewGuid().ToString("N"),
            authorityUseCount,
            CanIssueOutsideAuthority: true);
        var planningContext = new AgentExecutionTargetContext(
            Guid.NewGuid(),
            "profile",
            workspace,
            binding,
            AllowOutsideConfiguredScope: true)
        {
            ResourceOperation = operation,
        };
        var resource = LocalResourceResolver.ResolveFileResource(
            config,
            path,
            allowOutsideConfiguredScope: true,
            planningContext,
            resourceReferences);
        var claim = resource.ResourceClaim
            ?? throw new InvalidOperationException("Local resource planning did not produce a durable claim.");
        var executionContext = planningContext with
        {
            ApprovedResourceReferences = [resource.CanonicalReference],
            ApprovedResourceClaims = [claim],
            ApprovedResourceCapabilities = resource.AuthorityReferences,
            ResourceOperation = operation with { CanIssueOutsideAuthority = false },
        };
        return new ApprovedLocalResource(
            resource,
            executionContext,
            resourceReferences,
            ownsResourceReferences);
    }
}

internal sealed record ApprovedLocalResource(
    AgentResolvedResource Resource,
    AgentExecutionTargetContext Context,
    LocalResourceReference ResourceReferences,
    bool OwnsResourceReferences) : IDisposable
{
    public void Dispose()
    {
        if (OwnsResourceReferences)
        {
            ResourceReferences.Dispose();
        }
    }
}
