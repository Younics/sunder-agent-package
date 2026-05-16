using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Mcp.Services;

namespace Sunder.Package.Agent.Mcp;

public sealed class McpToolSource(
    McpServerCatalogService serverCatalogService,
    McpClientConnectionManager connectionManager) : IAgentNativeToolSource, IAgentProfileSelectableCapabilityProvider, IAgentProfileSelectableCapabilityChangeNotifier
{
    private readonly McpServerCatalogService _serverCatalogService = serverCatalogService;
    private readonly McpClientConnectionManager _connectionManager = connectionManager;

    public string SourceId => "mcp";

    public string DisplayName => "Model Context Protocol";

    public string SourceKind => "mcp";

    public string ProviderId => SourceId;

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

    public async ValueTask<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListCapabilitiesAsync(
        AgentProfileSelectableCapabilityRequest request,
        CancellationToken cancellationToken = default)
    {
        var servers = await ListConfiguredServersAsync(cancellationToken);
        return servers
            .Select(server => new AgentProfileSelectableCapabilityDescriptor(
                AgentProfileSelectableCapabilityKinds.ToolGroup,
                server.ServerId,
                SourceId,
                server.DisplayName,
                server.Description,
                server.StatusText,
                SourceDisplayName: DisplayName,
                GroupId: SourceId,
                GroupDisplayName: "MCP Servers",
                GroupDescription: "Configured Model Context Protocol servers.",
                GroupSortOrder: 20))
            .ToArray();
    }

    public async ValueTask<IReadOnlyList<AgentMcpServerDescriptor>> ListConfiguredServersAsync(CancellationToken cancellationToken = default)
    {
        var servers = await _serverCatalogService.ListServersAsync(cancellationToken);
        return servers.Select(server => new AgentMcpServerDescriptor(
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

    public async ValueTask<IReadOnlyList<AgentRuntimeTool>> ListRuntimeToolsAsync(
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
    {
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

        var sessionId = context.SessionId.Value;
        var servers = await _serverCatalogService.ListServersAsync(cancellationToken);
        var runtimeTools = new List<AgentRuntimeTool>();
        foreach (var server in servers.Where(server => server.IsEnabled && enabledServerIds.Contains(server.ServerId)))
        {
            var discoveryTimeoutMilliseconds = McpTimeoutResolver.ResolveDiscoveryTimeoutMilliseconds(server);
            var cachedTools = _connectionManager.GetCachedTools(sessionId, server);
            if (cachedTools is not null)
            {
                runtimeTools.AddRange(cachedTools.Select(tool => ToRuntimeTool(server, tool)));
                continue;
            }

            var headers = _serverCatalogService.GetHeaders(server);
            var environmentVariables = _serverCatalogService.GetEnvironmentVariables(server);
            var tools = await _connectionManager.GetToolsAsync(
                sessionId,
                server,
                headers,
                environmentVariables,
                discoveryTimeoutMilliseconds,
                cancellationToken);
            if (tools.Count == 0)
            {
                QueueBackgroundRefresh(sessionId, server, headers, environmentVariables, discoveryTimeoutMilliseconds);
            }

            runtimeTools.AddRange(tools.Select(tool => ToRuntimeTool(server, tool)));
        }

        return runtimeTools;
    }

    public async ValueTask<AgentToolReadiness?> GetReadinessAsync(
        string toolId,
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.SessionId is null)
        {
            return new AgentToolReadiness(toolId, AgentToolReadinessStatus.Failed, "MCP tools require an active session.");
        }

        var server = await FindServerForToolAsync(toolId, cancellationToken);
        if (server is null)
        {
            return null;
        }

        var sessionId = context.SessionId.Value;
        var tools = _connectionManager.GetCachedTools(sessionId, server);
        if (tools is null)
        {
            var headers = _serverCatalogService.GetHeaders(server);
            var environmentVariables = _serverCatalogService.GetEnvironmentVariables(server);
            var discoveryTimeoutMilliseconds = McpTimeoutResolver.ResolveDiscoveryTimeoutMilliseconds(server);
            tools = await _connectionManager.GetToolsAsync(
                sessionId,
                server,
                headers,
                environmentVariables,
                discoveryTimeoutMilliseconds,
                cancellationToken);
            if (tools.Count == 0)
            {
                QueueBackgroundRefresh(sessionId, server, headers, environmentVariables, discoveryTimeoutMilliseconds);
            }
        }

        return tools.Any(tool => string.Equals(BuildPrefixedToolId(server, tool.Name), toolId, StringComparison.OrdinalIgnoreCase))
            ? new AgentToolReadiness(toolId, AgentToolReadinessStatus.Ready, $"MCP server '{server.DisplayName}' is connected.")
            : new AgentToolReadiness(toolId, AgentToolReadinessStatus.Failed, $"MCP server '{server.DisplayName}' is unavailable or did not expose '{toolId}'.");
    }

    public async ValueTask<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        if (context.SessionId is null)
        {
            return new AgentToolResult(
                request.ToolId,
                "MCP tools require an active session.",
                Content: "### MCP tool unavailable\n\nMCP tools require an active session.",
                IsError: true,
                ErrorCode: "mcp-session-required");
        }

        var server = await FindServerForToolAsync(request.ToolId, cancellationToken);
        if (server is null)
        {
            return new AgentToolResult(
                request.ToolId,
                $"MCP tool '{request.ToolId}' is not configured.",
                Content: $"### MCP tool unavailable\n\nTool '{request.ToolId}' is not configured.",
                IsError: true,
                ErrorCode: "mcp-tool-not-found");
        }

        if (!TryDeserializeArguments(request.ArgumentsJson, out var arguments, out var argumentError))
        {
            return new AgentToolResult(
                request.ToolId,
                argumentError!,
                Content: $"### MCP tool failed\n\n{argumentError}",
                IsError: true,
                ErrorCode: "mcp-arguments-invalid");
        }

        try
        {
            var discoveryTimeoutMilliseconds = McpTimeoutResolver.ResolveDiscoveryTimeoutMilliseconds(server);
            var client = await _connectionManager.GetClientAsync(
                context.SessionId.Value,
                server,
                _serverCatalogService.GetHeaders(server),
                _serverCatalogService.GetEnvironmentVariables(server),
                discoveryTimeoutMilliseconds,
                cancellationToken);
            if (client is null)
            {
                return new AgentToolResult(
                    request.ToolId,
                    $"MCP server '{server.DisplayName}' is unavailable.",
                    Content: $"### MCP server unavailable\n\nServer '{server.DisplayName}' could not be reached.",
                    IsError: true,
                    ErrorCode: "mcp-server-unavailable");
            }

            var rawToolName = request.ToolId[(server.Name.Length + 1)..];
            using var toolTimeoutScope = CreateTimeoutScope(McpTimeoutResolver.ResolveToolTimeoutMilliseconds(server), cancellationToken);
            var result = await client.CallToolAsync(
                rawToolName,
                arguments,
                progress: null,
                options: null,
                cancellationToken: toolTimeoutScope.Token);

            var structuredPayloadJson = result.StructuredContent is JsonElement structured && structured.ValueKind != JsonValueKind.Undefined
                ? structured.GetRawText()
                : null;
            var content = !string.IsNullOrWhiteSpace(structuredPayloadJson)
                ? structuredPayloadJson
                : result.Content is { Count: > 0 }
                    ? JsonSerializer.Serialize(result.Content)
                    : null;

            return new AgentToolResult(
                request.ToolId,
                result.IsError == true ? $"MCP tool '{request.ToolId}' returned an error." : $"MCP tool '{request.ToolId}' completed.",
                Content: content,
                StructuredPayloadJson: structuredPayloadJson,
                WasTruncated: false,
                IsError: result.IsError == true,
                ErrorCode: result.IsError == true ? "mcp-tool-error" : null,
                BackendId: $"mcp:{server.Name}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AgentToolResult(
                request.ToolId,
                ex.Message,
                Content: $"### MCP tool failed\n\n{ex.Message}",
                IsError: true,
                ErrorCode: "mcp-tool-execution");
        }
    }

    private async Task<ConfiguredMcpServerRecord?> FindServerForToolAsync(string toolId, CancellationToken cancellationToken)
    {
        var servers = await _serverCatalogService.ListServersAsync(cancellationToken);
        return servers.FirstOrDefault(server => server.IsEnabled && toolId.StartsWith(server.Name + "_", StringComparison.OrdinalIgnoreCase));
    }

    private void QueueBackgroundRefresh(
        Guid sessionId,
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? effectiveTimeoutMilliseconds)
    {
        var refreshTimeoutMilliseconds = McpTimeoutResolver.ResolveBackgroundRefreshTimeoutMilliseconds(effectiveTimeoutMilliseconds);
        _connectionManager.RefreshToolsInBackground(sessionId, server, headers, environmentVariables, refreshTimeoutMilliseconds);
    }

    private static TimeoutScope CreateTimeoutScope(int? timeoutMilliseconds, CancellationToken cancellationToken)
    {
        if (timeoutMilliseconds is not > 0)
        {
            return new TimeoutScope(cancellationToken, null);
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeoutMilliseconds.Value);
        return new TimeoutScope(source.Token, source);
    }

    private static IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> GetSelectableCapabilityAssignments(AgentProfileRecord profile)
        => profile.SelectableCapabilityAssignments ?? [];

    private static AgentToolDescriptor ToDescriptor(ConfiguredMcpServerRecord server, McpClientTool tool)
        => new(
            BuildPrefixedToolId(server, tool.Name),
            string.IsNullOrWhiteSpace(tool.Title) ? $"{server.DisplayName}: {tool.Name}" : $"{server.DisplayName}: {tool.Title}",
            string.IsNullOrWhiteSpace(tool.Description)
                ? $"MCP tool '{tool.Name}' from '{server.DisplayName}'."
                : $"[{server.DisplayName}] {tool.Description}",
            IsReadOnly: false,
            RequiresNetwork: server.TransportType == ConfiguredMcpTransportType.HttpSse,
            ArgumentsJsonSchema: SerializeSchema(tool.JsonSchema),
            SourceKind: "mcp",
            SourceId: server.ServerId,
            SourceDisplayName: server.DisplayName,
            SelectionScope: AgentToolSelectionScope.Group,
            SelectionGroupId: server.ServerId,
            SelectionGroupDisplayName: server.DisplayName,
            SelectionGroupDescription: server.Description);

    private static AgentRuntimeTool ToRuntimeTool(ConfiguredMcpServerRecord server, McpClientTool tool)
    {
        var descriptor = ToDescriptor(server, tool);
        var declaration = tool
            .WithName(descriptor.ToolId)
            .WithDescription(descriptor.Description);
        return new AgentRuntimeTool(descriptor, declaration);
    }

    private static string BuildPrefixedToolId(ConfiguredMcpServerRecord server, string rawToolName)
        => $"{server.Name}_{rawToolName}";

    private static string? SerializeSchema(object? schema)
        => schema is null ? null : JsonSerializer.Serialize(schema);

    private static bool TryDeserializeArguments(string argumentsJson, out IReadOnlyDictionary<string, object?> arguments, out string? error)
    {
        arguments = new Dictionary<string, object?>();
        if (!AgentToolArgumentObject.TryParse(argumentsJson, out var parsedArguments, out error))
        {
            error = $"Invalid MCP tool arguments: {error ?? "arguments were empty or invalid."}";
            return false;
        }

        arguments = parsedArguments!.ToDictionary();
        return true;
    }

    private sealed class TimeoutScope(CancellationToken token, CancellationTokenSource? source) : IDisposable
    {
        public CancellationToken Token { get; } = token;

        public void Dispose() => source?.Dispose();
    }
}
