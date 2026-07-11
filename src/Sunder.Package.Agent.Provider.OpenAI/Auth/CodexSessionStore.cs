using System.Text.Json;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.OpenAI.Auth;

internal sealed class CodexSessionStore(IPackageSecrets secrets)
{
    private const string SessionSecretKey = "auth.codex.session";
    private readonly IPackageSecrets _secrets = secrets;
    public async Task<OpenAiCodexSession?> GetAsync(CancellationToken cancellationToken = default)
    {
        var payload = await _secrets.GetSecretAsync(SessionSecretKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<OpenAiCodexSession>(payload);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public Task SaveAsync(OpenAiCodexSession session, CancellationToken cancellationToken = default)
        => _secrets.SetSecretAsync(SessionSecretKey, JsonSerializer.Serialize(session), cancellationToken);

    public Task ClearAsync(CancellationToken cancellationToken = default)
        => _secrets.DeleteSecretAsync(SessionSecretKey, cancellationToken);
}
