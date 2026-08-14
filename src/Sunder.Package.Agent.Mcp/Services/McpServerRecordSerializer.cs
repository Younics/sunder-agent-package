using System.Text.Json;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Mcp.Services;

internal static class McpServerRecordSerializer
{
    public static async Task<McpServerDeserializeResult> DeserializeAsync(
        string payload,
        IPackageSecrets secrets,
        Func<string?, string> normalizeName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (payload.Length > McpConfigurationSourceReader.MaxDocumentBytes)
            {
                return new(null, "Stored MCP server metadata exceeds the size limit.");
            }

            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                MaxDepth = McpConfigurationSourceReader.MaxJsonDepth,
            });
            McpJsonShapeValidator.RejectDuplicateProperties(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new(null, "Stored MCP server metadata is not a JSON object.");
            }

            var root = document.RootElement;
            var serverId = ReadString(root, "ServerId") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(serverId))
            {
                return new(null, "Stored MCP server metadata is missing ServerId.");
            }

            var name = normalizeName(ReadString(root, "Name"));
            var displayName = ReadString(root, "DisplayName");
            var commandParts = ReadStringArray(root, "CommandParts");
            var headerNames = ReadStringArray(root, "HeaderNames");

            var server = new ConfiguredMcpServerRecord
            {
                PersistenceVersion = ReadInt(root, "PersistenceVersion") ?? 0,
                ServerId = serverId,
                Name = name,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName.Trim(),
                Description = TrimOrNull(ReadString(root, "Description")),
                IsEnabled = ReadBool(root, "IsEnabled") ?? true,
                TransportType = ReadTransportType(root),
                CommandParts = commandParts,
                WorkingDirectory = TrimOrNull(ReadString(root, "WorkingDirectory")),
                EndpointUrl = TrimOrNull(ReadString(root, "EndpointUrl")),
                TimeoutMilliseconds = ReadInt(root, "TimeoutMilliseconds"),
                DiscoveryTimeoutMilliseconds = ReadInt(root, "DiscoveryTimeoutMilliseconds") ?? ReadInt(root, "TimeoutMilliseconds"),
                ToolTimeoutMilliseconds = ReadInt(root, "ToolTimeoutMilliseconds") ?? ReadInt(root, "TimeoutMilliseconds"),
                HeaderNames = headerNames,
                EnvironmentVariableNames = ReadStringArray(root, "EnvironmentVariableNames"),
                OAuthEnabled = ReadBool(root, "OAuthEnabled") ?? false,
                OAuthScopes = ReadStringArray(root, "OAuthScopes"),
                OAuthClientId = TrimOrNull(ReadString(root, "OAuthClientId")),
                SourceKind = TrimOrNull(ReadString(root, "SourceKind")),
                SourceUri = TrimOrNull(ReadString(root, "SourceUri")),
                SourceName = TrimOrNull(ReadString(root, "SourceName")),
                LastImportedHash = TrimOrNull(ReadString(root, "LastImportedHash")),
                IsExternallyManaged = ReadBool(root, "IsExternallyManaged") ?? false,
                CreatedAtUtc = ReadDateTimeOffset(root, "CreatedAtUtc") ?? DateTimeOffset.UtcNow,
                UpdatedAtUtc = ReadDateTimeOffset(root, "UpdatedAtUtc") ?? DateTimeOffset.UtcNow,
            };
            return new(server, null);
        }
        catch (JsonException ex)
        {
            return new(null, $"Stored MCP server metadata is malformed JSON: {ex.Message}");
        }
        catch (InvalidDataException ex)
        {
            return new(null, ex.Message);
        }
    }

    private static string? ReadString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? ReadBool(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    private static int? ReadInt(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var parsed)
            ? parsed
            : null;

    private static ConfiguredMcpTransportType ReadTransportType(JsonElement root)
    {
        if (!root.TryGetProperty("TransportType", out var value))
        {
            return ConfiguredMcpTransportType.Stdio;
        }

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var enumValue)
            && Enum.IsDefined(typeof(ConfiguredMcpTransportType), enumValue))
        {
            return (ConfiguredMcpTransportType)enumValue;
        }

        return value.ValueKind == JsonValueKind.String
               && Enum.TryParse<ConfiguredMcpTransportType>(value.GetString(), ignoreCase: true, out var parsed)
            ? parsed
            : ConfiguredMcpTransportType.Stdio;
    }

    private static string[] ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                continue;
            }

            var parsed = item.GetString()!.Trim();
            if (!unique.Add(parsed))
            {
                throw new InvalidDataException($"Stored MCP server metadata contains duplicate or case-colliding '{propertyName}' value '{parsed}'.");
            }

            result.Add(parsed);
        }

        return [.. result];
    }

    private static string? TrimOrNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed record McpServerDeserializeResult(
    ConfiguredMcpServerRecord? Server,
    string? Error);
