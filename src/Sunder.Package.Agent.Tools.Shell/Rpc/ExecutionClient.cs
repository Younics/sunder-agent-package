using Sunder.Package.Agent.Contracts.Models;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Execution_Target;

namespace Sunder.Package.Agent.Tools.Shell.Rpc;

internal sealed class ExecutionClient(ShellExecutionBindings.TargetLease lease, AgentExecutionTargetDescriptor descriptor) : IShellExecutionTarget
{
    public AgentExecutionTargetDescriptor Descriptor => descriptor;
    public async ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(AgentExecutionTargetContext context, CancellationToken token)
        => ShellProviderValues.Convert<AgentExecutionTargetReadiness>(await lease.InvokeAsync((client, cancellation) => client.ReadinessAsync(Context(context), cancellation), token).ConfigureAwait(false));
    public async ValueTask<AgentExecutionShellDescriptor> GetShellAsync(AgentExecutionTargetContext context, CancellationToken token)
        => ShellProviderValues.Convert<AgentExecutionShellDescriptor>(await lease.InvokeAsync((client, cancellation) => client.ShellAsync(Context(context), cancellation), token).ConfigureAwait(false));
    public async ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken token)
        => ShellProviderValues.Convert<AgentShellCommandResult>(await lease.InvokeAsync((client, cancellation) => client.ExecuteShellAsync(new()
        { Context = Context(context), Request = ShellProviderValues.Convert<Wire.AgentShellCommandRequest>(request) }, cancellation), token).ConfigureAwait(false));
    private static Wire.AgentExecutionTargetContextWire Context(AgentExecutionTargetContext value)
        => new()
        {
            Context = ShellProviderValues.Convert<Wire.AgentExecutionTargetContext>(value),
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
}
