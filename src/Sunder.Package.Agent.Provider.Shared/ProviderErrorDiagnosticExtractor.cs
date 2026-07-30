using System.Text.Json;

namespace Sunder.Package.Agent.Provider.Shared;

internal static class ProviderErrorDiagnosticExtractor
{
    public static string? Extract(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return payload;
        }

        if (TryExtractJson(payload, depth: 0, out var diagnostic))
        {
            return diagnostic;
        }

        var firstBrace = payload.IndexOf('{');
        var lastBrace = payload.LastIndexOf('}');
        return firstBrace >= 0
               && lastBrace > firstBrace
               && TryExtractJson(payload[firstBrace..(lastBrace + 1)], depth: 0, out diagnostic)
            ? diagnostic
            : payload;
    }

    private static bool TryExtractJson(string payload, int depth, out string diagnostic)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.String)
            {
                var decoded = document.RootElement.GetString() ?? string.Empty;
                if (depth < 2 && TryExtractJson(decoded, depth + 1, out diagnostic))
                {
                    return true;
                }

                if (depth >= 2 && IsJson(decoded))
                {
                    diagnostic = string.Empty;
                    return true;
                }

                diagnostic = decoded;
                return true;
            }
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostic = string.Empty;
                return true;
            }

            var root = document.RootElement;
            diagnostic = string.Join(
                ": ",
                new[]
                {
                    GetNestedString(root, "response", "error", "code"),
                    GetNestedString(root, "response", "error", "message"),
                    GetNestedString(root, "response", "error", "type"),
                    GetNestedString(root, "response", "incomplete_details", "reason"),
                    GetNestedString(root, "error", "code"),
                    GetNestedString(root, "error", "message"),
                    GetNestedString(root, "error", "type"),
                    GetNestedString(root, "error", "param"),
                    GetString(root, "code"),
                    GetString(root, "message"),
                    GetString(root, "type"),
                    GetString(root, "detail"),
                    GetString(root, "error"),
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
            return true;
        }
        catch (JsonException)
        {
            diagnostic = string.Empty;
            return false;
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsJson(string value)
    {
        try
        {
            using var _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? GetNestedString(JsonElement element, params string[] path)
    {
        foreach (var segment in path)
        {
            if (!element.TryGetProperty(segment, out element))
            {
                return null;
            }
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }
}
