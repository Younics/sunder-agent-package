using System.Text;

namespace Sunder.Package.Agent.Shared.Presentation;

internal sealed record McpToolCardText(string Label, string DetailText);

internal static class McpToolCardPresentation
{
    private const string StablePrefix = "m1_";
    private const string FriendlySummaryPrefix = "MCP: ";
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";
    private const int MaxLabelChars = 120;
    private const int MaxServerLabelChars = 56;

    private static readonly (string Suffix, string Detail)[] SuccessfulSummarySuffixes =
    [
        (" completed with truncated output.", "Completed with truncated output."),
        (" completed.", "Completed."),
    ];

    private static readonly (string Suffix, string Detail)[] FailedSummarySuffixes =
    [
        (" returned an error.", "Returned an error."),
        (" received invalid arguments.", "Invalid arguments."),
        (" is not configured.", "Not configured."),
        (" is unavailable.", "Unavailable."),
        (" outcome is uncertain.", "Outcome uncertain."),
        (" failed.", "Failed."),
    ];

    public static string CreateStableToolId(string serverId, string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        var encodedServerId = Encode(serverId);
        return $"{StablePrefix}{encodedServerId.Length}_{encodedServerId}{Encode(toolName)}";
    }

    public static bool TryParseStableToolId(
        string? toolId,
        out string serverId,
        out string toolName)
    {
        serverId = string.Empty;
        toolName = string.Empty;
        if (string.IsNullOrWhiteSpace(toolId)
            || !toolId.StartsWith(StablePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lengthSeparator = toolId.IndexOf('_', StablePrefix.Length);
        if (lengthSeparator < 0
            || !int.TryParse(
                toolId.AsSpan(StablePrefix.Length, lengthSeparator - StablePrefix.Length),
                out var serverLength)
            || serverLength <= 0)
        {
            return false;
        }

        var payload = toolId[(lengthSeparator + 1)..];
        if (payload.Length <= serverLength
            || !TryDecode(payload[..serverLength], out serverId)
            || !TryDecode(payload[serverLength..], out toolName)
            || string.IsNullOrWhiteSpace(serverId)
            || string.IsNullOrWhiteSpace(toolName))
        {
            serverId = string.Empty;
            toolName = string.Empty;
            return false;
        }

        return true;
    }

    public static string CreateDisplayLabel(
        string? serverDisplayName,
        string toolName,
        string? toolTitle = null)
    {
        var server = NormalizeSingleLine(serverDisplayName);
        var tool = NormalizeSingleLine(string.IsNullOrWhiteSpace(toolTitle)
            ? HumanizeToolName(toolName)
            : toolTitle);
        if (string.IsNullOrWhiteSpace(server))
        {
            return string.IsNullOrWhiteSpace(tool) ? "MCP Tool" : NormalizeLabel(tool);
        }
        server = TruncateLabel(server, MaxServerLabelChars);
        tool = TruncateLabel(
            string.IsNullOrWhiteSpace(tool) ? "MCP Tool" : tool,
            MaxLabelChars - server.Length - 2);
        return $"{server}: {tool}";
    }

    public static string CompletedSummary(string label, bool wasTruncated)
        => $"{FriendlySummaryPrefix}{NormalizeLabel(label)} completed{(wasTruncated ? " with truncated output" : string.Empty)}.";

    public static string ToolErrorSummary(string label)
        => $"{FriendlySummaryPrefix}{NormalizeLabel(label)} returned an error.";

    public static string TimeoutSummary(string label, int timeoutMilliseconds, string? timeoutSource = null)
        => timeoutMilliseconds is > 0
            ? $"{FriendlySummaryPrefix}{NormalizeLabel(label)} timed out after {FormatDuration(timeoutMilliseconds)}{(string.IsNullOrWhiteSpace(timeoutSource) ? string.Empty : $" ({timeoutSource.Trim()})")}."
            : $"{FriendlySummaryPrefix}{NormalizeLabel(label)} timed out.";

    public static string FailureSummary(string label)
        => $"{FriendlySummaryPrefix}{NormalizeLabel(label)} failed.";

    public static string AmbiguousSummary(string label)
        => $"{FriendlySummaryPrefix}{NormalizeLabel(label)} outcome is uncertain.";

    public static string UnavailableSummary(string label)
        => $"{FriendlySummaryPrefix}{NormalizeLabel(label)} is unavailable.";

    public static string InvalidArgumentsSummary(string label)
        => $"{FriendlySummaryPrefix}{NormalizeLabel(label)} received invalid arguments.";

    public static string NotConfiguredSummary(string label)
        => $"{FriendlySummaryPrefix}{NormalizeLabel(label)} is not configured.";

    public static bool TryResolveCardText(
        string? toolId,
        string? persistedSummary,
        bool isFailure,
        bool wasTruncated,
        out McpToolCardText card)
    {
        card = new McpToolCardText(string.Empty, string.Empty);
        if (!TryParseStableToolId(toolId, out _, out var toolName))
        {
            return false;
        }

        var fallbackLabel = HumanizeToolName(toolName);
        var summary = NormalizeSingleLine(persistedSummary);
        if (string.IsNullOrWhiteSpace(summary))
        {
            card = new McpToolCardText(fallbackLabel, string.Empty);
            return true;
        }

        var quotedId = $"'{toolId}'";
        string detail;
        if (summary.Equals($"MCP tool {quotedId} completed.", StringComparison.Ordinal))
        {
            detail = "Completed.";
        }
        else if (summary.Equals(
                     $"MCP tool {quotedId} completed with a bounded, truncated result.",
                     StringComparison.Ordinal))
        {
            detail = "Completed with truncated output.";
        }
        else if (summary.Equals($"MCP tool {quotedId} returned an error.", StringComparison.Ordinal))
        {
            detail = "Returned an error.";
        }
        else if (summary.Equals($"MCP tool {quotedId} is not configured.", StringComparison.Ordinal))
        {
            detail = "Not configured.";
        }
        else if (summary.StartsWith("MCP tool 'm1_", StringComparison.OrdinalIgnoreCase))
        {
            detail = TryExtractHttpClientTimeout(summary, out var timeoutDetail)
                ? timeoutDetail
                : ContainsTimeoutText(summary)
                    ? "Timed out."
                    : !isFailure && (wasTruncated || summary.Contains("truncated", StringComparison.OrdinalIgnoreCase))
                        ? "Completed with truncated output."
                        : isFailure ? "Failed." : "Completed.";
        }
        else if (TrySplitFriendlySummary(summary, isFailure, out var label, out detail))
        {
            card = new McpToolCardText(label, detail);
            return true;
        }
        else if (TryExtractHttpClientTimeout(summary, out detail)
                 || ContainsTimeoutText(summary))
        {
            detail = string.IsNullOrWhiteSpace(detail) ? "Timed out." : detail;
        }
        else if (isFailure)
        {
            detail = "Failed.";
        }
        else
        {
            detail = NormalizeSingleLine(summary.Replace(toolId!, fallbackLabel, StringComparison.Ordinal));
        }

        card = new McpToolCardText(fallbackLabel, detail);
        return true;
    }

    public static string HumanizeToolName(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return "MCP Tool";
        }

        var builder = new StringBuilder(toolName.Length + 8);
        for (var index = 0; index < toolName.Length; index++)
        {
            var character = toolName[index];
            if (character is '_' or '-' or '.')
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }
                continue;
            }
            if (char.IsUpper(character)
                && index > 0
                && char.IsLower(toolName[index - 1])
                && builder.Length > 0
                && builder[^1] != ' ')
            {
                builder.Append(' ');
            }
            builder.Append(character);
        }

