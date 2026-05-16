using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sunder.Package.Agent.Mcp;

internal sealed record ParsedMcpServerConfiguration(
    ConfiguredMcpServerRecord Server,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> EnvironmentVariables);

internal static class McpConfigurationDocument
{
    private static readonly JsonDocumentOptions JsonDocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string CreateLocalTemplate()
        => Serialize(new
        {
            type = "local",
            enabled = true,
            command = new[] { "npx", "-y", "@modelcontextprotocol/server-everything" },
        });

    public static string CreateRemoteTemplate()
        => Serialize(new
        {
            type = "remote",
            enabled = true,
            url = "https://my-mcp-server.com",
            headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer MY_API_KEY",
            },
        });

    public static ParsedMcpServerConfiguration Parse(
        string serverId,
        string normalizedName,
        string rawJson,
        ConfiguredMcpServerRecord? existingServer = null)
    {
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new InvalidOperationException("Machine name is required.");
        }

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            throw new InvalidOperationException("Paste an MCP server object before saving.");
        }

        using var document = JsonDocument.Parse(rawJson, JsonDocumentOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("MCP configuration must be a JSON object.");
        }

        if (document.RootElement.TryGetProperty("mcp", out _)
            || document.RootElement.TryGetProperty("$schema", out _))
        {
            throw new InvalidOperationException("Paste only the bare MCP server object, not the outer Sunder config wrapper.");
        }

        var parsed = ParseDocument(document.RootElement);
        var now = DateTimeOffset.UtcNow;
        return new ParsedMcpServerConfiguration(
            new ConfiguredMcpServerRecord
            {
                ServerId = serverId,
                Name = normalizedName,
                DisplayName = string.IsNullOrWhiteSpace(parsed.DisplayName) ? normalizedName : parsed.DisplayName.Trim(),
                Description = string.IsNullOrWhiteSpace(parsed.Description) ? null : parsed.Description.Trim(),
                IsEnabled = parsed.Enabled,
                TransportType = parsed.TransportType,
                CommandParts = parsed.CommandParts,
                WorkingDirectory = string.IsNullOrWhiteSpace(parsed.WorkingDirectory) ? null : parsed.WorkingDirectory.Trim(),
                EndpointUrl = string.IsNullOrWhiteSpace(parsed.EndpointUrl) ? null : parsed.EndpointUrl.Trim(),
                TimeoutMilliseconds = parsed.LegacyTimeoutMilliseconds,
                DiscoveryTimeoutMilliseconds = parsed.DiscoveryTimeoutMilliseconds,
                ToolTimeoutMilliseconds = parsed.ToolTimeoutMilliseconds,
                HeaderNames = [.. parsed.Headers.Keys],
                EnvironmentVariableNames = [.. parsed.EnvironmentVariables.Keys],
                CreatedAtUtc = existingServer?.CreatedAtUtc ?? now,
                UpdatedAtUtc = now,
            },
            parsed.Headers,
            parsed.EnvironmentVariables);
    }

    public static string BuildEditorText(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables)
    {
        var displayName = string.Equals(server.DisplayName, server.Name, StringComparison.OrdinalIgnoreCase)
            ? null
            : server.DisplayName;
        var legacyTimeout = server.TimeoutMilliseconds is not null
                            && server.DiscoveryTimeoutMilliseconds is null
                            && server.ToolTimeoutMilliseconds is null
            ? server.TimeoutMilliseconds
            : null;

        object payload = server.TransportType switch
        {
            ConfiguredMcpTransportType.Stdio => new
            {
                type = "local",
                enabled = server.IsEnabled,
                command = server.CommandParts,
                environment = environmentVariables.Count == 0 ? null : environmentVariables,
                timeout = legacyTimeout,
                discoveryTimeout = server.DiscoveryTimeoutMilliseconds,
                toolTimeout = server.ToolTimeoutMilliseconds,
                workingDirectory = server.WorkingDirectory,
                displayName,
                description = server.Description,
            },
            ConfiguredMcpTransportType.HttpSse => new
            {
                type = "remote",
                url = server.EndpointUrl,
                enabled = server.IsEnabled,
                headers = headers.Count == 0 ? null : headers,
                timeout = legacyTimeout,
                discoveryTimeout = server.DiscoveryTimeoutMilliseconds,
                toolTimeout = server.ToolTimeoutMilliseconds,
                displayName,
                description = server.Description,
            },
            _ => throw new InvalidOperationException($"Unsupported MCP transport '{server.TransportType}'."),
        };

        return Serialize(payload);
    }

    private static ParsedEditorDocument ParseDocument(JsonElement root)
    {
        var type = ReadRequiredString(root, "type").Trim().ToLowerInvariant();
        if (root.TryGetProperty("oauth", out _))
        {
            throw new InvalidOperationException("The 'oauth' block is not supported yet. Use direct headers for now.");
        }

        return type switch
        {
            "local" => ParseLocal(root),
            "remote" => ParseRemote(root),
            _ => throw new InvalidOperationException("Unsupported MCP server type. Use 'local' or 'remote'."),
        };
    }

    private static ParsedEditorDocument ParseLocal(JsonElement root)
    {
        var commandElement = ReadRequiredProperty(root, "command");
        if (commandElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Local MCP configuration requires 'command' to be an array of strings.");
        }

        var commandParts = new List<string>();
        foreach (var item in commandElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new InvalidOperationException("Local MCP 'command' array must contain only non-empty strings.");
            }

            commandParts.Add(item.GetString()!.Trim());
        }

        if (commandParts.Count == 0)
        {
            throw new InvalidOperationException("Local MCP configuration requires at least one command segment.");
        }

        var legacyTimeoutMilliseconds = ReadOptionalPositiveInt(root, "timeout");
        var discoveryTimeoutMilliseconds = ReadOptionalPositiveInt(root, "discoveryTimeout") ?? legacyTimeoutMilliseconds;
        var toolTimeoutMilliseconds = ReadOptionalPositiveInt(root, "toolTimeout") ?? legacyTimeoutMilliseconds;

        return new ParsedEditorDocument(
            ConfiguredMcpTransportType.Stdio,
            ReadOptionalBool(root, "enabled") ?? true,
            [.. commandParts],
            ReadOptionalString(root, "workingDirectory"),
            EndpointUrl: null,
            Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            EnvironmentVariables: ReadStringMap(root, "environment"),
            legacyTimeoutMilliseconds,
            discoveryTimeoutMilliseconds,
            toolTimeoutMilliseconds,
            ReadOptionalString(root, "displayName"),
            ReadOptionalString(root, "description"));
    }

    private static ParsedEditorDocument ParseRemote(JsonElement root)
    {
        var endpointUrl = ReadRequiredString(root, "url").Trim();
        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("Remote MCP configuration requires 'url' to be an absolute URL.");
        }

        var legacyTimeoutMilliseconds = ReadOptionalPositiveInt(root, "timeout");
        var discoveryTimeoutMilliseconds = ReadOptionalPositiveInt(root, "discoveryTimeout") ?? legacyTimeoutMilliseconds;
        var toolTimeoutMilliseconds = ReadOptionalPositiveInt(root, "toolTimeout") ?? legacyTimeoutMilliseconds;

        return new ParsedEditorDocument(
            ConfiguredMcpTransportType.HttpSse,
            ReadOptionalBool(root, "enabled") ?? true,
            CommandParts: [],
            WorkingDirectory: null,
            endpointUrl,
            ReadStringMap(root, "headers"),
            EnvironmentVariables: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            legacyTimeoutMilliseconds,
            discoveryTimeoutMilliseconds,
            toolTimeoutMilliseconds,
            ReadOptionalString(root, "displayName"),
            ReadOptionalString(root, "description"));
    }

    private static JsonElement ReadRequiredProperty(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            throw new InvalidOperationException($"MCP configuration is missing required property '{propertyName}'.");
        }

        return value;
    }

    private static string ReadRequiredString(JsonElement root, string propertyName)
    {
        var value = ReadOptionalString(root, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"MCP configuration is missing required property '{propertyName}'.");
        }

        return value;
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new InvalidOperationException($"'{propertyName}' must be a string.");
    }

    private static bool? ReadOptionalBool(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidOperationException($"'{propertyName}' must be a boolean."),
        };
    }

    private static int? ReadOptionalPositiveInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var parsedValue) || parsedValue <= 0)
        {
            throw new InvalidOperationException($"'{propertyName}' must be a positive integer.");
        }

        return parsedValue;
    }

    private static Dictionary<string, string> ReadStringMap(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"'{propertyName}' must be an object of string values.");
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException($"'{propertyName}.{property.Name}' must be a string value.");
            }

            if (string.IsNullOrWhiteSpace(property.Name) || string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                throw new InvalidOperationException($"'{propertyName}' entries must use non-empty names and values.");
            }

            result[property.Name] = property.Value.GetString()!.Trim();
        }

        return result;
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonSerializerOptions);

    private sealed record ParsedEditorDocument(
        ConfiguredMcpTransportType TransportType,
        bool Enabled,
        string[] CommandParts,
        string? WorkingDirectory,
        string? EndpointUrl,
        IReadOnlyDictionary<string, string> Headers,
        IReadOnlyDictionary<string, string> EnvironmentVariables,
        int? LegacyTimeoutMilliseconds,
        int? DiscoveryTimeoutMilliseconds,
        int? ToolTimeoutMilliseconds,
        string? DisplayName,
        string? Description);
}
