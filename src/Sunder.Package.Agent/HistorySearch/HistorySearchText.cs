using System.Buffers;
using System.Text;

namespace Sunder.Package.Agent.HistorySearch;

internal static class HistorySearchText
{
    internal static string ToValidScalars(string? value, out bool hadNonWhitespaceInput)
    {
        hadNonWhitespaceInput = false;
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
            if (status != OperationStatus.Done)
            {
                hadNonWhitespaceInput = true;
                builder.Append(' ');
                remaining = remaining[Math.Max(1, consumed)..];
                continue;
            }

            if (!Rune.IsWhiteSpace(rune))
            {
                hadNonWhitespaceInput = true;
            }
            builder.Append(rune.ToString());
            remaining = remaining[consumed..];
        }
        return builder.ToString();
    }

    internal static string NormalizeFacet(string? value)
    {
        return NormalizeForSearch(value).Trim();
    }

    internal static string NormalizeStoredText(string? value)
    {
        var valid = ToValidScalars(value, out _);
        var redacted = HistorySecretRedactor.Redact(valid);
        return ToValidScalars(redacted, out _).Normalize(NormalizationForm.FormC);
    }

    internal static string NormalizeForSearch(string? value)
    {
        var valid = ToValidScalars(value, out _).Normalize(NormalizationForm.FormC);
        return valid.ToLowerInvariant().Normalize(NormalizationForm.FormC);
    }

    internal static string SanitizeQueryInput(string? value, int maximumUtf16Characters)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        var builder = new StringBuilder(Math.Min(value.Length, maximumUtf16Characters));
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
            if (status != OperationStatus.Done)
            {
                rune = Rune.ReplacementChar;
                consumed = Math.Max(1, consumed);
            }
            if (builder.Length + rune.Utf16SequenceLength > maximumUtf16Characters)
            {
                break;
            }
            builder.Append(rune.ToString());
            remaining = remaining[consumed..];
        }
        return builder.ToString().Trim();
    }

    internal static string SanitizeSnippet(string? value)
    {
        var valid = NormalizeStoredText(value);
        return BoundAtRuneBoundary(valid.Trim(), HistorySearchLimits.MaximumSnippetCharacters);
    }

    internal static string BoundAtRuneBoundary(string? value, int maximumUtf16Characters)
    {
        if (string.IsNullOrEmpty(value) || maximumUtf16Characters <= 0)
        {
            return string.Empty;
        }
        if (value.Length <= maximumUtf16Characters && IsWellFormedUtf16(value))
        {
            return value;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maximumUtf16Characters));
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
            if (status != OperationStatus.Done)
            {
                remaining = remaining[Math.Max(1, consumed)..];
                continue;
            }
            if (builder.Length + rune.Utf16SequenceLength > maximumUtf16Characters)
            {
                break;
            }
            builder.Append(rune.ToString());
            remaining = remaining[consumed..];
        }
        return builder.ToString();
    }

    internal static int CountRunes(string value)
        => value.EnumerateRunes().Count();

    private static bool IsWellFormedUtf16(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done)
            {
                return false;
            }
            remaining = remaining[consumed..];
        }
        return true;
    }
}
