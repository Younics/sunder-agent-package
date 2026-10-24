using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using ISelectableCapabilityProviderService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Selectable_Capability_Provider.ISelectableCapabilityProviderService;
using IToolExecutionObservedService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Tool_Execution_Observed.IToolExecutionObservedService;
using IToolSourceService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Tool_Source.IToolSourceService;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Mcp;

[SunderServiceProvider(typeof(IToolSourceService), ProviderId = "mcp.tools")]
[SunderServiceProvider(typeof(IToolExecutionObservedService), ProviderId = "mcp.tools.observed")]
[SunderServiceProvider(typeof(ISelectableCapabilityProviderService), ProviderId = "mcp.selectable.capabilities")]
public sealed partial class McpToolSource(
    McpServerCatalogService serverCatalogService,
    McpClientConnectionManager connectionManager,
    McpConfigurationCoordinator? configurationCoordinator = null) :
    IToolSourceService, IToolExecutionObservedService, ISelectableCapabilityProviderService
{
    private readonly McpServerCatalogService _serverCatalogService = serverCatalogService;
    private readonly McpClientConnectionManager _connectionManager = connectionManager;
    private readonly McpConfigurationCoordinator? _configurationCoordinator = configurationCoordinator;
    public string SourceId => "mcp";

    public string DisplayName => "Model Context Protocol";

    public string SourceKind => "mcp";

    public string ProviderId => SourceId;

    public ValueTask<AgentToolSourceDescriptor> DescribeAsync(CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new AgentToolSourceDescriptor { SourceId = SourceId, DisplayName = DisplayName, SourceKind = SourceKind }); }

    ValueTask<string> IToolExecutionObservedService.DescribeAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(SourceId); }

    ValueTask<AgentServiceIdentity> ISelectableCapabilityProviderService.DescribeAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new AgentServiceIdentity { Id = ProviderId, DisplayName = DisplayName }); }

    public ValueTask<AgentToolPresentation?> PresentationAsync(AgentToolPresentationRequest request, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(McpToolPresentationAdapter.Resolve(request)); }

    public event Action? SelectableCapabilitiesChanged
    {
        add
        {
            _serverCatalogService.ServersChanged += value;
            _connectionManager.StatusChanged += value;
        }
        remove
        {
            _serverCatalogService.ServersChanged -= value;
            _connectionManager.StatusChanged -= value;
        }
    }

    public async ValueTask<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListAsync(
        AgentProfileRecord? profile,
        CancellationToken cancellationToken = default)
    {
        var servers = await ListConfiguredServersAsync(cancellationToken);
        return servers
            .Select(server => new AgentProfileSelectableCapabilityDescriptor
            {
                Kind = AgentProfileSelectableCapabilityKinds.ToolGroup, CapabilityId = server.ServerId, SourceId = SourceId,
                DisplayName = server.DisplayName, Description = server.Description, StatusText = server.StatusText,
                IsSelectable = true, SourceDisplayName = DisplayName, GroupId = SourceId, GroupDisplayName = "MCP Servers",
                GroupDescription = "Configured Model Context Protocol servers.", GroupSortOrder = 20,
            })
            .ToArray();
    }

    private async ValueTask<IReadOnlyList<McpServerSummary>> ListConfiguredServersAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConfigurationInitializedAsync(cancellationToken);
        var servers = await _serverCatalogService.ListServersAsync(cancellationToken);
        return servers.Select(server => new McpServerSummary(
                server.ServerId,
                server.DisplayName,
                server.Description,
                _connectionManager.GetStatus(server).Message))
            .ToArray();
    }

    public async ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
        => (await ListRuntimeToolsAsync(context, cancellationToken))
            .Select(tool => tool.Descriptor)
            .ToArray();

    internal async ValueTask<IReadOnlyList<McpRuntimeTool>> ListRuntimeToolsAsync(
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
    {
        await EnsureConfigurationInitializedAsync(cancellationToken);
        if (context.Profile is null || context.SessionId is null)
        {
            return [];
        }

        var enabledServerIds = GetSelectableCapabilityAssignments(context.Profile)
            .Where(assignment => string.Equals(assignment.Kind, AgentProfileSelectableCapabilityKinds.ToolGroup, StringComparison.OrdinalIgnoreCase)
                                 && (string.IsNullOrWhiteSpace(assignment.SourceId)
                                     || string.Equals(assignment.SourceId, SourceId, StringComparison.OrdinalIgnoreCase)))
            .Select(assignment => assignment.CapabilityId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (enabledServerIds.Count == 0)
        {
            return [];
        }

        var servers = await _serverCatalogService.ListServersAsync(cancellationToken);
        var runtimeTools = new List<McpRuntimeTool>();
        foreach (var server in servers.Where(server => server.IsEnabled && enabledServerIds.Contains(server.ServerId)))
        {
            var discoveryTimeoutMilliseconds = McpTimeoutResolver.ResolveDiscoveryTimeoutMilliseconds(server);
            var cachedTools = _connectionManager.GetCachedTools(server);
            if (cachedTools is not null)
            {
                runtimeTools.AddRange(cachedTools.Select(tool => ToRuntimeTool(server, tool)));
                continue;
            }

            var headers = await _serverCatalogService.GetHeadersAsync(server, cancellationToken);
            var environmentVariables = await _serverCatalogService.GetEnvironmentVariablesAsync(server, cancellationToken);
            var tools = await _connectionManager.GetToolsAsync(
                server,
                headers,
                environmentVariables,
                discoveryTimeoutMilliseconds,
                McpConnectionScope.For(context.SessionId, context.Workspace?.WorkspaceId),
                cancellationToken);
            if (tools.Count == 0)
            {
                QueueBackgroundRefresh(server, headers, environmentVariables, discoveryTimeoutMilliseconds, McpConnectionScope.For(context.SessionId, context.Workspace?.WorkspaceId));
            }

            runtimeTools.AddRange(tools.Select(tool => ToRuntimeTool(server, tool)));
        }

        return runtimeTools;
    }

    public async ValueTask<AgentToolReadiness?> GetReadinessAsync(
        CYounics_Ai_Agent_Tool_Source_ToolSourceGetReadinessRequest request,
        CancellationToken cancellationToken = default)
    {
        var (toolId, context) = (request.ToolId, request.Context);
        await EnsureConfigurationInitializedAsync(cancellationToken);
        if (context.SessionId is null)
        {
            return new() { ToolId = toolId, Status = AgentToolReadinessStatus.Failed, Message = "MCP tools require an active session." };
        }

        var resolved = await ResolveToolAsync(toolId, cancellationToken);
        if (resolved is null)
        {
            return null;
        }

        var server = resolved.Server;
        var tools = _connectionManager.GetCachedTools(server);
        if (tools is null)
        {
            var headers = await _serverCatalogService.GetHeadersAsync(server, cancellationToken);
            var environmentVariables = await _serverCatalogService.GetEnvironmentVariablesAsync(server, cancellationToken);
            var discoveryTimeoutMilliseconds = McpTimeoutResolver.ResolveDiscoveryTimeoutMilliseconds(server);
            tools = await _connectionManager.GetToolsAsync(
                server,
                headers,
                environmentVariables,
                discoveryTimeoutMilliseconds,
                McpConnectionScope.For(context.SessionId, context.Workspace?.WorkspaceId),
                cancellationToken);
            if (tools.Count == 0)
            {
                QueueBackgroundRefresh(server, headers, environmentVariables, discoveryTimeoutMilliseconds, McpConnectionScope.For(context.SessionId, context.Workspace?.WorkspaceId));
            }
        }

        var ready = tools.Any(tool => string.Equals(tool.Name, resolved.ToolName, StringComparison.OrdinalIgnoreCase));
        return new() { ToolId = McpToolIdentity.CreateStable(server.ServerId, resolved.ToolName), Status = ready ? AgentToolReadinessStatus.Ready : AgentToolReadinessStatus.Failed,
            Message = ready ? $"MCP server '{server.DisplayName}' is connected." : $"MCP server '{server.DisplayName}' is unavailable or did not expose '{toolId}'." };
    }

    public ValueTask<AgentToolResult> ExecuteAsync(CYounics_Ai_Agent_Tool_Source_ToolSourceExecuteRequest request, CancellationToken token = default)
        => ExecuteCoreAsync(request.Context, request.Request, null, token);

    private async ValueTask<AgentToolResult> ExecuteCoreAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        Func<CancellationToken, ValueTask>? dispatchStarted,
        CancellationToken cancellationToken = default)
    {
        if (context.SessionId is null)
        {
            return Result(
                request.ToolId,
                "MCP tools require an active session.",
                "### MCP tool unavailable\n\nMCP tools require an active session.", "mcp-session-required");
        }

        if (!TryDeserializeArguments(
                request.ArgumentsJson,
                out var arguments,
                out var agentTimeoutSeconds,
                out var argumentError))
        {
            var invalidLabel = McpToolCardPresentation.TryParseStableToolId(
                request.ToolId,
                out _,
                out var invalidToolName)
                ? McpToolCardPresentation.HumanizeToolName(invalidToolName)
                : "MCP Tool";
            return Result(
                request.ToolId,
                McpToolCardPresentation.InvalidArgumentsSummary(invalidLabel),
                $"### MCP tool failed\n\n{argumentError}", "mcp-arguments-invalid");
        }

        var resolved = await ResolveToolAsync(request.ToolId, cancellationToken);
        if (resolved is null)
        {
            var missingLabel = McpToolCardPresentation.TryParseStableToolId(
                request.ToolId,
                out _,
                out var missingToolName)
                ? McpToolCardPresentation.HumanizeToolName(missingToolName)
                : "MCP Tool";
            return Result(
                request.ToolId,
                McpToolCardPresentation.NotConfiguredSummary(missingLabel),
                $"### MCP tool unavailable\n\nTool '{request.ToolId}' is not configured.", "mcp-tool-not-found");
        }

        var server = resolved.Server;
        var canonicalToolId = McpToolIdentity.CreateStable(server.ServerId, resolved.ToolName);
        var displayLabel = ResolveDisplayLabel(server, resolved.ToolName);
        var backendId = $"mcp:{server.ServerId}";

        var serverTimeoutMilliseconds = McpTimeoutResolver.ResolveToolTimeoutMilliseconds(server);
        var toolTimeout = McpTimeoutResolver.ResolveToolExecutionTimeout(
            serverTimeoutMilliseconds,
            agentTimeoutSeconds);
        TimeoutScope? toolTimeoutScope = null;
        var invocationAwaitingResult = false;
        try
        {
            var discoveryTimeoutMilliseconds = McpTimeoutResolver.ResolveDiscoveryTimeoutMilliseconds(server);
            await using var invocationLease = await _connectionManager.AcquireClientLeaseAsync(
                server,
                await _serverCatalogService.GetHeadersAsync(server, cancellationToken),
                await _serverCatalogService.GetEnvironmentVariablesAsync(server, cancellationToken),
                discoveryTimeoutMilliseconds,
                McpConnectionScope.For(context.SessionId, context.Workspace?.WorkspaceId),
                cancellationToken);
            if (invocationLease?.CanInvokeTools != true)
            {
                return Result(
                    canonicalToolId,
                    McpToolCardPresentation.UnavailableSummary(displayLabel),
                    $"### MCP server unavailable\n\nServer '{server.DisplayName}' could not be reached.", "mcp-server-unavailable", backendId);
            }

            if (dispatchStarted is not null) await dispatchStarted(cancellationToken).ConfigureAwait(false);
            if (toolTimeout is { } timeout)
            {
                toolTimeoutScope = CreateTimeoutScope(timeout.Milliseconds, cancellationToken);
            }
            invocationAwaitingResult = true;
            var result = await invocationLease.CallToolAsync(
                resolved.ToolName,
                arguments,
                toolTimeoutScope?.Token ?? cancellationToken);
            invocationAwaitingResult = false;

            var serializedResult = SerializeResult(result.StructuredContent, result.Content);

            return Result(
                canonicalToolId,
                result.IsError == true
                    ? McpToolCardPresentation.ToolErrorSummary(displayLabel)
                    : McpToolCardPresentation.CompletedSummary(displayLabel, wasTruncated: false),
                serializedResult.Content, result.IsError == true ? "mcp-tool-error" : null, backendId) with
            { StructuredPayloadJson = serializedResult.StructuredPayloadJson };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (invocationAwaitingResult
                                   && ContainsCancellation(ex)
                                   && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The MCP invocation was canceled by the caller.", ex, cancellationToken);
        }
        catch (Exception ex) when (invocationAwaitingResult
                                   && ContainsCancellation(ex)
                                   && toolTimeoutScope?.DidTimeout == true)
        {
            return CreateTimeoutResult(canonicalToolId, displayLabel, backendId, toolTimeout!.Value);
        }
        catch (Exception ex) when (invocationAwaitingResult && ContainsCancellation(ex))
        {
            return CreateAmbiguousResult(canonicalToolId, displayLabel, backendId);
        }
        catch (Exception ex) when (invocationAwaitingResult && IsTransportLoss(ex))
        {
            return CreateAmbiguousResult(canonicalToolId, displayLabel, backendId);
        }
        catch (McpProtocolException ex) when (invocationAwaitingResult && IsDefinitiveProtocolRequestError(ex))
        {
            return Result(
                canonicalToolId,
                McpToolCardPresentation.FailureSummary(displayLabel),
                $"### MCP protocol error\n\n{ex.Message}", "mcp-protocol-error", backendId);
        }
        catch (McpProtocolException) when (invocationAwaitingResult)
        {
            return CreateAmbiguousResult(canonicalToolId, displayLabel, backendId);
        }
        catch (Exception) when (invocationAwaitingResult)
        {
            return CreateAmbiguousResult(canonicalToolId, displayLabel, backendId);
        }
        catch (Exception ex)
        {
            return Result(
                canonicalToolId,
                McpToolCardPresentation.FailureSummary(displayLabel),
                $"### MCP tool failed\n\n{ex.Message}", "mcp-tool-execution", backendId);
        }
        finally
        {
            toolTimeoutScope?.Dispose();
        }
    }

    private static AgentToolResult CreateAmbiguousResult(
        string toolId,
        string displayLabel,
        string backendId)
        => Result(
            toolId,
            McpToolCardPresentation.AmbiguousSummary(displayLabel),
            "### MCP tool outcome is uncertain\n\nThe request was dispatched, but no definitive response was received. External effects may have occurred. Verify current state before retrying, and disclose the uncertain outcome with any later verification.",
            AgentToolResultErrorCodes.ToolOutcomeAmbiguous, backendId);

    private static AgentToolResult CreateTimeoutResult(
        string toolId,
        string displayLabel,
        string backendId,
        McpToolExecutionTimeout timeout)
    {
        var timeoutSource = DescribeTimeoutSource(timeout.Source);
        return Result(
            toolId,
            McpToolCardPresentation.TimeoutSummary(displayLabel, timeout.Milliseconds, timeoutSource),
            $"### MCP tool outcome is uncertain\n\nThe request reached the effective timeout after dispatch. The limit came from {timeoutSource}. External effects may have occurred. Verify current state before retrying, and disclose the timeout with any later verification.",
            AgentToolResultErrorCodes.ToolOutcomeAmbiguous, backendId);
    }

    private static bool IsDefinitiveProtocolRequestError(McpProtocolException exception)
        => exception.ErrorCode is McpErrorCode.ParseError
            or McpErrorCode.InvalidRequest
            or McpErrorCode.MethodNotFound
            or McpErrorCode.InvalidParams;

    private static bool ContainsCancellation(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return true;
        }
        if (exception is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.Any(ContainsCancellation);
        }
        return exception.InnerException is not null && ContainsCancellation(exception.InnerException);
    }

    private static bool IsTransportLoss(Exception exception)
    {
        if (exception is McpProtocolException)
        {
            return exception.InnerException is not null && IsTransportLoss(exception.InnerException);
        }
        if (exception is HttpRequestException or IOException or System.Net.WebSockets.WebSocketException)
        {
            return true;
        }
        if (exception is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.Any(IsTransportLoss);
        }
        return exception.InnerException is not null && IsTransportLoss(exception.InnerException);
    }

    private async Task<ResolvedMcpTool?> ResolveToolAsync(string toolId, CancellationToken cancellationToken)
    {
        await EnsureConfigurationInitializedAsync(cancellationToken);
        if (McpToolIdentity.TryParseStable(toolId, out var serverId, out var toolName))
        {
            var server = await _serverCatalogService.GetServerAsync(serverId, cancellationToken);
            return server?.IsEnabled == true ? new ResolvedMcpTool(server, toolName) : null;
        }

        return null;
    }

    private void QueueBackgroundRefresh(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? effectiveTimeoutMilliseconds,
        McpConnectionScope scope)
    {
        var refreshTimeoutMilliseconds = McpTimeoutResolver.ResolveBackgroundRefreshTimeoutMilliseconds(effectiveTimeoutMilliseconds);
        _connectionManager.RefreshToolsInBackground(server, headers, environmentVariables, refreshTimeoutMilliseconds, scope);
    }

    private Task EnsureConfigurationInitializedAsync(CancellationToken cancellationToken)
        => _configurationCoordinator?.InitializeAsync(cancellationToken) ?? Task.CompletedTask;

    private static TimeoutScope CreateTimeoutScope(int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        var timeoutSource = new CancellationTokenSource(timeoutMilliseconds);
        var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        return new TimeoutScope(linkedSource.Token, timeoutSource, linkedSource);
    }

    private static IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> GetSelectableCapabilityAssignments(AgentProfileRecord profile)
        => profile.SelectableCapabilityAssignments ?? [];

    private McpRuntimeTool ToRuntimeTool(ConfiguredMcpServerRecord server, McpClientTool tool)
    {
        var converted = CreateRuntimeTool(
            server,
            tool.Name,
            tool.Title,
            tool.Description,
            tool.JsonSchema,
            tool.ReturnJsonSchema);
        return converted.RuntimeTool;
    }

    internal static (McpRuntimeTool RuntimeTool, McpToolArgumentSchemaMode ArgumentSchemaMode) CreateRuntimeTool(
        ConfiguredMcpServerRecord server,
        string toolName,
        string? toolTitle,
        string? toolDescription,
        JsonElement argumentsJsonSchema,
        JsonElement? returnJsonSchema)
    {
        var toolId = McpToolIdentity.CreateStable(server.ServerId, toolName);
        var augmentedSchema = McpToolTimeoutMetadata.AugmentSchema(argumentsJsonSchema);
        var descriptor = new AgentToolDescriptor
        {
            ToolId = toolId, DisplayName = McpToolCardPresentation.CreateDisplayLabel(server.DisplayName, toolName, toolTitle),
            Description = string.IsNullOrWhiteSpace(toolDescription)
                ? $"MCP tool '{toolName}' from '{server.DisplayName}'."
                : $"[{server.DisplayName}] {toolDescription}",
            IsReadOnly = false, RequiresNetwork = server.TransportType == ConfiguredMcpTransportType.HttpSse,
            ArgumentsJsonSchema = augmentedSchema.Schema.GetRawText(), SourceKind = "mcp", SourceId = server.ServerId,
            SourceDisplayName = server.DisplayName, SelectionScope = AgentToolSelectionScope.Group, SelectionGroupId = server.ServerId,
            SelectionGroupDisplayName = server.DisplayName, SelectionGroupDescription = server.Description,
            Aliases = [], RuntimeInstructions = null, ActivationRequirement = null,
            Priority = AgentToolPriority.Medium, ConcurrencyMode = AgentToolConcurrencyMode.Sequential,
        };
        return (
            new McpRuntimeTool(
                descriptor,
                AIFunctionFactory.CreateDeclaration(
                    descriptor.ToolId,
                    descriptor.Description,
                    augmentedSchema.Schema,
                    returnJsonSchema)),
            augmentedSchema.Mode);
    }

    private string ResolveDisplayLabel(ConfiguredMcpServerRecord server, string toolName)
    {
        var tool = _connectionManager.GetCachedTools(server)?.FirstOrDefault(item =>
            string.Equals(item.Name, toolName, StringComparison.OrdinalIgnoreCase));
        return McpToolCardPresentation.CreateDisplayLabel(
            server.DisplayName,
            toolName,
            tool?.Title);
    }

    private static bool TryDeserializeArguments(
        string argumentsJson,
        out IReadOnlyDictionary<string, object?> arguments,
        out int? agentTimeoutSeconds,
        out string? error)
    {
        arguments = new Dictionary<string, object?>();
        agentTimeoutSeconds = null;
        if (!AgentToolArgumentObject.TryParse(argumentsJson, out var parsedArguments, out error))
        {
            error = $"Invalid MCP tool arguments: {error ?? "arguments were empty or invalid."}";
            return false;
        }

        return McpToolTimeoutMetadata.TryExtract(
            parsedArguments!,
            out arguments,
            out agentTimeoutSeconds,
            out error);
    }

    private static string DescribeTimeoutSource(McpToolTimeoutSource source)
        => source switch
        {
            McpToolTimeoutSource.ServerConfiguration => "server configuration",
            McpToolTimeoutSource.AgentRequest => "the agent request",
            McpToolTimeoutSource.ServerAndAgent => "both server configuration and the agent request",
            _ => "the effective local limit",
        };

    private static SerializedMcpResult SerializeResult(JsonElement? structuredContent, IList<ContentBlock>? content)
    {
        if (structuredContent is { ValueKind: not JsonValueKind.Undefined } structured)
        {
            var structuredJson = JsonSerializer.Serialize(structured);
            return new SerializedMcpResult(structuredJson, structuredJson);
        }

        if (content is not { Count: > 0 })
        {
            return new SerializedMcpResult(null, null);
        }

        return new SerializedMcpResult(JsonSerializer.Serialize(content.ToArray()), null);
    }

    private sealed class TimeoutScope(
        CancellationToken token,
        CancellationTokenSource? timeoutSource,
        CancellationTokenSource? linkedSource) : IDisposable
    {
        public CancellationToken Token { get; } = token;

        public bool DidTimeout => timeoutSource?.IsCancellationRequested == true;

        public void Dispose()
        {
            linkedSource?.Dispose();
            timeoutSource?.Dispose();
        }
    }

    public async IAsyncEnumerable<AgentObservedToolExecutionEvent> ExecuteAsync(
        CYounics_Ai_Agent_Tool_Execution_Observed_ToolExecutionObservedExecuteRequest request,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask Dispatched(CancellationToken cancellation)
        {
            dispatched.TrySetResult();
            await acknowledged.Task.WaitAsync(cancellation).ConfigureAwait(false);
        }
        var execution = ExecuteCoreAsync(request.Context, request.Request, Dispatched, lifetime.Token).AsTask();
        try
        {
            if (!execution.IsCompleted) await Task.WhenAny(dispatched.Task, execution).ConfigureAwait(false);
            if (dispatched.Task.IsCompletedSuccessfully)
            {
                yield return new AgentToolDispatchedEvent();
                acknowledged.TrySetResult();
            }
            yield return new AgentToolExecutionCompletedEvent { Result = await execution.ConfigureAwait(false) };
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            acknowledged.TrySetCanceled(lifetime.Token);
            try { await execution.ConfigureAwait(false); }
            catch (Exception) when (execution.IsCanceled || execution.IsFaulted) { /* Drain observed or abandoned work. */ }
        }
    }

    public async IAsyncEnumerable<AgentCapabilityCatalogChange> WatchAsync([EnumeratorCancellation] CancellationToken token = default)
    {
        var channel = Channel.CreateBounded<AgentCapabilityCatalogChange>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        long revision = 0;
        void Changed() => channel.Writer.TryWrite(new() { Revision = Interlocked.Increment(ref revision) });
        SelectableCapabilitiesChanged += Changed;
        try
        {
            await foreach (var change in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            { token.ThrowIfCancellationRequested(); yield return change; }
        }
        finally { SelectableCapabilitiesChanged -= Changed; channel.Writer.TryComplete(); }
    }

    public ValueTask<AgentToolResult?> ValidateArgumentsAsync(AgentToolRequest request, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<AgentToolResult?>(null); }
    public ValueTask<AgentToolResult?> PreflightAsync(CYounics_Ai_Agent_Tool_Source_ToolSourcePreflightRequest request, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<AgentToolResult?>(null); }
    public ValueTask<AgentPermissionRequest?> PermissionAsync(CYounics_Ai_Agent_Tool_Source_ToolSourcePermissionRequest request, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<AgentPermissionRequest?>(null); }

    private static AgentToolResult Result(string toolId, string summary, string? content, string? errorCode = null, string? backendId = null) => new()
    {
        ToolId = toolId, Summary = summary, Content = content, ErrorCode = errorCode, IsError = errorCode is not null,
        BackendId = backendId, StructuredPayloadJson = null, Sources = null, WasTruncated = false,
        PresentationPayloadJson = null, RequiresPromptContextRefresh = false,
    };

    internal sealed record McpRuntimeTool(AgentToolDescriptor Descriptor, AIFunctionDeclaration Declaration);
    private sealed record McpServerSummary(string ServerId, string DisplayName, string? Description, string StatusText);
    private sealed record ResolvedMcpTool(ConfiguredMcpServerRecord Server, string ToolName);

    private sealed record SerializedMcpResult(string? Content, string? StructuredPayloadJson);
}
