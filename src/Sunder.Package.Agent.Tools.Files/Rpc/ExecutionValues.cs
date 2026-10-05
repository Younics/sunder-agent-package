using Domain = Sunder.Package.Agent.Contracts.Models;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Execution_Target;

namespace Sunder.Package.Agent.Tools.Files.Rpc;

internal static class ExecutionValues
{
    internal static Wire.AgentExecutionTargetContextWire Context(Domain.AgentExecutionTargetContext value)
        => new()
        {
            Context = FilesProviderValues.Convert<Wire.AgentExecutionTargetContext>(value),
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
        => FilesProviderValues.Convert<Domain.AgentResolvedResource>(value.Resource) with { AuthorityReferences = value.AuthorityReferences, DeleteAuthorityReferences = value.DeleteAuthorityReferences };
    internal static Domain.AgentFileMutationResult Mutation(Wire.AgentFileMutationResultWire value)
        => FilesProviderValues.Convert<Domain.AgentFileMutationResult>(value.Result) with { PostMutationResource = value.PostMutationResource is { } resource ? Resource(resource) : null };
}
