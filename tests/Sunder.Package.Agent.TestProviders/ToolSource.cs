using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using IToolSourceService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Tool_Source.IToolSourceService;
using Sunder.Sdk.Services;

namespace RegressionTestGeneratedProviders;

[SunderServiceProvider(typeof(IToolSourceService), ProviderId = "fixture.regression.tool.source")]
public sealed partial class ToolSourceProvider(Func<string, Type, object?[], CancellationToken, object> invoke) : IToolSourceService
{
    private ValueTask<T> Reply<T>(string operation, object?[] arguments, CancellationToken token) => (ValueTask<T>)invoke(operation, typeof(T), arguments, token);
    public ValueTask<AgentToolSourceDescriptor> DescribeAsync(CancellationToken token = default) => Reply<AgentToolSourceDescriptor>(nameof(DescribeAsync), [], token);
    public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(AgentToolSourceContext context, CancellationToken token = default) => Reply<IReadOnlyList<AgentToolDescriptor>>(nameof(ListToolsAsync), [context], token);
    public ValueTask<AgentToolReadiness?> GetReadinessAsync(CYounics_Ai_Agent_Tool_Source_ToolSourceGetReadinessRequest request, CancellationToken token = default) => Reply<AgentToolReadiness?>(nameof(GetReadinessAsync), [request], token);
    public ValueTask<AgentToolResult> ExecuteAsync(CYounics_Ai_Agent_Tool_Source_ToolSourceExecuteRequest request, CancellationToken token = default) => Reply<AgentToolResult>(nameof(ExecuteAsync), [request], token);
    public ValueTask<AgentToolResult?> ValidateArgumentsAsync(AgentToolRequest request, CancellationToken token = default) => Reply<AgentToolResult?>(nameof(ValidateArgumentsAsync), [request], token);
    public ValueTask<AgentToolResult?> PreflightAsync(CYounics_Ai_Agent_Tool_Source_ToolSourcePreflightRequest request, CancellationToken token = default) => Reply<AgentToolResult?>(nameof(PreflightAsync), [request], token);
    public ValueTask<AgentPermissionRequest?> PermissionAsync(CYounics_Ai_Agent_Tool_Source_ToolSourcePermissionRequest request, CancellationToken token = default) => Reply<AgentPermissionRequest?>(nameof(PermissionAsync), [request], token);
    public ValueTask<AgentToolPresentation?> PresentationAsync(AgentToolPresentationRequest request, CancellationToken token = default) => Reply<AgentToolPresentation?>(nameof(PresentationAsync), [request], token);
}
