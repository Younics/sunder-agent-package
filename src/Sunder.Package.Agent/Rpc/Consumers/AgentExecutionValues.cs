using Domain = Sunder.Package.Agent.Contracts.Models;
using Wire = Sunder.Generated.Rpc.ExecutionTarget;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal static class AgentExecutionValues
{
    internal static Wire.AgentExecutionTargetContextWire Context(Domain.AgentExecutionTargetContext value)
        => new()
        {
            Context = AgentConsumerValues.Convert<Wire.AgentExecutionTargetContext>(value),
            ApprovedResourceCapabilities = value.ApprovedResourceCapabilities,
            CapturePostMutationResource = value.CapturePostMutationResource,
            ExpectedConfigurationGeneration = value.ExpectedConfigurationGeneration,
            ResourceOperation = value.ResourceOperation is { } operation ? new Wire.AgentResourceOperationContextWire
            {
                RunId = operation.RunId, RunRevision = operation.RunRevision, ToolCallId = operation.ToolCallId, ActionId = operation.ActionId,
                ResourceIndex = operation.ResourceIndex, WorkspaceGeneration = operation.WorkspaceGeneration, BindingGeneration = operation.BindingGeneration,
                ToolOwnerPackageId = operation.ToolOwnerPackageId, ExecutionTargetOwnerPackageId = operation.ExecutionTargetOwnerPackageId,
                AuthorityActivationId = operation.AuthorityActivationId, AuthorityUseCount = operation.AuthorityUseCount, CanIssueOutsideAuthority = operation.CanIssueOutsideAuthority,
            } : null,
        };

    internal static Domain.AgentResolvedResource Resource(Wire.AgentResolvedResourceWire value)
        => AgentConsumerValues.Convert<Domain.AgentResolvedResource>(value.Resource) with
        { AuthorityReferences = value.AuthorityReferences.ToArray(), DeleteAuthorityReferences = value.DeleteAuthorityReferences.ToArray() };
    internal static Domain.AgentFileMutationResult Mutation(Wire.AgentFileMutationResultWire value)
        => AgentConsumerValues.Convert<Domain.AgentFileMutationResult>(value.Result) with
        { PostMutationResource = value.PostMutationResource is { } resource ? Resource(resource) : null };
}
