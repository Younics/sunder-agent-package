using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using IExecutionTargetService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Execution_Target.IExecutionTargetService;
using IToolSourceService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Tool_Source.IToolSourceService;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Tools.Web.Services;

[SunderServiceProvider(typeof(IToolSourceService), ProviderId = "web.tools")]
[SunderRuntimeUse(typeof(IExecutionTargetService), Actions = ["discover"])]
internal sealed partial class WebToolSource(WebFetchTool fetch, WebSearchTool search) : IToolSourceService, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _stop;
    private bool _stopped;
    private int _calls;

    public string SourceId => "web";
    public string DisplayName => "Web Tools";
    public string SourceKind => "web";
    public ValueTask<AgentToolSourceDescriptor> DescribeAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(new AgentToolSourceDescriptor { SourceId = SourceId, DisplayName = DisplayName, SourceKind = SourceKind }); }
    public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(AgentToolSourceContext context, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<IReadOnlyList<AgentToolDescriptor>>([fetch.Descriptor, search.Descriptor]); }
    public ValueTask<AgentToolReadiness?> GetReadinessAsync(CYounics_Ai_Agent_Tool_Source_ToolSourceGetReadinessRequest request, CancellationToken cancellationToken = default)
        => InvokeAsync<AgentToolReadiness?>(async token => request.ToolId.ToLowerInvariant() switch
        {
            "web_fetch" => await fetch.GetReadinessAsync(token).ConfigureAwait(false),
            "web_search" => await search.GetReadinessAsync(token).ConfigureAwait(false),
            _ => null,
        }, cancellationToken);
    public ValueTask<AgentToolResult?> PreflightAsync(CYounics_Ai_Agent_Tool_Source_ToolSourcePreflightRequest request, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<AgentToolResult?>(null); }
    public ValueTask<AgentToolResult> ExecuteAsync(CYounics_Ai_Agent_Tool_Source_ToolSourceExecuteRequest request, CancellationToken cancellationToken = default)
        => InvokeAsync(token => request.Request.ToolId.ToLowerInvariant() switch
        {
            "web_fetch" => fetch.ExecuteAsync(request.Context, request.Request, token),
            "web_search" => search.ExecuteAsync(request.Context, request.Request, token),
            _ => ValueTask.FromResult(new AgentToolResult
            {
                ToolId = request.Request.ToolId, Summary = "Tool not found.", Content = null, StructuredPayloadJson = null,
                Sources = null, WasTruncated = false, IsError = true, ErrorCode = "tool-not-found", BackendId = SourceId,
                PresentationPayloadJson = null, RequiresPromptContextRefresh = false,
            }),
        }, cancellationToken);
    public ValueTask<AgentPermissionRequest?> PermissionAsync(CYounics_Ai_Agent_Tool_Source_ToolSourcePermissionRequest request, CancellationToken cancellationToken = default)
        => InvokeAsync(_ => ValueTask.FromResult<AgentPermissionRequest?>(null), cancellationToken);
    public ValueTask<AgentToolPresentation?> PresentationAsync(AgentToolPresentationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(request.ToolId.ToLowerInvariant() switch
        {
            "web_fetch" => fetch.ResolveToolPresentation(request),
            "web_search" => search.ResolveToolPresentation(request),
            _ => null,
        });
    }

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
