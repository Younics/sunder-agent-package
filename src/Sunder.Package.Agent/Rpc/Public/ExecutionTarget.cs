using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderService(SunderServiceExposure.Public, ContractId = "younics.ai.agent.execution.target", ServiceId = "execution-target", IncludeSchemaDialect = true)]
[SunderServiceError("agent.execution.facet-unavailable", Description = "The execution target does not implement the requested optional facet.")]
[SunderRuntimeUse(Actions = ["discover", "invoke"])]
public interface ExecutionTargetService
{
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<AgentExecutionTargetDescriptor> DescribeAsync(CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<string?> ConfigurationGenerationAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<AgentExecutionTargetReadiness> ReadinessAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<AgentExecutionShellDescriptor> ShellAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default);
    ValueTask<AgentResolvedResource> ResolveFileAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default);
    ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<AgentFileReadResult> ReadFileAsync(AgentExecutionTargetContext context, AgentFileReadRequest request, CancellationToken cancellationToken = default);
    ValueTask<AgentFileMutationResult> WriteFileAsync(AgentExecutionTargetContext context, AgentFileWriteRequest request, CancellationToken cancellationToken = default);
    ValueTask<AgentFileMutationResult> DeleteFileAsync(AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken cancellationToken = default);
    [SunderMethod(Errors = ["agent.execution.facet-unavailable"])]
    ValueTask<AgentShellCommandResult> ExecuteProcessAsync(AgentExecutionTargetContext context, AgentProcessCommandRequest request, CancellationToken cancellationToken = default);
    [SunderMethod(Errors = ["agent.execution.facet-unavailable"])]
    ValueTask<IReadOnlyList<AgentResolvedExecutionResource>> ResolveResourcesAsync(AgentExecutionTargetContext context,
        IReadOnlyList<AgentExecutionResourceDescriptor> resources, CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["agent.execution.facet-unavailable"])]
    ValueTask<AgentResourceAuthorityValidation> ValidateResourceAuthorityAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["agent.execution.facet-unavailable"])]
    ValueTask ReleaseResourceAuthorityAsync(IReadOnlyList<string> capabilities, CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["agent.execution.facet-unavailable"])]
    ValueTask<AgentExecutionScopeDescriptor> ExecutionScopeAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["agent.execution.facet-unavailable"])]
    ValueTask<AgentExecutionPathMapping> MapPathAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["agent.execution.facet-unavailable"])]
    ValueTask<IReadOnlyList<string>> ListPathAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default);
    [SunderMethod(Errors = ["agent.execution.facet-unavailable"])]
    ValueTask AddPathAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["agent.execution.facet-unavailable"])]
    ValueTask<AgentFileSearchResult> StructuredFileSearchAsync(AgentExecutionTargetContext context, AgentFileSearchRequest request, CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["agent.execution.facet-unavailable"])]
    ValueTask<AgentScopedInstructionDiscoveryResult> DiscoverInstructionsAsync(AgentExecutionTargetContext context,
        AgentScopedInstructionDiscoveryRequest request, CancellationToken cancellationToken = default);
}
