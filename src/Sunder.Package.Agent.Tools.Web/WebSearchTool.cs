using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Package.Agent.Tools.Web.Services;

namespace Sunder.Package.Agent.Tools.Web;

public sealed class WebSearchTool(WebSearchService webSearchService, WebToolsSettingsService settingsService)
{
    private readonly WebSearchService _webSearchService = webSearchService;
    private readonly WebToolsSettingsService _settingsService = settingsService;

    public AgentToolDescriptor Descriptor { get; } = new()
    {
        ToolId = "web_search", DisplayName = "Web Search",
        Description = "Search the web using the configured provider and return source links and excerpts. Auto mode supports free search and rate-limit failover.",
        IsReadOnly = true, RequiresNetwork = true, SourceKind = "web", SourceId = "web-tools", SourceDisplayName = "Web Tools",
        ArgumentsJsonSchema =
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
              "maximum": 10,
              "description": "Optional maximum number of results to return."
            }
          },
          "required": ["query"],
          "additionalProperties": false
        }
        """,
        Aliases = null, SelectionScope = AgentToolSelectionScope.Tool, SelectionGroupId = null,
        SelectionGroupDisplayName = null, SelectionGroupDescription = null, RuntimeInstructions = null,
        ActivationRequirement = null, Priority = AgentToolPriority.Medium,
        ConcurrencyMode = AgentToolConcurrencyMode.Sequential,
    };

    public AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request)
    {
        if (!string.Equals(request.ToolId, Descriptor.ToolId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parsed = TryParseArguments(request.ArgumentsJson, out var args, out var error);
        return new AgentToolPresentation
        {
            HeaderText = request.ResultSummary ?? (parsed ? args.Query : null),
            DetailMarkdown = !parsed
                ? AgentToolPresentationMarkdown.BuildRawRequestMarkdown(request.ArgumentsJson, error)
                : AgentToolPresentationMarkdown.BuildRequestMarkdown(
                    ("Query", args.Query),
                    ("Max results", args.MaxResults?.ToString())),
            OutputText = request.TextContent,
        };
    }

    public async ValueTask<AgentToolReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _webSearchService.GetReadinessAsync(cancellationToken);
    }

    public async ValueTask<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseArguments(request.ArgumentsJson, out var arguments, out var error))
        {
            return Failure(error!, error!, "web-search-args");
        }

        if (string.IsNullOrWhiteSpace(arguments.Query))
        {
            return Failure("The query parameter is required.", "The `query` parameter is required.", "web-search-query-required");
        }

        var readiness = await _webSearchService.GetReadinessAsync(cancellationToken);
        if (readiness.Status != AgentToolReadinessStatus.Ready)
        {
            return Failure(readiness.Message, readiness.Message, "web-search-not-ready", "Web search not ready");
        }

        try
        {
            var result = await _webSearchService.SearchAsync(
                arguments.Query,
                arguments.MaxResults ?? await _settingsService.GetDefaultMaxResultsAsync(cancellationToken),
                context.SessionId,
                cancellationToken);
            return new AgentToolResult
            {
                ToolId = Descriptor.ToolId, Summary = result.Summary, Content = result.Content,
                StructuredPayloadJson = result.StructuredPayloadJson, Sources = result.Sources,
                WasTruncated = result.WasTruncated, BackendId = result.BackendId, IsError = false, ErrorCode = null,
                PresentationPayloadJson = null, RequiresPromptContextRefresh = false,
            };
        }
        catch (Backends.WebSearchRateLimitException ex)
        {
            return Failure("Web search rate limited", ex.Message, "web-search-rate-limited");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(ex.Message, $"The web search timed out or was canceled before the backend could respond.\n\n{ex.Message}", "web-search-http");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            return Failure(ex.Message, ex.Message, "web-search-http");
        }
    }

    private AgentToolResult Failure(string summary, string detail, string code, string heading = "Web search failed") => new()
    {
        ToolId = Descriptor.ToolId, Summary = summary, Content = AgentToolPresentationMarkdown.BuildFailureMarkdown(heading, detail),
        StructuredPayloadJson = null, Sources = null, WasTruncated = false, IsError = true, ErrorCode = code, BackendId = null,
        PresentationPayloadJson = null, RequiresPromptContextRefresh = false,
    };

    private static bool TryParseArguments(string argumentsJson, out WebSearchArguments arguments, out string? error)
    {
        arguments = new WebSearchArguments(string.Empty);
        if (!AgentToolArgumentObject.TryParse(argumentsJson, out var parsedArguments, out error)
            || !parsedArguments!.TryReadRequiredString("query", out var query, out error)
            || !parsedArguments.TryReadOptionalInt32("maxResults", out var maxResults, out error))
        {
            error = $"Invalid web_search arguments: {error ?? "arguments were empty or invalid."}";
            return false;
        }

        arguments = new WebSearchArguments(query!, maxResults);
        return true;
    }

    private static bool IsFatal(Exception exception)
        => exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or System.Runtime.InteropServices.SEHException
           || exception is AggregateException aggregate && aggregate.InnerExceptions.Any(IsFatal)
           || exception.InnerException is { } inner && IsFatal(inner);

    private sealed record WebSearchArguments(string Query, int? MaxResults = null);
}
