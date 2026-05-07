namespace Sunder.Package.Agent.Provider.OpenAI.Auth;

public sealed record OpenAiCodexSession(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAtUtc,
    string ChatGptAccountId);
