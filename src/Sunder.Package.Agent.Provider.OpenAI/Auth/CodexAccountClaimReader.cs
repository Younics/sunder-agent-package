using System.Text.Json;

namespace Sunder.Package.Agent.Provider.OpenAI.Auth;

internal static class CodexAccountClaimReader
{
    public static string? ExtractAccountId(string jwt)
    {
        try
        {
            using var document = ParsePayload(jwt);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return ExtractAccountId(document.RootElement);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    public static DateTimeOffset? ExtractExpiration(string jwt)
    {
        try
        {
            using var document = ParsePayload(jwt);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("exp", out var expiration)
                || expiration.ValueKind != JsonValueKind.Number
                || !expiration.TryGetInt64(out var unixTimeSeconds))
            {
                return null;
            }

            return DateTimeOffset.FromUnixTimeSeconds(unixTimeSeconds);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or FormatException or JsonException)
        {
            return null;
        }
    }

    private static string? ExtractAccountId(JsonElement claims)
    {
        if (TryGetNonEmptyString(claims, "chatgpt_account_id") is { } topLevelAccountId)
        {
            return topLevelAccountId;
        }

        if (claims.TryGetProperty("https://api.openai.com/auth", out var authClaim)
            && TryGetNonEmptyString(authClaim, "chatgpt_account_id") is { } authAccountId)
        {
            return authAccountId;
        }

        if (claims.TryGetProperty("organizations", out var organizations)
            && organizations.ValueKind == JsonValueKind.Array)
        {
            foreach (var organization in organizations.EnumerateArray())
            {
                if (TryGetNonEmptyString(organization, "id") is { } organizationId)
                {
                    return organizationId;
                }
            }
        }

        return null;
    }

    private static string? TryGetNonEmptyString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()
            : null;

    private static JsonDocument ParsePayload(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            throw new FormatException("The token is not a JWT.");
        }

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        var payloadBytes = Convert.FromBase64String(PadBase64(payload));
        return JsonDocument.Parse(payloadBytes);
    }

    private static string PadBase64(string value)
        => value.PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
}
