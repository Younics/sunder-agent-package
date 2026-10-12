using Sunder.Agent.Execution.Common;
using Sunder.Generated.Packages.Sunder_Package_Agent_Models;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Package.Agent.Tools.Web.Services;

namespace Sunder.Package.Agent.Tools.Web;

public sealed class WebFetchTool(WebFetchService fetchService)
{
    private readonly WebFetchService _fetchService = fetchService;

    public AgentToolDescriptor Descriptor { get; } = new()
    {
        ToolId = "web_fetch",
        DisplayName = "Web Fetch",
        Description = "Fetch a known URL and return it as text, markdown, or raw HTML.",
        IsReadOnly = true,
        RequiresNetwork = true,
        SourceKind = "web",
        SourceId = "web-tools",
        SourceDisplayName = "Web Tools",
        ArgumentsJsonSchema =
        """
        {
          "type": "object",
          "properties": {
            "url": {
              "type": "string",
              "description": "Absolute HTTP or HTTPS URL to fetch."
            },
            "format": {
              "type": "string",
              "enum": ["markdown", "text", "html"],
              "description": "Response format. Defaults to markdown."
            },
            "timeoutSeconds": {
              "type": "integer",
              "minimum": 1,
              "maximum": 120,
              "description": "Optional request timeout in seconds."
            }
          },
          "required": ["url"],
          "additionalProperties": false
        }
        """,
        Aliases = null, SelectionScope = AgentToolSelectionScope.Tool, SelectionGroupId = null,
        SelectionGroupDisplayName = null, SelectionGroupDescription = null, RuntimeInstructions = null,
        ActivationRequirement = null, Priority = AgentToolPriority.Medium,
        ConcurrencyMode = AgentToolConcurrencyMode.ParallelSafe,
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
            HeaderText = request.ResultSummary ?? (parsed ? args.Url : null),
            DetailMarkdown = !parsed
                ? AgentToolPresentationMarkdown.BuildRawRequestMarkdown(request.ArgumentsJson, error)
                : AgentToolPresentationMarkdown.BuildRequestMarkdown(
                    ("URL", args.Url),
                    ("Format", string.IsNullOrWhiteSpace(args.Format) ? "markdown" : args.Format),
                    ("Timeout", args.TimeoutSeconds is null ? null : $"{args.TimeoutSeconds.Value}s")),
            OutputText = request.TextContent,
        };
    }

    public ValueTask<AgentToolReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentToolReadiness { ToolId = Descriptor.ToolId, Status = AgentToolReadinessStatus.Ready, Message = "Web fetch is available." });
    }

    public async ValueTask<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseArguments(request.ArgumentsJson, out var arguments, out var error))
        {
            return Failure(error!, error!, "web-fetch-args");
        }

        if (string.IsNullOrWhiteSpace(arguments.Url))
        {
            return Failure("The url parameter is required.", "The `url` parameter is required.", "web-fetch-url-required");
        }

        var format = string.IsNullOrWhiteSpace(arguments.Format) ? "markdown" : arguments.Format.ToLowerInvariant();
        if (format is not ("text" or "markdown" or "html"))
        {
            return Failure("Format must be text, markdown, or html.", "`format` must be one of `text`, `markdown`, or `html`.", "web-fetch-format-invalid");
        }

        if (!Uri.TryCreate(arguments.Url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return Failure("URL must start with http:// or https://.", "`url` must be an absolute HTTP or HTTPS URL.", "web-fetch-url-invalid");
        }

        try
        {
            var result = await _fetchService.FetchAsync(uri.ToString(), format, arguments.TimeoutSeconds, cancellationToken);
            return new AgentToolResult
            {
                ToolId = Descriptor.ToolId, Summary = result.Summary, Content = result.Content,
                StructuredPayloadJson = result.StructuredPayloadJson,
                Sources = [new() { Title = result.Title ?? result.FinalUrl, Url = result.FinalUrl, Snippet = null }],
                WasTruncated = result.WasTruncated, BackendId = "http", IsError = false, ErrorCode = null,
                PresentationPayloadJson = null, RequiresPromptContextRefresh = false,
            };
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(ex.Message, $"The web fetch timed out or was canceled before the resource could be reached.\n\n{ex.Message}", "web-fetch-http");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure(ex.Message, ex.Message, "web-fetch-http");
        }
    }

    private AgentToolResult Failure(string summary, string detail, string code) => new()
    {
        ToolId = Descriptor.ToolId, Summary = summary, Content = AgentToolPresentationMarkdown.BuildFailureMarkdown("Web fetch failed", detail),
        StructuredPayloadJson = null, Sources = null, WasTruncated = false, IsError = true, ErrorCode = code, BackendId = null,
        PresentationPayloadJson = null, RequiresPromptContextRefresh = false,
    };

    private static bool TryParseArguments(string argumentsJson, out WebFetchArguments arguments, out string? error)
    {
        arguments = new WebFetchArguments(string.Empty);
        if (!AgentToolArgumentObject.TryParse(argumentsJson, out var parsedArguments, out error)
            || !parsedArguments!.TryReadRequiredString("url", out var url, out error)
            || !parsedArguments.TryReadOptionalString("format", out var format, out error)
            || !parsedArguments.TryReadOptionalInt32("timeoutSeconds", out var timeoutSeconds, out error))
        {
            error = $"Invalid web_fetch arguments: {error ?? "arguments were empty or invalid."}";
            return false;
        }

        if (!BoundedValue.IsInRange(timeoutSeconds, 1, 120))
        {
            error = "Invalid web_fetch arguments: timeoutSeconds must be between 1 and 120.";
            return false;
        }

        arguments = new WebFetchArguments(url!, format, timeoutSeconds);
        return true;
    }

    private sealed record WebFetchArguments(string Url, string? Format = null, int? TimeoutSeconds = null);
}