        var words = builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length == 0
            ? "MCP Tool"
            : NormalizeLabel(string.Join(' ', words.Select(word =>
                char.ToUpperInvariant(word[0]) + word[1..])));
    }

    private static bool TrySplitFriendlySummary(
        string summary,
        bool isFailure,
        out string label,
        out string detail)
    {
        if (!summary.StartsWith(FriendlySummaryPrefix, StringComparison.Ordinal))
        {
            label = string.Empty;
            detail = string.Empty;
            return false;
        }

        var body = summary[FriendlySummaryPrefix.Length..];
        var suffixes = isFailure ? FailedSummarySuffixes : SuccessfulSummarySuffixes;
        foreach (var (suffix, projectedDetail) in suffixes)
        {
            if (body.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && body.Length > suffix.Length)
            {
                label = NormalizeLabel(body[..^suffix.Length]);
                detail = projectedDetail;
                return !string.IsNullOrWhiteSpace(label);
            }
        }

        if (isFailure)
        {
            const string timeoutMarker = " timed out after ";
            var timeoutIndex = body.LastIndexOf(timeoutMarker, StringComparison.OrdinalIgnoreCase);
            if (timeoutIndex > 0 && body.EndsWith(".", StringComparison.Ordinal))
            {
                label = NormalizeLabel(body[..timeoutIndex]);
                detail = "Timed out after " + body[(timeoutIndex + timeoutMarker.Length)..];
                return !string.IsNullOrWhiteSpace(label);
            }

            if (body.EndsWith(" timed out.", StringComparison.OrdinalIgnoreCase))
            {
                label = NormalizeLabel(body[..^" timed out.".Length]);
                detail = "Timed out.";
                return !string.IsNullOrWhiteSpace(label);
            }
        }

        label = string.Empty;
        detail = string.Empty;
        return false;
    }

    private static bool TryExtractHttpClientTimeout(string summary, out string detail)
    {
        const string marker = "HttpClient.Timeout of ";
        detail = string.Empty;
        var start = summary.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return false;
        }
        start += marker.Length;
        var end = summary.IndexOf(" elapsing", start, StringComparison.OrdinalIgnoreCase);
        if (end <= start)
        {
            detail = "Timed out.";
            return true;
        }
        detail = $"Timed out after {summary[start..end].Trim()}.";
        return true;
    }

    private static bool ContainsTimeoutText(string summary)
        => summary.Contains("timed out", StringComparison.OrdinalIgnoreCase)
           || summary.Contains("timeout", StringComparison.OrdinalIgnoreCase)
           || summary.Contains("canceled before it completed", StringComparison.OrdinalIgnoreCase);

    private static string FormatDuration(int timeoutMilliseconds)
        => timeoutMilliseconds % 1000 == 0
            ? $"{timeoutMilliseconds / 1000} {(timeoutMilliseconds == 1000 ? "second" : "seconds")}"
            : $"{timeoutMilliseconds} {(timeoutMilliseconds == 1 ? "millisecond" : "milliseconds")}";

    private static string NormalizeLabel(string? value)
    {
        var normalized = NormalizeSingleLine(value);
        return TruncateLabel(normalized, MaxLabelChars);
    }

    private static string TruncateLabel(string value, int maximumCharacters)
    {
        if (value.Length <= maximumCharacters)
        {
            return value;
        }
        var prefixLength = maximumCharacters - 3;
        if (prefixLength > 0
            && char.IsHighSurrogate(value[prefixLength - 1])
            && char.IsLowSurrogate(value[prefixLength]))
        {
            prefixLength--;
        }
        return value[..prefixLength].TrimEnd() + "...";
    }

    private static string NormalizeSingleLine(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(' ', value.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Encode(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var result = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var buffer = 0;
        var bitCount = 0;
        foreach (var item in bytes)
        {
            buffer = (buffer << 8) | item;
            bitCount += 8;
            while (bitCount >= 5)
            {
                result.Append(Alphabet[(buffer >> (bitCount - 5)) & 31]);
                bitCount -= 5;
            }
        }
        if (bitCount > 0)
        {
            result.Append(Alphabet[(buffer << (5 - bitCount)) & 31]);
        }
        return result.ToString();
    }

    private static bool TryDecode(string value, out string decoded)
    {
        decoded = string.Empty;
        var bytes = new List<byte>((value.Length * 5) / 8);
        var buffer = 0;
        var bitCount = 0;
        foreach (var character in value)
        {
            var index = Alphabet.IndexOf(char.ToLowerInvariant(character));
            if (index < 0)
            {
                return false;
            }
            buffer = (buffer << 5) | index;
            bitCount += 5;
            if (bitCount >= 8)
            {
                bytes.Add((byte)(buffer >> (bitCount - 8)));
                bitCount -= 8;
            }
        }

        try
        {
            decoded = new UTF8Encoding(false, true).GetString([.. bytes]);
            return string.Equals(Encode(decoded), value, StringComparison.OrdinalIgnoreCase);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
