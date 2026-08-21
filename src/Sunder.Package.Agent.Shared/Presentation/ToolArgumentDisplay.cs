using System.Text;
using System.Text.Json;

namespace Sunder.Package.Agent.Shared.Presentation;

internal static class ToolArgumentDisplay
{
    private const int MaxRenderedChars = 16_000;
    private const int MaxStringChars = 1_000;
    private const string RedactedValue = "[REDACTED]";

    private static readonly HashSet<string> SensitiveNormalizedNames = new(StringComparer.Ordinal)
    {
        "accesskey",
        "accesstoken",
        "apikey",
        "auth",
        "authorization",
        "clientsecret",
        "connectionstring",
        "cookie",
        "credential",
        "credentials",
        "dsn",
        "encryptionkey",
        "idtoken",
        "key",
        "oauth",
        "passphrase",
        "password",
        "passwd",
        "privatekey",
        "pwd",
        "refreshtoken",
        "secret",
        "signingkey",
        "sessionkey",
        "sessiontoken",
        "token",
        "xauth",
    };

    private static readonly HashSet<string> MetadataNameWords = new(StringComparer.Ordinal)
    {
        "budget",
        "count",
        "counts",
        "estimate",
        "estimated",
        "label",
        "labels",
        "limit",
        "limits",
        "max",
        "maximum",
        "min",
        "minimum",
        "name",
        "names",
        "policies",
        "policy",
        "remaining",
        "scope",
        "scopes",
        "type",
        "types",
        "usage",
        "used",
    };

    private static readonly string[] StrongSensitiveNameFragments =
    [
        "accesskey",
        "apikey",
        "authorization",
        "clientsecret",
        "connectionstring",
        "cookie",
        "credential",
        "encryptionkey",
        "passphrase",
        "password",
        "passwd",
        "privatekey",
        "secret",
        "sessionkey",
        "signingkey",
    ];

    private static readonly string[] CredentialPrefixes =
    [
        "ghp_",
        "gho_",
        "ghr_",
        "ghs_",
        "ghu_",
        "github_pat_",
        "glpat-",
        "hf_",
        "npm_",
        "pypi-",
        "rk_live_",
        "sk-",
        "sk_",
        "xoxa-",
        "xoxb-",
        "xoxp-",
        "xoxr-",
        "xoxs-",
    ];

    private static readonly string[] EmbeddedCredentialMarkers =
    [
        "api-key=",
        "api_key=",
        "apikey=",
        "access-token=",
        "access_token=",
        "authorization:",
        "client-secret=",
        "client_secret=",
        "password=",
        "passwd=",
        "private-key=",
        "private_key=",
        "pwd=",
        "secret=",
        "token=",
    ];

    private static readonly string[] AuthorizationPrefixes =
    [
        "basic ",
        "bearer ",
        "token ",
    ];

    public static bool TryFormatRedactedJson(string? json, out string formatted)
    {
        formatted = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            formatted = "{}";
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                WriteElement(writer, document.RootElement);
            }

            var rendered = Encoding.UTF8.GetString(stream.ToArray());
            formatted = rendered.Length <= MaxRenderedChars
                ? rendered
                : "{\n  \"notice\": \"Arguments were too large to display safely.\"\n}";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsSensitiveName(string name)
    {
        var words = SplitNameWords(name);
        var normalized = string.Concat(words);
        if (SensitiveNormalizedNames.Contains(normalized))
        {
            return true;
        }
        if (words.Count > 0
            && (MetadataNameWords.Contains(words[^1])
                || words[0] is "max" or "maximum" or "min" or "minimum"))
        {
            return false;
        }
        if (StrongSensitiveNameFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal)))
        {
            return true;
        }
        if (words.Contains("auth", StringComparer.Ordinal)
            || words.Contains("oauth", StringComparer.Ordinal))
        {
            return true;
        }
        return normalized is "token" or "tokens"
               || normalized.EndsWith("token", StringComparison.Ordinal)
               || normalized.EndsWith("tokens", StringComparison.Ordinal);
    }

    public static string? SanitizeInlineValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
        if (TrySanitizeUri(value, out var sanitizedUrl))
        {
            return sanitizedUrl;
        }
        if (LooksCredentialLike(value))
        {
            return RedactedValue;
        }
        return value.Length <= MaxStringChars
            ? value
            : $"[{value.Length:N0} characters]";
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (IsSensitiveName(property.Name))
                    {
                        writer.WriteStringValue(RedactedValue);
                    }
                    else
                    {
                        WriteElement(writer, property.Value);
                    }
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                var value = element.GetString() ?? string.Empty;
                writer.WriteStringValue(SanitizeInlineValue(value));
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                writer.WriteStringValue(RedactedValue);
                break;
        }
    }

    private static bool LooksCredentialLike(string value)
    {
        var trimmed = value.Trim();
        if (CredentialPrefixes.Any(prefix => trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || AuthorizationPrefixes.Any(prefix => trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || EmbeddedCredentialMarkers.Any(marker => trimmed.Contains(marker, StringComparison.OrdinalIgnoreCase))
            || trimmed.Contains("-----BEGIN ", StringComparison.OrdinalIgnoreCase)
               && trimmed.Contains("PRIVATE KEY-----", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsCloudAccessKey(trimmed) || IsJsonWebToken(trimmed);
    }

    private static bool TrySanitizeUri(string value, out string sanitized)
    {
        sanitized = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Scheme))
        {
            return false;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            if (!string.IsNullOrWhiteSpace(uri.UserInfo)
                || !string.IsNullOrWhiteSpace(uri.Query))
            {
                sanitized = RedactedValue;
                return true;
            }
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        try
        {
            var decodedPath = Uri.UnescapeDataString(uri.AbsolutePath);
            if (EmbeddedCredentialMarkers.Any(marker => decodedPath.Contains(marker, StringComparison.OrdinalIgnoreCase))
                || CredentialPrefixes.Any(prefix => decodedPath.Contains(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                sanitized = RedactedValue;
                return true;
            }

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
            sanitized = builder.Uri.GetLeftPart(UriPartial.Path);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> SplitNameWords(string name)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (!char.IsLetterOrDigit(character))
            {
                AddCurrentWord(words, current);
                continue;
            }
            if (char.IsUpper(character)
                && current.Length > 0
                && index > 0
                && (char.IsLower(name[index - 1]) || char.IsDigit(name[index - 1])))
            {
                AddCurrentWord(words, current);
            }
            current.Append(char.ToLowerInvariant(character));
        }
        AddCurrentWord(words, current);
        return words;
    }

    private static void AddCurrentWord(List<string> words, StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }
        words.Add(current.ToString());
        current.Clear();
    }

    private static bool IsCloudAccessKey(string value)
        => value.Length >= 16
           && (value.StartsWith("AKIA", StringComparison.Ordinal)
               || value.StartsWith("ASIA", StringComparison.Ordinal));

    private static bool IsJsonWebToken(string value)
    {
        if (!value.StartsWith("eyJ", StringComparison.Ordinal))
        {
            return false;
        }
        var segments = value.Split('.');
        return segments.Length == 3 && segments.All(segment => segment.Length > 0);
    }
}
