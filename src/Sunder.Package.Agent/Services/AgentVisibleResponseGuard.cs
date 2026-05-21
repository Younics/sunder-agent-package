using System.Text;
using System.Text.RegularExpressions;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services;

public static partial class AgentVisibleResponseGuard
{
    public const string BlockedResponseContent = "### Assistant response blocked\n\nThe provider returned internal tool/protocol text instead of a visible answer. Please retry or continue with a follow-up message.";

    private const string VisibleResponseInstructions = "Write only Markdown intended for the user. Do not print internal tool protocol, pseudo tool calls, channel labels, or execution directives such as `assistant to=functions.*`, `<assistant to=functions.*>`, `function=tool_name`, `\"tool_calls\"`, `<tool>`, or `tool_code`. When a tool is needed, use Sunder's native tool-calling interface instead of describing or printing the call.";

    public static AgentSystemPromptBlock CreateSystemPromptBlock() =>
        new(
            "visible-response-format",
            "Visible Response Format",
            VisibleResponseInstructions,
            Priority: 120,
            Required: true,
            SourceId: "sunder.package.agent");

    public static bool ContainsProtocolLeak(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return false;
        }

        var visibleText = RemoveFencedCodeBlocks(markdown);
        return AssistantToolCallPattern().IsMatch(visibleText)
               || AssistantTagToolCallPattern().IsMatch(visibleText)
               || FunctionAssignmentPattern().IsMatch(visibleText)
               || ToolCallsJsonPattern().IsMatch(visibleText)
               || FunctionsRecipientJsonPattern().IsMatch(visibleText)
               || ToolTagPattern().IsMatch(visibleText)
               || ToolCodePattern().IsMatch(visibleText);
    }

    private static string RemoveFencedCodeBlocks(string markdown)
    {
        var builder = new StringBuilder(markdown.Length);
        var insideFence = false;
        string? fenceMarker = null;
        using var reader = new StringReader(markdown);
        while (reader.ReadLine() is { } line)
        {
            var trimmedStart = line.TrimStart();
            var marker = GetFenceMarker(trimmedStart);
            if (marker is not null)
            {
                if (!insideFence)
                {
                    insideFence = true;
                    fenceMarker = marker;
                }
                else if (string.Equals(marker, fenceMarker, StringComparison.Ordinal))
                {
                    insideFence = false;
                    fenceMarker = null;
                }

                builder.AppendLine();
                continue;
            }

            if (!insideFence)
            {
                builder.AppendLine(line);
            }
            else
            {
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private static string? GetFenceMarker(string line)
    {
        if (line.StartsWith("```", StringComparison.Ordinal))
        {
            return "```";
        }

        return line.StartsWith("~~~", StringComparison.Ordinal) ? "~~~" : null;
    }

    [GeneratedRegex(@"(?im)\b(?:assistant|analysis|commentary)\s+to\s*=\s*functions(?:\.|\b)")]
    private static partial Regex AssistantToolCallPattern();

    [GeneratedRegex(@"(?is)<\s*(?:assistant|analysis|commentary)\b[^>]*\bto\s*=\s*functions(?:\.|\b)[^>]*>")]
    private static partial Regex AssistantTagToolCallPattern();

    [GeneratedRegex(@"(?im)(?:^\s*|<\s*)function\s*=\s*[a-zA-Z_][\w.-]*(?:\b|>)")]
    private static partial Regex FunctionAssignmentPattern();

    [GeneratedRegex("(?i)[\\\"']tool_calls[\\\"']\\s*:")]
    private static partial Regex ToolCallsJsonPattern();

    [GeneratedRegex("(?i)[\\\"']recipient_name[\\\"']\\s*:\\s*[\\\"']functions\\.")]
    private static partial Regex FunctionsRecipientJsonPattern();

    [GeneratedRegex(@"(?im)^\s*</?\s*(?:tool|tool_code|tool_call|function_call|function_result)\b[^>]*>")]
    private static partial Regex ToolTagPattern();

    [GeneratedRegex(@"(?im)^\s*(?:tool_code|tool_call|function_call)\s*$")]
    private static partial Regex ToolCodePattern();
}
