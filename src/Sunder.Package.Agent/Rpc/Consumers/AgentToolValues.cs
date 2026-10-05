using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Runtime.Rpc;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class AgentToolValues
{
    internal static SunderRpcCallOptions ExecutionOptions() => new(DateTimeOffset.UtcNow.AddMinutes(35));

    internal static T SourceContext<T>(AgentToolSourceContext context) where T : class
        => AgentConsumerValues.Convert<T>(new
        {
            Context = context, ExecutionTarget = context.ExecutionTargetReference?.ToHandle(), context.ExecutionTargetConfigurationGeneration,
        });

    internal static T Invocation<T>(AgentToolExecutionContext context, AgentToolRequest request) where T : class
        => AgentConsumerValues.Convert<T>(new
        {
            Context = new
            {
                Context = context, ExecutionTarget = context.ExecutionTargetReference?.ToHandle(), context.ApprovedResourceCapabilities,
                context.ExecutionTargetConfigurationGeneration,
                ResourceOperation = context.ResourceOperation is { } value ? new
                {
                    value.RunId, value.RunRevision, value.ToolCallId, value.ActionId, value.ResourceIndex,
                    value.WorkspaceGeneration, value.BindingGeneration, value.ToolOwnerPackageId, value.ExecutionTargetOwnerPackageId,
                    value.AuthorityActivationId, value.AuthorityUseCount, value.CanIssueOutsideAuthority,
                } : null,
            },
            Request = request,
        });
}
