using System.Text.Json;
using Sunder.Package.Agent.Shared.Importing;

namespace Sunder.Package.Agent.Mcp.Services;

internal static class McpConfigurationSourceReader
{
    internal const int MaxDocumentBytes = 2 * 1024 * 1024;
    internal const int MaxJsonDepth = 32;

    public static async Task<JsonDocument> ReadAsync(string filePath, CancellationToken cancellationToken)
    {
        var json = await BoundedImportIO.ReadUtf8FileAsync(filePath, MaxDocumentBytes, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = MaxJsonDepth,
        });
        try
        {
            McpJsonShapeValidator.RejectDuplicateProperties(document.RootElement);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }
}

internal static class McpJsonShapeValidator
{
    public static void RejectDuplicateProperties(JsonElement root)
        => Visit(root, "$", depth: 0);

    private static void Visit(JsonElement value, string path, int depth)
    {
        if (depth > McpConfigurationSourceReader.MaxJsonDepth)
        {
            throw new InvalidDataException($"MCP JSON exceeds the {McpConfigurationSourceReader.MaxJsonDepth}-level depth limit.");
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.TryAdd(property.Name, property.Name))
                {
                    throw new InvalidDataException($"MCP JSON contains duplicate or case-colliding property '{property.Name}' at '{path}' (first seen as '{names[property.Name]}').");
                }

                Visit(property.Value, path + "." + property.Name, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                Visit(item, $"{path}[{index++}]", depth + 1);
            }
        }
    }
}

internal sealed record McpServerCatalogWrite(
    ConfiguredMcpServerRecord Server,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> EnvironmentVariables);
