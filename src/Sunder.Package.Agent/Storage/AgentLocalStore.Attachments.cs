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
            SELECT StructuredPayloadJson
            FROM AgentTurnItems
            WHERE Kind = 'Attachment' AND StructuredPayloadJson IS NOT NULL;
            """;
        using var reader = command.ExecuteReader();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            try
            {
                using var document = JsonDocument.Parse(reader.GetString(0));
                if ((document.RootElement.TryGetProperty("StorageRelativePath", out var path)
                     || document.RootElement.TryGetProperty("storageRelativePath", out path))
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
