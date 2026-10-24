using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;

namespace Sunder.Package.Agent.Tests;

internal interface IRegressionTestArgumentValidator
{
    ValueTask<AgentToolResult?> ValidateArgumentsAsync(AgentToolRequest request, CancellationToken cancellationToken = default);
}

// A local domain collaborator implementing the actual generated service signature.
// There is no request envelope, JSON conversion, endpoint or call-scope here.
internal sealed class RegressionTestToolProvider(IAgentToolSource source) : AgentServices.IToolSourceService
{
    public ValueTask<AgentToolSourceDescriptor> DescribeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentToolSourceDescriptor(source.SourceId, source.DisplayName, source.SourceKind));
    }

    public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(AgentToolSourceContext context, CancellationToken cancellationToken = default)
        => source.ListToolsAsync(context, cancellationToken);
    public ValueTask<AgentToolReadiness?> GetReadinessAsync(string toolId, AgentToolSourceContext context, CancellationToken cancellationToken = default)
        => source.GetReadinessAsync(toolId, context, cancellationToken);
    public ValueTask<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken cancellationToken = default)
        => source.ExecuteAsync(context, request, cancellationToken);
    public ValueTask<AgentToolResult?> ValidateArgumentsAsync(AgentToolRequest request, CancellationToken cancellationToken = default)
        => source is IRegressionTestArgumentValidator validator
            ? validator.ValidateArgumentsAsync(request, cancellationToken)
            : ValueTask.FromResult<AgentToolResult?>(null);
    public ValueTask<AgentToolResult?> PreflightAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken cancellationToken = default)
        => source is IAgentToolExecutionPreflightSource preflight
            ? preflight.PreflightExecutionAsync(context, request, cancellationToken)
            : ValueTask.FromResult<AgentToolResult?>(null);
    public ValueTask<AgentPermissionRequest?> PermissionAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken cancellationToken = default)
        => source is IAgentPermissionAwareToolSource permission
            ? permission.BuildPermissionRequestAsync(context, request, cancellationToken)
            : ValueTask.FromResult<AgentPermissionRequest?>(null);
    public ValueTask<AgentToolPresentation?> PresentationAsync(AgentToolPresentationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(source is IAgentToolPresentationResolver presentation ? presentation.ResolveToolPresentation(request) : null);
    }
}

internal sealed class RegressionTestSingleToolSource(IAgentTool tool) : IAgentToolSource, IAgentPermissionAwareToolSource, IAgentToolPresentationResolver
{
    public string SourceId => "installed-packages";
    public string DisplayName => "Installed packages";
    public string SourceKind => "test";
    public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(AgentToolSourceContext context, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<IReadOnlyList<AgentToolDescriptor>>([tool.Descriptor]); }
    public async ValueTask<AgentToolReadiness?> GetReadinessAsync(string toolId, AgentToolSourceContext context, CancellationToken token = default)
        => Matches(toolId) ? await tool.GetReadinessAsync(token).ConfigureAwait(false) : null;
    public ValueTask<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken token = default)
        => Matches(request.ToolId) ? tool.ExecuteAsync(context, request, token)
            : ValueTask.FromResult(new AgentToolResult(request.ToolId, "Tool not found.", IsError: true, ErrorCode: "tool-not-found", BackendId: SourceId));
    public ValueTask<AgentPermissionRequest?> BuildPermissionRequestAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken token = default)
        => Matches(request.ToolId) && tool is IAgentPermissionAwareTool permission ? permission.BuildPermissionRequestAsync(context, request, token)
            : ValueTask.FromResult<AgentPermissionRequest?>(null);
    public AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request)
        => Matches(request.ToolId) && tool is IAgentToolPresentationResolver presentation ? presentation.ResolveToolPresentation(request) : null;
    private bool Matches(string id) => string.Equals(tool.Descriptor.ToolId, id, StringComparison.OrdinalIgnoreCase);
}
