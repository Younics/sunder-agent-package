using System.Text;
using System.Text.RegularExpressions;

namespace Sunder.Package.Agent.HistorySearch;

internal static partial class HistorySecretRedactor
{
    internal static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Strip whole URL-private portions before assignment scanning can consume adjacent safe text.
        var redacted = SanitizeHttpUrls(value);
        redacted = RedactPemBlocks(redacted);
        redacted = BearerRegex().Replace(redacted, "$1[REDACTED]");
        redacted = CommonCredentialRegex().Replace(redacted, "[REDACTED CREDENTIAL]");
        return RedactSensitiveAssignments(redacted);
    }

    internal static string? SanitizeHttpUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return null;
        }

        try
        {
            var builder = new UriBuilder(uri.Scheme, uri.IdnHost)
            {
                Path = uri.AbsolutePath,
                UserName = string.Empty,
                Password = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty,
            };
            if (!uri.IsDefaultPort)
            {
                builder.Port = uri.Port;
            }
            return builder.Uri.GetLeftPart(UriPartial.Path);
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            return null;
        }
    }

    private static string SanitizeHttpUrls(string value)
    {
        var matches = HttpUrlRegex().Matches(value);
        if (matches.Count == 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var cursor = 0;
        foreach (Match match in matches)
        {
            if (match.Index < cursor)
            {
                continue;
            }

            builder.Append(value, cursor, match.Index - cursor);
            var candidateEnd = FindHttpUrlCandidateEnd(value, match.Index, match.Index + match.Length);
            builder.Append(SanitizeHttpUrlMatch(value[match.Index..candidateEnd]));
            cursor = candidateEnd;
        }
        builder.Append(value, cursor, value.Length - cursor);
        return builder.ToString();
    }

    private static int FindHttpUrlCandidateEnd(string value, int start, int end)
    {
        while (end < value.Length)
        {
            if (value[end] is ' ' or '\t')
            {
                if (!CanStartQuotedPrivateUrlValue(value.AsSpan(start, end - start)))
                {
                    break;
                }

                var quoteStart = end;
                while (quoteStart < value.Length && value[quoteStart] is ' ' or '\t')
                {
                    quoteStart++;
                }
                if (quoteStart >= value.Length || value[quoteStart] is not ('"' or '\'' or '`'))
                {
                    break;
                }
                end = quoteStart;
            }

            if (value[end] is '"' or '\'' or '`')
            {
                if (!CanStartQuotedPrivateUrlValue(value.AsSpan(start, end - start)))
                {
                    break;
                }

                end = FindUrlQuotedValueEnd(value, end, value[end], out var closed);
                if (!closed)
                {
                    return end;
                }
                continue;
            }

            if (char.IsWhiteSpace(value[end]) || value[end] is '<' or '>')
            {
                break;
            }
            end++;
        }

        return end;
    }

    private static bool CanStartQuotedPrivateUrlValue(ReadOnlySpan<char> candidate)
    {
        var end = candidate.Length;
        while (end > 0 && candidate[end - 1] is ' ' or '\t')
        {
            end--;
        }
        if (end == 0)
        {
            return false;
        }

        return candidate[end - 1] is '=' or ':' or '?' or '#' or '&' or ';'
               || candidate[..end].EndsWith("://", StringComparison.Ordinal);
    }

    private static int FindUrlQuotedValueEnd(string value, int start, char quote, out bool closed)
    {
        var lineEnd = FindLineEnd(value, start);
        for (var index = start + 1; index < lineEnd; index++)
        {
            if (value[index] != quote)
            {
                continue;
            }
            if (quote == '\'' && index + 1 < lineEnd && value[index + 1] == '\'')
            {
                index++;
                continue;
            }

            var slashCount = 0;
            for (var previous = index - 1; previous > start && value[previous] == '\\'; previous--)
            {
                slashCount++;
            }
            if (slashCount % 2 == 0)
            {
                closed = true;
                return index + 1;
            }
        }

        closed = false;
        return lineEnd;
    }

    private static string SanitizeHttpUrlMatch(string candidate)
    {
        var coreLength = candidate.Length;
        while (coreLength > 0)
        {
            var character = candidate[coreLength - 1];
            if (character is '.' or ',' or ';' or '!')
            {
                coreLength--;
                continue;
            }

            var opening = character switch
            {
                ')' => '(',
                ']' => '[',
                '}' => '{',
                _ => '\0',
            };
            if (opening == '\0'
                || !HasUnmatchedClosingDelimiter(candidate.AsSpan(0, coreLength), opening, character))
            {
                break;
            }

            coreLength--;
        }

        var core = candidate[..coreLength];
        var sanitized = SanitizeHttpUrl(core);
        if (sanitized is null)
        {
            var queryStart = core.IndexOf('?');
            var fragmentStart = core.IndexOf('#');
            var privateStart = queryStart < 0
                ? fragmentStart
                : fragmentStart < 0
                    ? queryStart
                    : Math.Min(queryStart, fragmentStart);
            if (privateStart > 0)
            {
                sanitized = SanitizeHttpUrl(core[..privateStart]);
            }
        }
        return sanitized is null
            ? "[REDACTED URL]" + candidate[coreLength..]
            : sanitized + candidate[coreLength..];
    }

    private static bool HasUnmatchedClosingDelimiter(ReadOnlySpan<char> value, char opening, char closing)
    {
        var balance = 0;
        foreach (var character in value)
        {
            if (character == opening)
            {
                balance++;
            }
            else if (character == closing)
            {
                balance--;
            }
        }

        return balance < 0;
    }

    private static string RedactPemBlocks(string value)
    {
        var begins = PemBeginRegex().Matches(value);
        if (begins.Count == 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var cursor = 0;
        foreach (Match begin in begins)
        {
            if (begin.Index < cursor)
            {
                continue;
            }

            builder.Append(value, cursor, begin.Index - cursor);
            var complete = PemRegex().Match(value, begin.Index);
            var end = complete.Success && complete.Index == begin.Index
                ? complete.Index + complete.Length
                : FindUnterminatedPemEnd(value, begin.Index, begin.Index + begin.Length);
            builder.Append("[REDACTED PEM]");
            cursor = end;
        }
        builder.Append(value, cursor, value.Length - cursor);
        return builder.ToString();
    }

    private static int FindUnterminatedPemEnd(string value, int beginStart, int headerEnd)
    {
        var lineStart = FindLineStart(value, beginStart);
        var contentStart = SkipIndent(value, lineStart, beginStart);
        var beginIndent = contentStart - lineStart;
        var inlineYamlProperty = StartsWithReliableYamlProperty(value, contentStart, beginStart);
        if (!inlineYamlProperty && beginIndent == 0)
        {
            return value.Length;
        }

        var boundaryIndent = inlineYamlProperty ? beginIndent : beginIndent - 1;
        var headerLineEnd = FindLineEnd(value, headerEnd);
        var consumed = headerLineEnd;
        var line = AdvancePastNewline(value, headerLineEnd);
        while (line < value.Length)
        {
            var lineEnd = FindLineEnd(value, line);
            var content = SkipIndent(value, line, lineEnd);
            if (content < lineEnd
                && content - line <= boundaryIndent
                && StartsWithReliableYamlProperty(value, content, lineEnd))
            {
                return consumed;
            }

            consumed = lineEnd;
            var next = AdvancePastNewline(value, lineEnd);
            if (next == lineEnd)
            {
                return lineEnd;
            }
            line = next;
        }

        return value.Length;
    }

    private static string RedactSensitiveAssignments(string value)
    {
        var matches = SensitiveAssignmentPrefixRegex().Matches(value);
        if (matches.Count == 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var cursor = 0;
        foreach (Match match in matches)
        {
            if (match.Index < cursor)
            {
                continue;
            }
            var valueStart = match.Index + match.Length;
            builder.Append(value, cursor, valueStart - cursor);
            builder.Append("[REDACTED]");
            cursor = FindSensitiveValueEnd(value, valueStart, match.Index);
        }
        builder.Append(value, cursor, value.Length - cursor);
        return builder.ToString();
    }

    private static int FindSensitiveValueEnd(string value, int start, int assignmentStart)
    {
        var lineEnd = FindLineEnd(value, start);
        var parentIndent = GetLineIndent(value, assignmentStart);
        var yamlAssignment = IsYamlAssignment(value, start);
        var scalarStart = SkipYamlNodeProperties(value, start, lineEnd);
        if (scalarStart >= lineEnd)
        {
            return FindIndentedContinuationEnd(
                value,
                lineEnd,
                parentIndent);
        }

        if (scalarStart + 1 < lineEnd
            && value[scalarStart] == '\\'
            && value[scalarStart + 1] is '"' or '\'')
        {
            return FindEscapedQuotedValueEnd(
                value,
                scalarStart,
                value[scalarStart + 1],
                yamlAssignment,
                parentIndent);
        }
        var quote = value[scalarStart];
        if (quote is '"' or '\'')
        {
            return FindQuotedValueEnd(value, scalarStart, quote, yamlAssignment, parentIndent);
        }
        if (value[scalarStart] is '|' or '>')
        {
            return FindIndentedContinuationEnd(
                value,
                lineEnd,
                parentIndent);
        }

        var end = scalarStart;
        while (end < lineEnd && value[end] is not (',' or ';' or '}' or ']'))
        {
            end++;
        }
        return yamlAssignment && end == lineEnd
            ? FindIndentedContinuationEnd(value, lineEnd, parentIndent)
            : end;
    }

    private static bool IsYamlAssignment(string value, int valueStart)
    {
        var cursor = valueStart - 1;
        while (cursor >= 0 && value[cursor] is ' ' or '\t')
        {
            cursor--;
        }
        return cursor >= 0 && value[cursor] == ':';
    }

    private static int SkipYamlNodeProperties(string value, int start, int lineEnd)
    {
        var cursor = start;
        while (cursor < lineEnd)
        {
            while (cursor < lineEnd && value[cursor] is ' ' or '\t')
            {
                cursor++;
            }
            if (cursor >= lineEnd || value[cursor] == '#')
            {
                return lineEnd;
            }

            var propertyEnd = value[cursor] switch
            {
                '!' => FindYamlTagEnd(value, cursor, lineEnd),
                '&' => FindYamlPropertyEnd(value, cursor, lineEnd),
                _ => cursor,
            };
            if (propertyEnd == cursor)
            {
                return cursor;
            }
            cursor = propertyEnd;
        }
        return lineEnd;
    }

    private static int FindYamlTagEnd(string value, int start, int lineEnd)
    {
        if (start + 1 < lineEnd && value[start + 1] == '<')
        {
            var close = value.IndexOf('>', start + 2, lineEnd - start - 2);
            return close < 0 ? lineEnd : close + 1;
        }
        return FindYamlPropertyEnd(value, start, lineEnd);
    }

    private static int FindYamlPropertyEnd(string value, int start, int lineEnd)
    {
        var cursor = start + 1;
        while (cursor < lineEnd && value[cursor] is not (' ' or '\t'))
        {
            cursor++;
        }
        return cursor;
    }

    private static int FindQuotedValueEnd(
        string value,
        int start,
        char quote,
        bool preserveYamlBoundary,
        int parentIndent)
    {
        var index = start + 1;
        while (index < value.Length)
        {
            var lineEnd = FindLineEnd(value, index);
            for (; index < lineEnd; index++)
            {
                if (value[index] != quote)
                {
                    continue;
                }
                if (quote == '\'' && index + 1 < lineEnd && value[index + 1] == '\'')
                {
                    index++;
                    continue;
                }

                var slashCount = 0;
                for (var previous = index - 1; previous >= start && value[previous] == '\\'; previous--)
                {
                    slashCount++;
                }
                if (slashCount % 2 == 0)
                {
                    return index + 1;
                }
            }

            var next = AdvancePastNewline(value, lineEnd);
            if (next == lineEnd)
            {
                return lineEnd;
            }
            if (preserveYamlBoundary && IsReliableYamlPropertyBoundary(value, next, parentIndent))
            {
                return lineEnd;
            }
            index = next;
        }
        return value.Length;
    }

    private static int FindEscapedQuotedValueEnd(
        string value,
        int start,
        char quote,
        bool preserveYamlBoundary,
        int parentIndent)
    {
        var index = start + 2;
        while (index < value.Length)
        {
            var lineEnd = FindLineEnd(value, index);
            for (; index + 1 < lineEnd; index++)
            {
                if (value[index] == '\\' && value[index + 1] == quote)
                {
                    return index + 2;
                }
            }

            var next = AdvancePastNewline(value, lineEnd);
            if (next == lineEnd)
            {
                return lineEnd;
            }
            if (preserveYamlBoundary && IsReliableYamlPropertyBoundary(value, next, parentIndent))
            {
                return lineEnd;
            }
            index = next;
        }
        return value.Length;
    }

    private static bool IsReliableYamlPropertyBoundary(string value, int lineStart, int parentIndent)
    {
        var lineEnd = FindLineEnd(value, lineStart);
        var content = SkipIndent(value, lineStart, lineEnd);
        return content < lineEnd
               && content - lineStart <= parentIndent
               && StartsWithReliableYamlProperty(value, content, lineEnd);
    }

    private static bool StartsWithReliableYamlProperty(string value, int contentStart, int lineEnd)
    {
        var cursor = contentStart;
        if (cursor >= lineEnd)
        {
            return false;
        }

        if (value[cursor] is '"' or '\'')
        {
            var quote = value[cursor++];
            while (cursor < lineEnd && value[cursor] != quote)
            {
                cursor++;
            }
            if (cursor >= lineEnd)
            {
                return false;
            }
            cursor++;
        }
        else
        {
            var keyStart = cursor;
            while (cursor < lineEnd
                   && (char.IsLetterOrDigit(value[cursor]) || value[cursor] is '_' or '-' or '.'))
            {
                cursor++;
            }
            if (cursor == keyStart)
            {
                return false;
            }
        }

        while (cursor < lineEnd && value[cursor] is ' ' or '\t')
        {
            cursor++;
        }
        return cursor < lineEnd
               && value[cursor] == ':'
               && (cursor + 1 == lineEnd || char.IsWhiteSpace(value[cursor + 1]));
    }

    private static int FindIndentedContinuationEnd(string value, int headerLineEnd, int parentIndent)
    {
        var cursor = AdvancePastNewline(value, headerLineEnd);
        if (cursor == headerLineEnd)
        {
            return headerLineEnd;
        }
        var consumed = headerLineEnd;
        while (cursor < value.Length)
        {
            var lineEnd = FindLineEnd(value, cursor);
            var content = cursor;
            while (content < lineEnd && value[content] is ' ' or '\t')
            {
                content++;
            }
            if (content < lineEnd && content - cursor <= parentIndent)
            {
                return consumed;
            }
            consumed = lineEnd;
            var next = AdvancePastNewline(value, lineEnd);
            if (next == lineEnd)
            {
                return lineEnd;
            }
            cursor = next;
        }
        return value.Length;
    }

    private static int GetLineIndent(string value, int index)
    {
        var lineStart = index;
        while (lineStart > 0 && value[lineStart - 1] is not ('\r' or '\n'))
        {
            lineStart--;
        }
        var content = lineStart;
        while (content < index && value[content] is ' ' or '\t')
        {
            content++;
        }
        return content - lineStart;
    }

    private static int FindLineStart(string value, int index)
    {
        var lineStart = index;
        while (lineStart > 0 && value[lineStart - 1] is not ('\r' or '\n'))
        {
            lineStart--;
        }
        return lineStart;
    }

    private static int SkipIndent(string value, int lineStart, int lineEnd)
    {
        var content = lineStart;
        while (content < lineEnd && value[content] is ' ' or '\t')
        {
            content++;
        }
        return content;
    }

    private static int FindLineEnd(string value, int start)
    {
        var lineEnd = start;
        while (lineEnd < value.Length && value[lineEnd] is not ('\r' or '\n'))
        {
            lineEnd++;
        }
        return lineEnd;
    }

    private static int AdvancePastNewline(string value, int lineEnd)
    {
        if (lineEnd >= value.Length)
        {
            return lineEnd;
        }
        if (value[lineEnd] == '\r' && lineEnd + 1 < value.Length && value[lineEnd + 1] == '\n')
        {
            return lineEnd + 2;
        }
        return lineEnd + 1;
    }

    [GeneratedRegex("""(?i)\bhttps?://[^\s<>"'`]*""", RegexOptions.CultureInvariant)]
    private static partial Regex HttpUrlRegex();

    [GeneratedRegex(@"-----BEGIN [^-\r\n]+-----[\s\S]*?-----END [^-\r\n]+-----", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PemRegex();

    [GeneratedRegex(@"-----BEGIN [^-\r\n]+-----", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PemBeginRegex();

    [GeneratedRegex(@"(?i)\b(Bearer\s+)[A-Za-z0-9._~+/=-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"(?i)\b(?:sk-(?:proj-)?[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{16,}|AKIA[A-Z0-9]{16}|xox[baprs]-[A-Za-z0-9-]{12,})\b", RegexOptions.CultureInvariant)]
    private static partial Regex CommonCredentialRegex();

    [GeneratedRegex("""(?im)(?<![\p{L}\p{N}_])(?:\\?["'])?(?:api[_-]?key|access[_-]?token|refresh[_-]?token|token|password|passwd|secret|client[_-]?secret|authorization|private[_-]?key|[A-Z0-9_]*(?:TOKEN|SECRET|PASSWORD|API_KEY))(?![\p{L}\p{N}_])(?:\\?["'])?[ \t]*(?::|=)[ \t]*""", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveAssignmentPrefixRegex();
}
