using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Tools.Web.Services;

namespace Sunder.Package.Agent.Tools.Web;

public sealed class WebFetchTool(WebFetchService fetchService) : IAgentTool, IAgentToolPresentationResolver
{
    private readonly WebFetchService _fetchService = fetchService;

    public AgentToolDescriptor Descriptor { get; } = new(
        "web_fetch",
        "Web Fetch",
        "Fetch a known URL and return it as text, markdown, or raw HTML.",
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
        """);

    public AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request)
    {
        if (!string.Equals(request.ToolId, Descriptor.ToolId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parsed = TryParseArguments(request.ArgumentsJson, out var args, out var error);
        return new AgentToolPresentation(
            HeaderText: request.ResultSummary ?? (parsed ? args.Url : null),
            DetailMarkdown: !parsed
                ? WebToolPresentation.BuildRawRequestMarkdown(request.ArgumentsJson, error)
                : WebToolPresentation.BuildRequestMarkdown(
                    ("URL", args.Url),
                    ("Format", string.IsNullOrWhiteSpace(args.Format) ? "markdown" : args.Format),
                    ("Timeout", args.TimeoutSeconds is null ? null : $"{args.TimeoutSeconds.Value}s")),
            OutputText: request.TextContent);
    }

    public ValueTask<AgentToolReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentToolReadiness(Descriptor.ToolId, AgentToolReadinessStatus.Ready, "Web fetch is available."));
    }

    public async ValueTask<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseArguments(request.ArgumentsJson, out var arguments, out var error))
        {
            return new AgentToolResult(
                Descriptor.ToolId,
                error!,
                Content: $"### Web fetch failed\n\n{error}",
                IsError: true,
                ErrorCode: "web-fetch-args");
        }

        if (string.IsNullOrWhiteSpace(arguments.Url))
        {
            return new AgentToolResult(
                Descriptor.ToolId,
                "The url parameter is required.",
                Content: "### Web fetch failed\n\nThe `url` parameter is required.",
                IsError: true,
                ErrorCode: "web-fetch-url-required");
        }

        var format = string.IsNullOrWhiteSpace(arguments.Format) ? "markdown" : arguments.Format.ToLowerInvariant();
        if (format is not ("text" or "markdown" or "html"))
        {
            return new AgentToolResult(
                Descriptor.ToolId,
                "Format must be text, markdown, or html.",
                Content: "### Web fetch failed\n\n`format` must be one of `text`, `markdown`, or `html`.",
                IsError: true,
                ErrorCode: "web-fetch-format-invalid");
        }

        if (!Uri.TryCreate(arguments.Url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return new AgentToolResult(
                Descriptor.ToolId,
                "URL must start with http:// or https://.",
                Content: "### Web fetch failed\n\n`url` must be an absolute HTTP or HTTPS URL.",
                IsError: true,
                ErrorCode: "web-fetch-url-invalid");
        }

        try
        {
            var result = await _fetchService.FetchAsync(uri.ToString(), format, arguments.TimeoutSeconds, cancellationToken);
            return new AgentToolResult(
                Descriptor.ToolId,
                result.Summary,
                Content: result.Content,
                StructuredPayloadJson: result.StructuredPayloadJson,
                Sources: [new AgentToolSourceItem(result.Title ?? result.FinalUrl, result.FinalUrl)],
                WasTruncated: result.WasTruncated,
                BackendId: "http");
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
                Content: $"### Web fetch failed\n\n{ex.Message}",
                IsError: true,
                ErrorCode: "web-fetch-http");
        }
    }

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

        arguments = new WebFetchArguments(url!, format, timeoutSeconds);
        return true;
    }

    private sealed record WebFetchArguments(string Url, string? Format = null, int? TimeoutSeconds = null);
}
