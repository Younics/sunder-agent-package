using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Tools.Web.Services;

namespace Sunder.Package.Agent.Tools.Web;

public sealed class WebSearchTool(Backends.ExaWebSearchBackend exaWebSearchBackend, WebToolsSettingsService settingsService) : IAgentTool, IAgentToolPresentationResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Backends.ExaWebSearchBackend _exaWebSearchBackend = exaWebSearchBackend;
    private readonly WebToolsSettingsService _settingsService = settingsService;

    public AgentToolDescriptor Descriptor { get; } = new(
        "web_search",
        "Web Search",
        "Search the web using the Exa-backed route and return normalized result items.",
        IsReadOnly: true,
        RequiresNetwork: true,
        SourceKind: "web",
        SourceId: "web-tools",
        SourceDisplayName: "Web Tools",
        ArgumentsJsonSchema:
        """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "Search query to send to the configured backend."
            },
            "maxResults": {
              "type": "integer",
              "minimum": 1,
              "maximum": 20,
              "description": "Optional maximum number of results to return."
            }
          },
          "required": ["query"],
          "additionalProperties": false
        }
        """);

    public AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request)
    {
        if (!string.Equals(request.ToolId, Descriptor.ToolId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var args = WebToolPresentation.TryDeserialize<WebSearchArguments>(request.ArgumentsJson);
        return new AgentToolPresentation(
            HeaderText: request.ResultSummary ?? args?.Query,
            DetailMarkdown: args is null
                ? WebToolPresentation.BuildUnavailableRequestMarkdown()
                : WebToolPresentation.BuildRequestMarkdown(
                    ("Query", args.Query),
                    ("Max results", args.MaxResults?.ToString())),
            OutputText: request.TextContent);
    }

    public async ValueTask<AgentToolReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _exaWebSearchBackend.GetReadinessAsync(cancellationToken);
    }

    public async ValueTask<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        WebSearchArguments? arguments;
        try
        {
            arguments = JsonSerializer.Deserialize<WebSearchArguments>(request.ArgumentsJson, JsonOptions);
        }
        catch (Exception ex)
        {
            return new AgentToolResult(
                Descriptor.ToolId,
                $"Failed to parse web_search arguments: {ex.Message}",
                Content: $"### Web search failed\n\n{ex.Message}",
                IsError: true,
                ErrorCode: "web-search-args");
        }

        if (arguments is null || string.IsNullOrWhiteSpace(arguments.Query))
        {
            return new AgentToolResult(
                Descriptor.ToolId,
                "The query parameter is required.",
                Content: "### Web search failed\n\nThe `query` parameter is required.",
                IsError: true,
                ErrorCode: "web-search-query-required");
        }

        var readiness = await _exaWebSearchBackend.GetReadinessAsync(cancellationToken);
        if (readiness.Status != AgentToolReadinessStatus.Ready)
        {
            return new AgentToolResult(
                Descriptor.ToolId,
                readiness.Message,
                Content: $"### Web search not ready\n\n{readiness.Message}",
                IsError: true,
                ErrorCode: "web-search-not-ready");
        }

        try
        {
            var result = await _exaWebSearchBackend.SearchAsync(arguments.Query, arguments.MaxResults ?? _settingsService.GetDefaultMaxResults(), cancellationToken);
            return new AgentToolResult(
                Descriptor.ToolId,
                result.Summary,
                Content: result.Content,
                StructuredPayloadJson: result.StructuredPayloadJson,
                Sources: result.Sources,
                WasTruncated: result.WasTruncated,
                BackendId: result.BackendId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AgentToolResult(
                Descriptor.ToolId,
                ex.Message,
                Content: $"### Web search failed\n\n{ex.Message}",
                IsError: true,
                ErrorCode: "web-search-http");
        }
    }

    private sealed record WebSearchArguments(string Query, int? MaxResults = null);
}
