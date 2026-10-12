extern alias McpWorker;
extern alias AgentApp;

using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using McpWorker::Sunder.Package.Agent.Mcp;
using AgentToolPresentationRequest = McpWorker::Sunder.Generated.Packages.Sunder_Package_Agent_Models.AgentToolPresentationRequest;
using AgentApp::Sunder.Package.Agent.Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class McpToolPresentationTests
{
    [Fact]
    public void CollapsedResult_UsesPersistedServerAndToolLabel()
    {
        var toolId = McpToolIdentity.CreateStable("stitch-server", "generate_screen_from_text");

        using var row = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem(
                toolId,
                resultSummary: "MCP: Stitch: Generate screen completed.",
                status: AgentToolExecutionStatus.Completed),
            new AgentToolPresentationService());

        Assert.Equal("Stitch: Generate screen", row.ToolLabel);
        Assert.Equal("Completed", row.StatusText);
        Assert.Equal("Completed.", row.HeaderDetailText);
        Assert.DoesNotContain("m1_", row.SummaryText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CollapsedHistoricalResult_DecodesToolNameAndHidesEncodedIdentifier()
    {
        var toolId = McpToolIdentity.CreateStable("historical-server", "generate_screen_from_text");

        using var row = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem(
                toolId,
                resultSummary: $"MCP tool '{toolId}' completed.",
                status: AgentToolExecutionStatus.Completed),
            new AgentToolPresentationService());

        Assert.Equal("Generate Screen From Text", row.ToolLabel);
        Assert.Equal("Completed.", row.HeaderDetailText);
        Assert.DoesNotContain(toolId, row.SummaryText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CollapsedHistoricalTimeout_ProjectsConciseFailureWithoutTransportDetails()
    {
        var toolId = McpToolIdentity.CreateStable("historical-server", "list_resources");
        const string timeout = "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.";

        using var row = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem(
                toolId,
                resultSummary: timeout,
                isError: true,
                status: AgentToolExecutionStatus.Failed),
            new AgentToolPresentationService());

        Assert.Equal("List Resources", row.ToolLabel);
        Assert.Equal("Failed", row.StatusText);
        Assert.Equal("Timed out after 100 seconds.", row.HeaderDetailText);
        Assert.DoesNotContain("HttpClient", row.SummaryText, StringComparison.Ordinal);
        Assert.DoesNotContain(toolId, row.SummaryText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CollapsedAmbiguousResult_ShowsUncertainOutcomeWithoutEncodedIdentifier()
    {
        var toolId = McpToolIdentity.CreateStable("stitch-server", "edit_screens");

        using var row = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem(
                toolId,
                resultSummary: "MCP: Stitch: Edit Screens outcome is uncertain.",
                isError: true,
                status: AgentToolExecutionStatus.Ambiguous),
            new AgentToolPresentationService());

        Assert.Equal("Stitch: Edit Screens", row.ToolLabel);
        Assert.Equal("Ambiguous", row.StatusText);
        Assert.Equal("Outcome uncertain.", row.HeaderDetailText);
        Assert.DoesNotContain("m1_", row.SummaryText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CollapsedHistoricalGenericFailure_DoesNotTreatErrorSentenceAsLabel()
    {
        var toolId = McpToolIdentity.CreateStable("historical-server", "list_resources");

        using var row = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem(
                toolId,
                resultSummary: "MCP server 'stitch' is unavailable.",
                isError: true,
                status: AgentToolExecutionStatus.Failed),
            new AgentToolPresentationService());

        Assert.Equal("List Resources", row.ToolLabel);
        Assert.Equal("Failed.", row.HeaderDetailText);
    }

    [Fact]
    public void CollapsedHistoricalLongIdentifier_NeverShowsTruncatedEncoding()
    {
        var toolId = McpToolIdentity.CreateStable(new string('s', 300), "list_resources");

        using var row = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem(
                toolId,
                resultSummary: $"MCP tool '{toolId}' completed with a bounded, truncated result.",
                wasTruncated: true,
                status: AgentToolExecutionStatus.Completed),
            new AgentToolPresentationService());

        Assert.Equal("List Resources", row.ToolLabel);
        Assert.Equal("Completed with truncated output.", row.HeaderDetailText);
        Assert.DoesNotContain("m1_", row.SummaryText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExpandedPresentation_RedactsNestedArgumentsAndExtractsTextOutput()
    {
        var toolId = McpToolIdentity.CreateStable("stitch-server", "generate_screen_from_text");
        var argumentsJson = JsonSerializer.Serialize(new
        {
            prompt = "Build a basic dashboard",
            maxTokens = 4_000,
            tokenCount = 12,
            tokenizer = "cl100k_base",
            passwordPolicy = "At least 20 characters",
            oauthScopes = new[] { "screens:read" },
            openAIApiKey = "opaque-openai-key",
            AWSSecretKey = "opaque-aws-key",
            apiKeys = new[] { "opaque-api-key" },
            tokens = new[] { "opaque-token" },
            auth = new
            {
                api_key = "sk-visible-secret",
                nested = new[] { new { password = "nested-password" } },
            },
            header = "Bearer bearer-secret",
            xAuth = "x-auth-secret",
            opaqueToken = "ghp_opaque-secret",
            connection = "postgresql://database-user:database-password@example.com/sunder",
            endpoint = "https://alice:url-password@example.com/api?token=query-secret#fragment",
            webhook = "https://example.com/hooks/token=path-secret",
            callback = "myapp://callback?code=custom-uri-secret",
        });
        const string outputJson = """
            [
              { "type": "text", "text": "Created screen 42." },
              { "type": "image", "data": "ignored" },
              { "type": "text", "text": "Ready to review." }
            ]
            """;

        var presentation = McpToolPresentationAdapter.Resolve(PresentationRequest(
            toolId,
            argumentsJson,
            ResultSummary: "MCP: Stitch: Generate screen completed.",
            TextContent: outputJson,
            StructuredPayloadJson: null,
            SourcesJson: null,
            IsError: false,
            ErrorCode: null,
            BackendId: "mcp:stitch-server"));

        Assert.NotNull(presentation);
        Assert.Equal("Stitch: Generate screen", presentation!.HeaderText);
        Assert.Contains("Build a basic dashboard", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.Contains("\"maxTokens\": 4000", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.Contains("\"tokenCount\": 12", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.Contains("cl100k_base", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.Contains("At least 20 characters", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.Contains("screens:read", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.Contains("https://example.com/api", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-visible-secret", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("nested-password", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-secret", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("x-auth-secret", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-secret", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("database-password", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("url-password", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("path-secret", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("custom-uri-secret", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-openai-key", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-aws-key", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-api-key", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-token", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.Equal($"Created screen 42.{Environment.NewLine}{Environment.NewLine}Ready to review.", presentation.OutputText);
    }

    [Fact]
    public void ExpandedPresentation_DoesNotEchoMalformedArguments()
    {
        var toolId = McpToolIdentity.CreateStable("stitch-server", "generate_screen_from_text");
        const string malformedArguments = "{\"apiKey\":\"must-not-leak\"";

        var presentation = McpToolPresentationAdapter.Resolve(PresentationRequest(
            toolId,
            malformedArguments,
            ResultSummary: null,
            TextContent: null,
            StructuredPayloadJson: null,
            SourcesJson: null,
            IsError: true,
            ErrorCode: "mcp-arguments-invalid",
            BackendId: null));

        Assert.NotNull(presentation);
        Assert.Contains("payload was invalid", presentation!.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-leak", presentation.DetailMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpandedPresentation_SeparatesSunderExpectedLimitFromForwardedArguments()
    {
        var toolId = McpToolIdentity.CreateStable("stitch-server", "generate_screen_from_text");
        var arguments = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            [McpToolTimeoutMetadata.EnvelopePropertyName] = new Dictionary<string, object?>
            {
                [McpToolTimeoutMetadata.MetadataMarkerPropertyName] = McpToolTimeoutMetadata.MetadataMarkerValue,
                [McpToolTimeoutMetadata.EnvelopeArgumentsPropertyName] = new Dictionary<string, object?>
                {
                    [McpToolTimeoutMetadata.TimeoutPropertyName] = "server-owned-value",
                    ["prompt"] = "Build a dashboard",
                },
                [McpToolTimeoutMetadata.TimeoutPropertyName] = 90,
            },
        });

        var presentation = McpToolPresentationAdapter.Resolve(PresentationRequest(
            toolId,
            arguments,
            ResultSummary: null,
            TextContent: null,
            StructuredPayloadJson: null,
            SourcesJson: null,
            IsError: false,
            ErrorCode: null,
            BackendId: null));

        Assert.NotNull(presentation);
        Assert.Contains("Sunder expected limit: 90 seconds", presentation!.DetailMarkdown, StringComparison.Ordinal);
        Assert.Contains(McpToolTimeoutMetadata.TimeoutPropertyName, presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.Contains("server-owned-value", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.Contains("Build a dashboard", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain(McpToolTimeoutMetadata.EnvelopePropertyName, presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain(McpToolTimeoutMetadata.MetadataMarkerPropertyName, presentation.DetailMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpandedPresentation_AfterRestartPreservesReservedLookingServerArgumentsWithoutMarker()
    {
        var toolId = McpToolIdentity.CreateStable("stitch-server", "generate_screen_from_text");
        var arguments = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            [McpToolTimeoutMetadata.TimeoutPropertyName] = "legitimate-server-value",
            [McpToolTimeoutMetadata.EnvelopePropertyName] = new Dictionary<string, object?>
            {
                [McpToolTimeoutMetadata.MetadataMarkerPropertyName] = "historical-server-version",
                [McpToolTimeoutMetadata.EnvelopeArgumentsPropertyName] = "also-server-owned",
            },
        });
        var presentation = McpToolPresentationAdapter.Resolve(PresentationRequest(
            toolId,
            arguments,
            ResultSummary: null,
            TextContent: null,
            StructuredPayloadJson: null,
            SourcesJson: null,
            IsError: false,
            ErrorCode: null,
            BackendId: null));

        Assert.NotNull(presentation);
        Assert.Contains(McpToolTimeoutMetadata.TimeoutPropertyName, presentation!.DetailMarkdown, StringComparison.Ordinal);
        Assert.Contains("legitimate-server-value", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.Contains("also-server-owned", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.Contains("historical-server-version", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Sunder expected limit:", presentation.DetailMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpandedPresentation_PreservesStructuredArrayShape()
    {
        var toolId = McpToolIdentity.CreateStable("stitch-server", "inspect_status");
        const string structuredPayload = """
            [{ "type": "metadata", "text": "not a content block", "status": "failed" }]
            """;

        var presentation = McpToolPresentationAdapter.Resolve(PresentationRequest(
            toolId,
            "{}",
            ResultSummary: null,
            TextContent: null,
            StructuredPayloadJson: structuredPayload,
            SourcesJson: null,
            IsError: false,
            ErrorCode: null,
            BackendId: null));

        Assert.NotNull(presentation);
        Assert.Contains("\"type\": \"metadata\"", presentation!.OutputText, StringComparison.Ordinal);
        Assert.Contains("\"status\": \"failed\"", presentation.OutputText, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpandedPresentation_BoundsLargeStructuredOutput()
    {
        var toolId = McpToolIdentity.CreateStable("stitch-server", "inspect_status");
        var structuredPayload = "[" + string.Join(',', Enumerable.Repeat("{\"value\":1}", 70_000)) + "]";

        var presentation = McpToolPresentationAdapter.Resolve(PresentationRequest(
            toolId,
            "{}",
            ResultSummary: null,
            TextContent: null,
            StructuredPayloadJson: structuredPayload,
            SourcesJson: null,
            IsError: false,
            ErrorCode: null,
            BackendId: null));

        Assert.NotNull(presentation);
        Assert.NotNull(presentation!.OutputText);
        Assert.True(presentation.OutputText!.Length <= 800_000);
        Assert.EndsWith("[Output truncated for display.]", presentation.OutputText, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpandedPresentation_TruncatesOutputAtUnicodeBoundary()
    {
        const string notice = "\n\n[Output truncated for display.]";
        const int maximumOutputCharacters = 800_000;
        var prefixLength = maximumOutputCharacters - notice.Length;
        var longText = new string('x', prefixLength - 1) + "\U0001F600" + new string('x', 100);
        var toolId = McpToolIdentity.CreateStable("stitch-server", "inspect_status");

        var presentation = McpToolPresentationAdapter.Resolve(PresentationRequest(
            toolId,
            "{}",
            ResultSummary: null,
            TextContent: JsonSerializer.Serialize(longText),
            StructuredPayloadJson: null,
            SourcesJson: null,
            IsError: false,
            ErrorCode: null,
            BackendId: null));

        Assert.NotNull(presentation);
        Assert.NotNull(presentation!.OutputText);
        _ = new UTF8Encoding(false, true).GetBytes(presentation.OutputText!);
        Assert.EndsWith("[Output truncated for display.]", presentation.OutputText, StringComparison.Ordinal);
    }

    private static AgentTurnRecord CreateToolTurn()
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentTurnRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            AgentMessageRole.Tool,
            AgentTurnKind.ToolResult,
            [],
            now,
            now);
    }

    private static AgentTurnItemRecord CreateToolItem(
        string toolId,
        string? resultSummary,
        bool isError = false,
        bool wasTruncated = false,
        AgentToolExecutionStatus? status = null)
    {
        var turnId = Guid.NewGuid();
        return new AgentTurnItemRecord(
            Guid.NewGuid(),
            turnId,
            SequenceNumber: 0,
            AgentTurnItemKind.ToolResult,
            TextContent: null,
            CallId: "call-1",
            toolId,
            ArgumentsJson: "{}",
            resultSummary,
            StructuredPayloadJson: null,
            SourcesJson: null,
            WasTruncated: wasTruncated,
            IsError: isError,
            ErrorCode: null,
            BackendId: null)
        {
            ToolExecutionId = Guid.NewGuid(),
            ToolExecutionStatus = status,
        };
    }

    private static AgentToolPresentationRequest PresentationRequest(
        string toolId,
        string argumentsJson,
        string? ResultSummary,
        string? TextContent,
        string? StructuredPayloadJson,
        string? SourcesJson,
        bool IsError,
        string? ErrorCode,
        string? BackendId)
        => new()
        {
            ToolId = toolId,
            ArgumentsJson = argumentsJson,
            ResultSummary = ResultSummary,
            TextContent = TextContent,
            StructuredPayloadJson = StructuredPayloadJson,
            SourcesJson = SourcesJson,
            IsError = IsError,
            ErrorCode = ErrorCode,
            BackendId = BackendId,
        };
}
