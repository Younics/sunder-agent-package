using System.Text.Json;

namespace Sunder.Package.Agent.Provider.OpenAI.Auth;

internal sealed class CodexOAuthTokenParser(TimeProvider timeProvider)
{
    private readonly TimeProvider _timeProvider = timeProvider;

    public OpenAiCodexSession? TryParse(string payload, OpenAiCodexSession? existingSession = null)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var replacementAccessToken = TryGetString(root, "access_token", out var parsedAccessToken)
                ? parsedAccessToken
                : null;
            var accessToken = replacementAccessToken ?? existingSession?.AccessToken;
            var idToken = TryGetString(root, "id_token", out var parsedIdToken)
                ? parsedIdToken
                : null;
            var refreshToken = TryGetString(root, "refresh_token", out var replacementRefreshToken)
                ? replacementRefreshToken
                : existingSession?.RefreshToken;
            if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
            {
                return null;
            }

            var accountId = idToken is null ? null : CodexAccountClaimReader.ExtractAccountId(idToken);
            accountId ??= replacementAccessToken is null
                ? null
                : CodexAccountClaimReader.ExtractAccountId(replacementAccessToken);
            accountId ??= existingSession?.ChatGptAccountId;
            var hasReplacementExpiration = TryGetExpiration(
                root,
                replacementAccessToken,
                idToken,
                out var replacementExpiresAtUtc);
            var expiresAtUtc = hasReplacementExpiration
                ? replacementExpiresAtUtc
                : existingSession?.ExpiresAtUtc;
            if (string.IsNullOrWhiteSpace(accountId) || expiresAtUtc is null)
            {
                return null;
            }

            return new OpenAiCodexSession(
                accessToken,
                refreshToken,
                expiresAtUtc.Value,
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

    private bool TryGetExpiration(
        JsonElement root,
        string? accessToken,
        string? idToken,
        out DateTimeOffset expiresAtUtc)
    {
        var now = _timeProvider.GetUtcNow();
        if (root.TryGetProperty("expires_in", out var expiresInElement)
            && expiresInElement.ValueKind == JsonValueKind.Number
            && expiresInElement.TryGetInt64(out var expiresIn)
            && expiresIn > 0)
        {
            try
            {
                expiresAtUtc = now.AddSeconds(expiresIn);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        var jwtExpiration = (accessToken is null ? null : CodexAccountClaimReader.ExtractExpiration(accessToken))
            ?? (idToken is null ? null : CodexAccountClaimReader.ExtractExpiration(idToken));
        if (jwtExpiration is not null && jwtExpiration > now)
        {
            expiresAtUtc = jwtExpiration.Value;
            return true;
        }

        expiresAtUtc = default;
        return false;
    }
}
