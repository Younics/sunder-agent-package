using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.Tests;

// Owns a real generated provider/consumer lease for component tests. The source,
// selected execution target, transient authority and cancellation cross RPC.
internal sealed class GeneratedToolSourceProbe : IDisposable
{
    private readonly AgentRpcLease<IAgentToolSource> _lease;
    private readonly AgentRpcLease<IAgentPromptContextContributor>? _prompt;
    private readonly AgentRpcLease<IAgentSessionDataCleaner>? _cleaner;

    internal GeneratedToolSourceProbe(RegressionTestExtensionCatalog catalog, object source, string packageId,
        bool withPromptContext = false, bool withSessionCleaner = false)
    {
        _lease = catalog.AcquireProvider(RegressionTestBindings.ToolSources, source, packageId);
        try
        {
            if (withPromptContext) _prompt = catalog.AcquireProvider(RegressionTestBindings.PromptContextContributors, source, packageId);
            if (withSessionCleaner) _cleaner = catalog.AcquireProvider(RegressionTestBindings.SessionCleaners, source, packageId);
        }
        catch { _prompt?.Dispose(); _lease.Dispose(); throw; }
    }

    internal ValueTask<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, AgentToolRequest request,
        CancellationToken token = default) => _lease.Service.ExecuteAsync(context, request, token);

    internal ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(AgentToolSourceContext context, CancellationToken token = default)
        => _lease.Service.ListToolsAsync(context, token);

    internal ValueTask<AgentToolReadiness?> GetReadinessAsync(string toolId, AgentToolSourceContext context, CancellationToken token = default)
        => _lease.Service.GetReadinessAsync(toolId, context, token);

    internal ValueTask<AgentPermissionRequest?> BuildPermissionRequestAsync(AgentToolExecutionContext context, AgentToolRequest request,
        CancellationToken token = default) => ((IAgentPermissionAwareToolSource)_lease.Service).BuildPermissionRequestAsync(context, request, token);

    internal AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request)
        => ((IAgentToolPresentationResolver)_lease.Service).ResolveToolPresentation(request);

    internal ValueTask<AgentToolResult?> PreflightExecutionAsync(AgentToolExecutionContext context, AgentToolRequest request,
        CancellationToken token = default) => ((IAgentToolExecutionPreflightSource)_lease.Service).PreflightExecutionAsync(context, request, token);

    internal ValueTask<AgentPromptContextContribution?> ContributeContextAsync(AgentPromptContextRequest request, CancellationToken token = default)
        => (_prompt ?? throw new InvalidOperationException("This probe has no prompt-context provider.")).Service.ContributeContextAsync(request, token);

    internal ValueTask AcknowledgePromptContextAsync(AgentPromptContextReceipt receipt, CancellationToken token = default)
        => ((IAgentPromptContextAcknowledgmentSink)(_prompt ?? throw new InvalidOperationException("This probe has no prompt-context provider.")).Service)
            .AcknowledgePromptContextAsync(receipt, token);

    internal void DeleteSessionData(Guid sessionId)
        => (_cleaner ?? throw new InvalidOperationException("This probe has no session cleaner.")).Service.DeleteSessionData(sessionId);

    public void Dispose()
    {
        try { _cleaner?.Dispose(); }
        finally { try { _prompt?.Dispose(); } finally { _lease.Dispose(); } }
    }
}
