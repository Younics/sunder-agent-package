using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Mcp.Services;

namespace Sunder.Package.Agent.Mcp;

public sealed class McpToolSource(
    McpServerCatalogService serverCatalogService,
    McpClientConnectionManager connectionManager,
    McpConfigurationCoordinator? configurationCoordinator = null) : IAgentNativeToolSource, IAgentProfileSelectableCapabilityProvider, IAgentProfileSelectableCapabilityChangeNotifier
{
    private readonly McpServerCatalogService _serverCatalogService = serverCatalogService;
    private readonly McpClientConnectionManager _connectionManager = connectionManager;
    private readonly McpConfigurationCoordinator? _configurationCoordinator = configurationCoordinator;

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
        await EnsureConfigurationInitializedAsync(cancellationToken);
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
        var runtimeTools = new List<AgentRuntimeTool>();
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
        string toolId,
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
    {
        await EnsureConfigurationInitializedAsync(cancellationToken);
        if (context.SessionId is null)
        {
            return new AgentToolReadiness(toolId, AgentToolReadinessStatus.Failed, "MCP tools require an active session.");
        }

        var resolved = await ResolveToolAsync(toolId, allowLegacyAlias: true, cancellationToken);
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

        return tools.Any(tool => string.Equals(tool.Name, resolved.ToolName, StringComparison.OrdinalIgnoreCase))
            ? new AgentToolReadiness(McpToolIdentity.CreateStable(server.ServerId, resolved.ToolName), AgentToolReadinessStatus.Ready, $"MCP server '{server.DisplayName}' is connected.")
            : new AgentToolReadiness(McpToolIdentity.CreateStable(server.ServerId, resolved.ToolName), AgentToolReadinessStatus.Failed, $"MCP server '{server.DisplayName}' is unavailable or did not expose '{toolId}'.");
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

        var resolved = await ResolveToolAsync(request.ToolId, allowLegacyAlias: true, cancellationToken);
        if (resolved is null)
        {
            return new AgentToolResult(
                request.ToolId,
                $"MCP tool '{request.ToolId}' is not configured.",
                Content: $"### MCP tool unavailable\n\nTool '{request.ToolId}' is not configured.",
                IsError: true,
                ErrorCode: "mcp-tool-not-found");
        }

        var server = resolved.Server;
        var canonicalToolId = McpToolIdentity.CreateStable(server.ServerId, resolved.ToolName);

        if (!TryDeserializeArguments(request.ArgumentsJson, out var arguments, out var argumentError))
        {
            return new AgentToolResult(
                canonicalToolId,
                argumentError!,
                Content: $"### MCP tool failed\n\n{argumentError}",
                IsError: true,
                ErrorCode: "mcp-arguments-invalid");
        }

        try
        {
            var discoveryTimeoutMilliseconds = McpTimeoutResolver.ResolveDiscoveryTimeoutMilliseconds(server);
            var client = await _connectionManager.GetClientAsync(
                server,
                await _serverCatalogService.GetHeadersAsync(server, cancellationToken),
                await _serverCatalogService.GetEnvironmentVariablesAsync(server, cancellationToken),
                discoveryTimeoutMilliseconds,
                McpConnectionScope.For(context.SessionId, context.Workspace?.WorkspaceId),
                cancellationToken);
            if (client is null)
            {
                return new AgentToolResult(
                    canonicalToolId,
                    $"MCP server '{server.DisplayName}' is unavailable.",
                    Content: $"### MCP server unavailable\n\nServer '{server.DisplayName}' could not be reached.",
                    IsError: true,
                    ErrorCode: "mcp-server-unavailable");
            }

            using var toolTimeoutScope = CreateTimeoutScope(McpTimeoutResolver.ResolveToolTimeoutMilliseconds(server), cancellationToken);
            var result = await client.CallToolAsync(
                resolved.ToolName,
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
                canonicalToolId,
                result.IsError == true ? $"MCP tool '{canonicalToolId}' returned an error." : $"MCP tool '{canonicalToolId}' completed.",
                Content: content,
                StructuredPayloadJson: structuredPayloadJson,
                WasTruncated: false,
                IsError: result.IsError == true,
                ErrorCode: result.IsError == true ? "mcp-tool-error" : null,
                BackendId: $"mcp:{server.ServerId}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AgentToolResult(
                canonicalToolId,
                ex.Message,
                Content: $"### MCP tool failed\n\n{ex.Message}",
                IsError: true,
                ErrorCode: "mcp-tool-execution");
        }
    }

    private async Task<ResolvedMcpTool?> ResolveToolAsync(string toolId, bool allowLegacyAlias, CancellationToken cancellationToken)
    {
        await EnsureConfigurationInitializedAsync(cancellationToken);
        if (McpToolIdentity.TryParseStable(toolId, out var serverId, out var toolName))
        {
            var server = await _serverCatalogService.GetServerAsync(serverId, cancellationToken);
            return server?.IsEnabled == true ? new ResolvedMcpTool(server, toolName, IsLegacyAlias: false) : null;
        }

        if (!allowLegacyAlias)
        {
            return null;
        }

        var servers = await _serverCatalogService.ListServersAsync(cancellationToken);
        return McpToolIdentity.TryParseLegacy(toolId, servers, out var legacyServer, out toolName)
            ? new ResolvedMcpTool(legacyServer!, toolName, IsLegacyAlias: true)
            : null;
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
            McpToolIdentity.CreateStable(server.ServerId, tool.Name),
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
            SelectionGroupDescription: server.Description,
            Aliases: [McpToolIdentity.CreateLegacyAlias(server.Name, tool.Name)]);

    private static AgentRuntimeTool ToRuntimeTool(ConfiguredMcpServerRecord server, McpClientTool tool)
    {
        var descriptor = ToDescriptor(server, tool);
        var declaration = tool
            .WithName(descriptor.ToolId)
            .WithDescription(descriptor.Description);
        return new AgentRuntimeTool(descriptor, declaration);
    }

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

    private sealed record ResolvedMcpTool(ConfiguredMcpServerRecord Server, string ToolName, bool IsLegacyAlias);
}
