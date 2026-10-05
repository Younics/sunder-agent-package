using System.Text.Json;
using Sunder.Package.Agent.Shared.Importing;

namespace Sunder.Package.Agent.Mcp.Services;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class McpConfigurationSourceReader
{
    internal const int MaxDocumentBytes = 2 * 1024 * 1024;
    internal const int MaxJsonDepth = 32;

    public static Task<string> ReadTextAsync(
        string filePath,
        long maxBytes,
        CancellationToken cancellationToken)
        => BoundedImportIO.ReadUtf8FileAsync(filePath, maxBytes, cancellationToken);

    public static async Task<JsonDocument> ReadAsync(string filePath, CancellationToken cancellationToken)
    {
        var json = await ReadTextAsync(filePath, MaxDocumentBytes, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = MaxJsonDepth,
            });
        }
        catch (JsonException exception)
        {
            throw new McpManagementOperationException(
                "The MCP configuration file contains invalid JSON.",
                exception);
        }
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

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class McpJsonShapeValidator
{
    public static void RejectDuplicateProperties(JsonElement root)
        => Visit(root, "$", depth: 0);

    private static void Visit(JsonElement value, string path, int depth)
    {
        if (depth > McpConfigurationSourceReader.MaxJsonDepth)
        {
            throw new McpManagementOperationException(
                $"MCP JSON exceeds the {McpConfigurationSourceReader.MaxJsonDepth}-level depth limit.");
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.TryAdd(property.Name, property.Name))
                {
                    throw new McpManagementOperationException(
                        $"MCP JSON contains duplicate or case-colliding property '{property.Name}' at '{path}' (first seen as '{names[property.Name]}').");
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

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record McpServerCatalogWrite(
    ConfiguredMcpServerRecord Server,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    bool RetainMissingSecretValues = false);
