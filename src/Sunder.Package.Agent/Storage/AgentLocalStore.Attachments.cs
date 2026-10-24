using System.Text.Json;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    internal IReadOnlySet<string> ListReferencedAttachmentPaths()
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Kind, StructuredPayloadJson
            FROM AgentTurnItems
            WHERE Kind IN ('Attachment', 'ToolResult') AND StructuredPayloadJson IS NOT NULL;
            """;
        using var reader = command.ExecuteReader();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var payload = reader.GetString(1);
            if (reader.GetString(0) == "ToolResult")
            {
                // Ordinary tool output can be any JSON value. Only metadata
                // adopted by the host can authorize retaining a tool image.
                if (Sunder.Package.Agent.Services.AgentToolService.ReadStoredToolImage(payload) is { } image)
                    paths.Add(image.StorageRelativePath.Replace('\\', '/'));
                continue;
            }
            try
            {
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && (document.RootElement.TryGetProperty("StorageRelativePath", out var path)
                     || document.RootElement.TryGetProperty("storageRelativePath", out path))
                    && path.ValueKind == JsonValueKind.String
                    && path.GetString() is { Length: > 0 } value)
                {
                    paths.Add(value.Replace('\\', '/'));
                }
            }
            catch (JsonException)
            {
                // Invalid legacy attachment metadata cannot authorize retaining an arbitrary file.
            }
        }
        return paths;
    }
}
