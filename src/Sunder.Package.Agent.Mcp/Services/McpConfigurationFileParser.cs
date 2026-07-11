using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sunder.Package.Agent.Mcp.Services;

internal sealed record DiscoveredMcpServer(string Name, string Json);

internal sealed record McpConfigurationFileParseResult(
    IReadOnlyList<DiscoveredMcpServer> Servers,
    int SkippedCount,
    IReadOnlyList<string> Warnings);

internal static class McpConfigurationFileParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static McpConfigurationFileParseResult Parse(JsonElement root, string sourcePath, string? defaultName)
    {
        var servers = new List<DiscoveredMcpServer>();
        var warnings = new List<string>();
        var skipped = 0;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new McpConfigurationFileParseResult(servers, skipped, warnings);
        }

        var foundWrapper = false;
        foreach (var propertyName in new[] { "mcp", "mcpServers", "servers" })
        {
            if (!root.TryGetProperty(propertyName, out var map) || map.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foundWrapper = true;
            ParseNamedMap(map, sourcePath, servers, warnings, ref skipped);
        }

        if (!foundWrapper && LooksLikeBareServerObject(root) && !string.IsNullOrWhiteSpace(defaultName))
        {
            if (TryBuildServerJson(defaultName, root, out var json, out var error))
            {
                servers.Add(new DiscoveredMcpServer(defaultName, json));
            }
            else
            {
                skipped++;
                warnings.Add($"Skipped MCP server '{defaultName}' from {Path.GetFileName(sourcePath)}: {error}");
            }
        }

        return new McpConfigurationFileParseResult(servers, skipped, warnings);
    }

    private static void ParseNamedMap(
        JsonElement map,
        string sourcePath,
        ICollection<DiscoveredMcpServer> servers,
        ICollection<string> warnings,
        ref int skipped)
    {
        foreach (var property in map.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                skipped++;
                warnings.Add($"Skipped MCP server '{property.Name}' from {Path.GetFileName(sourcePath)} because its value is not an object.");
            }
            else if (TryBuildServerJson(property.Name, property.Value, out var json, out var error))
            {
                servers.Add(new DiscoveredMcpServer(property.Name, json));
            }
            else
            {
                skipped++;
                warnings.Add($"Skipped MCP server '{property.Name}' from {Path.GetFileName(sourcePath)}: {error}");
            }
        }
    }

    private static bool TryBuildServerJson(string name, JsonElement source, out string json, out string? error)
    {
        json = string.Empty;
        error = null;
        var type = ReadString(source, "type")?.Trim().ToLowerInvariant();
        var enabled = ReadBool(source, "enabled") ?? !(ReadBool(source, "disabled") ?? false);
        if (type is "local" or "stdio" || source.TryGetProperty("command", out _))
        {
            var commandParts = ReadCommandParts(source);
            if (commandParts.Length == 0)
            {
                error = "local server is missing a command.";
                return false;
            }

            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "local",
                ["enabled"] = enabled,
                ["command"] = commandParts,
                ["displayName"] = ReadString(source, "displayName") ?? ReadString(source, "name") ?? name,
            };
            AddIfNotNull(payload, "env", ReadStringMap(source, "env") ?? ReadStringMap(source, "environment"));
            AddIfNotNull(payload, "workingDirectory", ReadString(source, "workingDirectory") ?? ReadString(source, "cwd"));
            AddIfNotNull(payload, "description", ReadString(source, "description"));
            json = Serialize(payload);
            return true;
        }

        if (type is "remote" or "sse" or "http" or "streamable-http" || source.TryGetProperty("url", out _))
        {
            var url = ReadString(source, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                error = "remote server is missing a url.";
                return false;
            }

            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "remote",
                ["enabled"] = enabled,
                ["url"] = url.Trim(),
                ["displayName"] = ReadString(source, "displayName") ?? ReadString(source, "name") ?? name,
            };
            AddIfNotNull(payload, "headers", ReadStringMap(source, "headers"));
            AddIfNotNull(payload, "oauth", ReadOAuth(source));
            AddIfNotNull(payload, "description", ReadString(source, "description"));
            json = Serialize(payload);
            return true;
        }

        error = "server is neither local nor remote.";
        return false;
    }

    private static string[] ReadCommandParts(JsonElement source)
    {
        if (!source.TryGetProperty("command", out var command))
        {
            return [];
        }

        var parts = new List<string>();
        if (command.ValueKind == JsonValueKind.Array)
        {
            parts.AddRange(command.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                .Select(item => item.GetString()!.Trim()));
        }
        else if (command.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(command.GetString()))
        {
            parts.Add(command.GetString()!.Trim());
        }

        if (source.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
        {
            parts.AddRange(args.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                .Select(item => item.GetString()!.Trim()));
        }

        return [.. parts];
    }

    private static Dictionary<string, string>? ReadStringMap(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = value.EnumerateObject()
            .Where(property => property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()))
            .ToDictionary(property => property.Name, property => property.Value.GetString()!.Trim(), StringComparer.OrdinalIgnoreCase);
        return result.Count == 0 ? null : result;
    }

    private static object? ReadOAuth(JsonElement source)
        => !source.TryGetProperty("oauth", out var oauth)
            ? null
            : oauth.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Object => JsonSerializer.Deserialize<Dictionary<string, object?>>(oauth.GetRawText(), SerializerOptions),
                _ => null,
            };

    private static string? ReadString(JsonElement source, string propertyName)
        => source.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? ReadBool(JsonElement source, string propertyName)
        => source.TryGetProperty(propertyName, out var value)
            ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

    private static void AddIfNotNull(IDictionary<string, object?> target, string key, object? value)
    {
        if (value is not null)
        {
            target[key] = value;
        }
    }

    private static bool LooksLikeBareServerObject(JsonElement root)
        => root.TryGetProperty("type", out _) || root.TryGetProperty("command", out _) || root.TryGetProperty("url", out _);

    private static string Serialize(object value) => JsonSerializer.Serialize(value, SerializerOptions);
}
