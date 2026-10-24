using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderService(SunderServiceExposure.Public, ContractId = "younics.ai.agent.tool.source", ServiceId = "tool-source", IncludeSchemaDialect = true)]
[SunderRuntimeUse(Actions = ["discover", "invoke"])]
public interface ToolSourceService
{
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<AgentToolSourceDescriptor> DescribeAsync(CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(AgentToolSourceContext context, CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<AgentToolReadiness?> GetReadinessAsync(string toolId, AgentToolSourceContext context, CancellationToken cancellationToken = default);
    /// <summary>Validates arguments without resolving targets, reading resources or requesting authority. Null means valid; failures are corrective tool results, not permission requests.</summary>
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<AgentToolResult?> ValidateArgumentsAsync(AgentToolRequest request, CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<AgentToolResult?> PreflightAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken cancellationToken = default);
    [SunderMethod(TimeoutSeconds = 2100)]
    ValueTask<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<AgentPermissionRequest?> PermissionAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<AgentToolPresentation?> PresentationAsync(AgentToolPresentationRequest request, CancellationToken cancellationToken = default);
}
