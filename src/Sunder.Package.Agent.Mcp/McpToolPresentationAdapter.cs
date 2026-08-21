using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Mcp;

internal static class McpToolPresentationAdapter
{
    private const int MaxPrettyJsonChars = 64_000;
    private const int MaxOutputChars = 800_000;
    private const string TruncatedOutputNotice = "\n\n[Output truncated for display.]";

    public static AgentToolPresentation? Resolve(AgentToolPresentationRequest request)
    {
        if (!McpToolCardPresentation.TryParseStableToolId(request.ToolId, out _, out var toolName))
        {
            return null;
        }

        _ = McpToolCardPresentation.TryResolveCardText(
            request.ToolId,
            request.ResultSummary,
            request.IsError,
            wasTruncated: false,
            out var card);
        var label = string.IsNullOrWhiteSpace(card.Label)
            ? McpToolCardPresentation.HumanizeToolName(toolName)
            : card.Label;
        var argumentPresentation = McpToolTimeoutMetadata.SeparateForPresentation(request.ArgumentsJson);
        return new AgentToolPresentation(
            label,
            BuildDetailMarkdown(
                label,
                argumentPresentation.ForwardedArgumentsJson,
                argumentPresentation.ExpectedLimitText),
            BuildOutputText(request.TextContent, request.StructuredPayloadJson));
    }

    private static string BuildDetailMarkdown(
        string label,
        string argumentsJson,
        string? expectedLimitText)
    {
        var builder = new StringBuilder();
        builder.AppendLine(AgentToolPresentationMarkdown.BuildRequestMarkdown(
            ("MCP tool", EscapeMarkdown(label)),
            ("Sunder expected limit", expectedLimitText)));
        if (string.IsNullOrWhiteSpace(argumentsJson)
            || string.Equals(argumentsJson.Trim(), "{}", StringComparison.Ordinal))
        {
            return builder.ToString().Trim();
        }

        builder.AppendLine();
        if (ToolArgumentDisplay.TryFormatRedactedJson(argumentsJson, out var formatted))
        {
            builder.AppendLine(AgentToolPresentationMarkdown.BuildFencedMarkdown(
                "Arguments",
                "json",
                formatted));
        }
        else
        {
            builder.AppendLine("**Arguments**");
            builder.AppendLine("Arguments could not be displayed because the payload was invalid.");
        }
        return builder.ToString().Trim();
    }

    private static string? BuildOutputText(string? textContent, string? structuredPayloadJson)
    {
        if (!string.IsNullOrWhiteSpace(structuredPayloadJson))
        {
            return FormatOutput(structuredPayloadJson, extractMcpTextBlocks: false);
        }
        if (string.IsNullOrWhiteSpace(textContent))
        {
            return null;
        }
        return FormatOutput(textContent, extractMcpTextBlocks: true);
    }

    private static string FormatOutput(string candidate, bool extractMcpTextBlocks)
    {
        try
        {
            using var document = JsonDocument.Parse(candidate);
            if (extractMcpTextBlocks && document.RootElement.ValueKind == JsonValueKind.Array)
            {
                var textBlocks = document.RootElement.EnumerateArray()
                    .Select(TryReadTextBlock)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToArray();
                if (textBlocks.Length > 0)
                {
                    return BoundOutput(string.Join(Environment.NewLine + Environment.NewLine, textBlocks));
                }
            }
            if (document.RootElement.ValueKind == JsonValueKind.String)
            {
                return BoundOutput(document.RootElement.GetString()?.Trim() ?? string.Empty);
            }
            var formatted = candidate.Length > MaxPrettyJsonChars
                ? candidate.Trim()
                : JsonSerializer.Serialize(
                    document.RootElement,
                    new JsonSerializerOptions { WriteIndented = true });
            return BoundOutput(formatted);
        }
        catch (JsonException)
        {
            return BoundOutput(candidate.Trim());
        }
    }

    private static string? TryReadTextBlock(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        string? type = null;
        string? text = null;
        foreach (var property in block.EnumerateObject())
        {
            if (string.Equals(property.Name, "type", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                type = property.Value.GetString();
            }
            else if (string.Equals(property.Name, "text", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                text = property.Value.GetString()?.Trim();
            }
        }
        return string.Equals(type, "text", StringComparison.OrdinalIgnoreCase) ? text : null;
    }

    private static string BoundOutput(string value)
    {
        if (value.Length <= MaxOutputChars)
        {
            return value;
        }
        var prefixLength = MaxOutputChars - TruncatedOutputNotice.Length;
        if (char.IsHighSurrogate(value[prefixLength - 1])
            && char.IsLowSurrogate(value[prefixLength]))
        {
            prefixLength--;
        }
        return value[..prefixLength].TrimEnd() + TruncatedOutputNotice;
    }

    private static string EscapeMarkdown(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
}
