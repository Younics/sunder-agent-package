using System.Text;
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
            using var document = JsonDocument.Parse(payload);
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
            if (commandParts.Length == 0)
            {
                commandParts = ReadLegacyCommandParts(root);
            }

            var headerNames = ReadStringArray(root, "HeaderNames");
            if (headerNames.Length == 0)
            {
                headerNames = await ReadLegacyHeaderNamesAsync(secrets, serverId, root, cancellationToken);
            }

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
    }

    private static async Task<string[]> ReadLegacyHeaderNamesAsync(
        IPackageSecrets secrets,
        string serverId,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var names = new List<string>();
        var apiKeyHeaderName = ReadString(root, "ApiKeyHeaderName");
        if (!string.IsNullOrWhiteSpace(apiKeyHeaderName)
            && !string.IsNullOrWhiteSpace(await secrets.GetSecretAsync(
                McpServerCatalogService.BuildApiKeySecretKey(serverId), cancellationToken)))
        {
            names.Add(apiKeyHeaderName.Trim());
        }

        if (!string.IsNullOrWhiteSpace(await secrets.GetSecretAsync(
            McpServerCatalogService.BuildAuthorizationSecretKey(serverId), cancellationToken)))
        {
            names.Add("Authorization");
        }

        return [.. names.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string[] ReadLegacyCommandParts(JsonElement root)
    {
        var command = ReadString(root, "Command");
        return string.IsNullOrWhiteSpace(command)
            ? []
            : [command.Trim(), .. SplitArguments(ReadString(root, "Arguments"))];
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
        => !root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array
            ? []
            : value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                .Select(item => item.GetString()!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static string[] SplitArguments(string? args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            return [];
        }

        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in args)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (ch == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return [.. tokens];
    }

    private static string? TrimOrNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed record McpServerDeserializeResult(
    ConfiguredMcpServerRecord? Server,
    string? Error);
