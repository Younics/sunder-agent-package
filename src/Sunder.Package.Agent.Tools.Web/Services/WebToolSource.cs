using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tools.Web.Services;

internal sealed class WebToolSource(WebFetchTool fetch, WebSearchTool search)
    : IAgentToolSource, IAgentPermissionAwareToolSource, IAgentToolPresentationResolver, IAsyncDisposable
{
    private readonly IAgentTool[] _tools = [fetch, search];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _stop;
    private bool _stopped;
    private int _calls;

    public string SourceId => "web";
    public string DisplayName => "Web Tools";
    public string SourceKind => "web";
    public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(AgentToolSourceContext context, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<IReadOnlyList<AgentToolDescriptor>>(_tools.Select(tool => tool.Descriptor).ToArray()); }
    public ValueTask<AgentToolReadiness?> GetReadinessAsync(string toolId, AgentToolSourceContext context, CancellationToken cancellationToken = default)
        => InvokeAsync<AgentToolReadiness?>(async token => Find(toolId) is { } tool ? await tool.GetReadinessAsync(token).ConfigureAwait(false) : null, cancellationToken);
    public ValueTask<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken cancellationToken = default)
        => InvokeAsync(token => Find(request.ToolId) is { } tool ? tool.ExecuteAsync(context, request, token)
            : ValueTask.FromResult(new AgentToolResult(request.ToolId, "Tool not found.", IsError: true, ErrorCode: "tool-not-found", BackendId: SourceId)), cancellationToken);
    public ValueTask<AgentPermissionRequest?> BuildPermissionRequestAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken cancellationToken = default)
        => InvokeAsync(token => Find(request.ToolId) is IAgentPermissionAwareTool permission ? permission.BuildPermissionRequestAsync(context, request, token)
            : ValueTask.FromResult<AgentPermissionRequest?>(null), cancellationToken);
    public AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request)
        => Find(request.ToolId) is IAgentToolPresentationResolver presentation ? presentation.ResolveToolPresentation(request) : null;

    private IAgentTool? Find(string id) => _tools.FirstOrDefault(tool => string.Equals(tool.Descriptor.ToolId, id, StringComparison.OrdinalIgnoreCase));
    private async ValueTask<T> InvokeAsync<T>(Func<CancellationToken, ValueTask<T>> operation, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        lock (_gate) { ObjectDisposedException.ThrowIf(_stopped, this); _calls++; }
        try
        {
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(caller, _lifetime.Token);
            var value = await operation(invocation.Token).ConfigureAwait(false);
            invocation.Token.ThrowIfCancellationRequested();
            return value;
        }
        finally { lock (_gate) if (--_calls == 0 && _stopped) _drained.TrySetResult(); }
    }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_stop is not null) return new(_stop);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _stop = completion.Task; _stopped = true;
            if (_calls == 0) _drained.TrySetResult();
        }
        _ = StopAsync(completion); return new(completion.Task);
    }
    private async Task StopAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            try { await _lifetime.CancelAsync().ConfigureAwait(false); }
            finally { await _drained.Task.ConfigureAwait(false); }
        }
        catch (Exception error) { failure = error; }
        finally { _lifetime.Dispose(); }
        if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
    }
}
