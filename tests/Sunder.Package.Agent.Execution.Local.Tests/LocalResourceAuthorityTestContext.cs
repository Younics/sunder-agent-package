extern alias LocalWorker;

using LocalWorker::Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using LocalModels = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models;

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
        var timestamp = new SunderRpcOffsetDateTime(now.ToString("O"));
        var workspaceId = Guid.NewGuid().ToString("N");
        var workspace = new LocalModels.AgentWorkspaceRecord
        {
            WorkspaceId = workspaceId,
            DisplayName = "Authority test workspace",
            CreatedAtUtc = timestamp,
            UpdatedAtUtc = timestamp,
            Paths = config.WorkspacePaths.Select((root, index) => new LocalModels.AgentWorkspacePathRecord
            {
                PathId = Guid.NewGuid().ToString("N"),
                WorkspaceId = workspaceId,
                HostPath = root,
                IsDefault = index == 0,
                SortOrder = index,
                CreatedAtUtc = timestamp,
                UpdatedAtUtc = timestamp,
            }).ToArray(),
            Documents = [],
        };
        var binding = new LocalModels.AgentWorkspaceBindingRecord
        {
            BindingId = Guid.NewGuid().ToString("N"),
            WorkspaceId = workspaceId,
            ExtensionPointId = "execution-targets",
            ContributionId = "local",
            Role = "primary-execution-target",
            IsEnabled = true,
            SortOrder = 0,
            CreatedAtUtc = timestamp,
            UpdatedAtUtc = timestamp,
        };
        var operation = new LocalModels.AgentResourceOperationContext
        {
            RunId = Guid.NewGuid(),
            RunRevision = 1,
            ToolCallId = "tool-call",
            ActionId = actionId,
            ResourceIndex = resourceIndex,
            WorkspaceGeneration = "workspace-generation",
            BindingGeneration = "binding-generation",
            ToolOwnerPackageId = "younics.ai.agent.tools.files",
            ExecutionTargetOwnerPackageId = "younics.ai.agent.execution.local",
            AuthorityActivationId = Guid.NewGuid().ToString("N"),
            AuthorityUseCount = authorityUseCount,
            CanIssueOutsideAuthority = true,
        };
        var planningContext = new LocalModels.AgentExecutionTargetContext
        {
            SessionId = Guid.NewGuid(),
            ProfileId = "profile",
            Workspace = workspace,
            Binding = binding,
            AllowOutsideConfiguredScope = true,
            ApprovedResourceReferences = [],
            ApprovedResourceClaims = [],
            ApprovedResourceCapabilities = [],
            CapturePostMutationResource = false,
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
    LocalModels.AgentResolvedResource Resource,
    LocalModels.AgentExecutionTargetContext Context,
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
