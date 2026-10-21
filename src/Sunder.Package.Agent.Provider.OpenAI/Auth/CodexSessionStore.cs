using System.Text.Json;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.OpenAI.Auth;

internal sealed class CodexSessionStore(IPackageSecrets secrets)
{
    private const string SessionSecretKey = "auth.codex.session";
    internal const string DocumentSchemaId = "younics.ai.agent.provider.openai.codex-session";
    internal const int DocumentSchemaVersion = 1;
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
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasDuplicateProperties(root))
            {
                return null;
            }

            var hasSchemaId = root.TryGetProperty(nameof(CodexSessionDocument.SchemaId), out var schemaId);
            var hasSchemaVersion = root.TryGetProperty(nameof(CodexSessionDocument.SchemaVersion), out var schemaVersion);
            var hasSession = root.TryGetProperty(nameof(CodexSessionDocument.Session), out var sessionElement);
            if (!hasSchemaId
                || !hasSchemaVersion
                || !hasSession
                || schemaId.ValueKind != JsonValueKind.String
                || !string.Equals(schemaId.GetString(), DocumentSchemaId, StringComparison.Ordinal)
                || schemaVersion.ValueKind != JsonValueKind.Number
                || !schemaVersion.TryGetInt32(out var version)
                || version != DocumentSchemaVersion
                || !HasExactProperties(root, nameof(CodexSessionDocument.SchemaId), nameof(CodexSessionDocument.SchemaVersion), nameof(CodexSessionDocument.Session))
                || sessionElement.ValueKind != JsonValueKind.Object
                || !HasExactProperties(sessionElement, nameof(OpenAiCodexSession.AccessToken), nameof(OpenAiCodexSession.RefreshToken), nameof(OpenAiCodexSession.ExpiresAtUtc), nameof(OpenAiCodexSession.ChatGptAccountId)))
            {
                return null;
            }

            var session = sessionElement.Deserialize<OpenAiCodexSession>();
            return IsValid(session) ? session : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public Task SaveAsync(OpenAiCodexSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!IsValid(session))
        {
            throw new ArgumentException("Codex session fields must be complete.", nameof(session));
        }

        return _secrets.SetSecretAsync(
            SessionSecretKey,
            JsonSerializer.Serialize(new CodexSessionDocument(DocumentSchemaId, DocumentSchemaVersion, session)),
            cancellationToken);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
        => _secrets.DeleteSecretAsync(SessionSecretKey, cancellationToken);

    private static bool IsValid(OpenAiCodexSession? session)
        => session is not null
           && !string.IsNullOrWhiteSpace(session.AccessToken)
           && !string.IsNullOrWhiteSpace(session.RefreshToken)
           && session.ExpiresAtUtc != default
           && !string.IsNullOrWhiteSpace(session.ChatGptAccountId);

    private static bool HasExactProperties(JsonElement value, params string[] expected)
    {
        var actual = value.EnumerateObject().Select(static property => property.Name).ToHashSet(StringComparer.Ordinal);
        return actual.Count == expected.Length && actual.SetEquals(expected);
    }

    private static bool HasDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                {
                    return true;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray().Any(HasDuplicateProperties);
        }

        return false;
    }
}

internal sealed record CodexSessionDocument(
    string SchemaId,
    int SchemaVersion,
    OpenAiCodexSession Session);
