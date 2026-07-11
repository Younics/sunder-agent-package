using System.Text.Json;

namespace Sunder.Package.Agent.Provider.OpenAI.Auth;

internal sealed class CodexOAuthTokenParser(TimeProvider timeProvider)
{
    private readonly TimeProvider _timeProvider = timeProvider;

    public OpenAiCodexSession? TryParse(string payload, string? fallbackRefreshToken = null)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!TryGetString(root, "access_token", out var accessToken)
                || !root.TryGetProperty("expires_in", out var expiresInElement)
                || expiresInElement.ValueKind != JsonValueKind.Number
                || !expiresInElement.TryGetInt32(out var expiresIn))
            {
                return null;
            }

            var refreshToken = TryGetString(root, "refresh_token", out var replacementRefreshToken)
                ? replacementRefreshToken
                : fallbackRefreshToken;
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                return null;
            }

            var accountId = TryGetString(root, "id_token", out var idToken)
                ? CodexAccountClaimReader.ExtractAccountId(idToken) ?? CodexAccountClaimReader.ExtractAccountId(accessToken)
                : CodexAccountClaimReader.ExtractAccountId(accessToken);
            if (string.IsNullOrWhiteSpace(accountId))
            {
                return null;
            }

            return new OpenAiCodexSession(
                accessToken,
                refreshToken,
                _timeProvider.GetUtcNow().AddSeconds(expiresIn),
                accountId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            return false;
        }

        value = property.GetString()!;
        return true;
    }
}
