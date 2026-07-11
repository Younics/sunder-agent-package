using System.Text.Json;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.OpenAI.Auth;

internal sealed class CodexSessionStore(IPackageSecrets secrets)
{
    private const string SessionSecretKey = "auth.codex.session";
    private readonly IPackageSecrets _secrets = secrets;
    private readonly object _gate = new();

    public OpenAiCodexSession? Get()
    {
        lock (_gate)
        {
            var payload = _secrets.GetSecret(SessionSecretKey);
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
    }

    public void Save(OpenAiCodexSession session)
    {
        lock (_gate)
        {
            _secrets.SetSecret(SessionSecretKey, JsonSerializer.Serialize(session));
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _secrets.DeleteSecret(SessionSecretKey);
        }
    }
}
