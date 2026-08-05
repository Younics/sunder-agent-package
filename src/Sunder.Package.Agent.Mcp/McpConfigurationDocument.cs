using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Agent.Mcp.Services;

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
        MaxDepth = McpConfigurationSourceReader.MaxJsonDepth,
    };

    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static string CreateLocalTemplate()
        => Serialize(new
        {
            type = "local",
            enabled = true,
            command = new[] { "npx", "-y", "@modelcontextprotocol/server-everything" },
            discoveryTimeout = 5000,
            toolTimeout = 180000,
            env = new Dictionary<string, string>
            {
                ["MY_API_KEY"] = "<insert-your-api-key-here>",
            },
        });

    public static string CreateRemoteTemplate()
        => Serialize(new
        {
            type = "remote",
            enabled = true,
            url = "https://my-mcp-server.com",
            discoveryTimeout = 5000,
            toolTimeout = 180000,
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

        if (rawJson.Length > McpConfigurationSourceReader.MaxDocumentBytes)
        {
            throw new InvalidOperationException($"MCP configuration exceeds the {McpConfigurationSourceReader.MaxDocumentBytes}-character limit.");
        }

        using var document = JsonDocument.Parse(rawJson, JsonDocumentOptions);
        McpJsonShapeValidator.RejectDuplicateProperties(document.RootElement);
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
        var server = new ConfiguredMcpServerRecord
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
            HeaderNames = [.. parsed.Headers.Names],
            EnvironmentVariableNames = [.. parsed.EnvironmentVariables.Names],
            OAuthEnabled = parsed.OAuthEnabled,
            OAuthScopes = parsed.OAuthScopes,
            OAuthClientId = parsed.OAuthClientId,
            CreatedAtUtc = existingServer?.CreatedAtUtc ?? now,
            UpdatedAtUtc = now,
        };
        McpTransportSecurity.ValidateRemoteEndpoint(server, parsed.Headers.Replacements);
        return new ParsedMcpServerConfiguration(
            server,
            parsed.Headers.Replacements,
            parsed.EnvironmentVariables.Replacements);
    }

    public static string BuildRedactedEditorText(ConfiguredMcpServerRecord server)
        => BuildEditorText(
            server,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public static string BuildEditorText(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables)
    {
        var editorHeaders = BuildEditorSecretMap(server.HeaderNames, headers);
        var editorEnvironment = BuildEditorSecretMap(server.EnvironmentVariableNames, environmentVariables);
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
                env = editorEnvironment.Count == 0 ? null : editorEnvironment,
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
                headers = editorHeaders.Count == 0 ? null : editorHeaders,
                timeout = legacyTimeout,
                discoveryTimeout = server.DiscoveryTimeoutMilliseconds,
                toolTimeout = server.ToolTimeoutMilliseconds,
                displayName,
                description = server.Description,
                oauth = BuildOAuthEditorObject(server),
            },
            _ => throw new InvalidOperationException($"Unsupported MCP transport '{server.TransportType}'."),
        };

        return Serialize(payload);
    }

    private static ParsedEditorDocument ParseDocument(JsonElement root)
    {
        var type = ReadRequiredString(root, "type").Trim().ToLowerInvariant();
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
            Headers: ParsedEditorSecretMap.Empty,
            EnvironmentVariables: ReadSecretMap(root, "env", "environment"),
            legacyTimeoutMilliseconds,
            discoveryTimeoutMilliseconds,
            toolTimeoutMilliseconds,
            ReadOptionalString(root, "displayName"),
            ReadOptionalString(root, "description"),
            OAuthEnabled: false,
            OAuthScopes: [],
            OAuthClientId: null);
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
        var oauth = ReadOAuth(root);

        return new ParsedEditorDocument(
            ConfiguredMcpTransportType.HttpSse,
            ReadOptionalBool(root, "enabled") ?? true,
            CommandParts: [],
            WorkingDirectory: null,
            endpointUrl,
            ReadSecretMap(root, "headers"),
            EnvironmentVariables: ParsedEditorSecretMap.Empty,
            legacyTimeoutMilliseconds,
            discoveryTimeoutMilliseconds,
            toolTimeoutMilliseconds,
            ReadOptionalString(root, "displayName"),
            ReadOptionalString(root, "description"),
            oauth.Enabled,
            oauth.Scopes,
            oauth.ClientId);
    }

    private static object? BuildOAuthEditorObject(ConfiguredMcpServerRecord server)
    {
        if (!server.OAuthEnabled)
        {
            return null;
        }

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["enabled"] = true,
        };
        if (server.OAuthScopes.Length > 0)
        {
            payload["scopes"] = server.OAuthScopes;
        }

        if (!string.IsNullOrWhiteSpace(server.OAuthClientId))
        {
            payload["clientId"] = server.OAuthClientId.Trim();
        }

        return payload;
    }

    private static ParsedOAuthDocument ReadOAuth(JsonElement root)
    {
        if (!root.TryGetProperty("oauth", out var value))
        {
            return new ParsedOAuthDocument(false, [], null);
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return new ParsedOAuthDocument(value.GetBoolean(), [], null);
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("'oauth' must be a boolean or object.");
        }

        var enabled = ReadOptionalBool(value, "enabled") ?? true;
        var scopes = ReadOptionalStringArray(value, "scopes");
        var clientId = ReadOptionalString(value, "clientId") ?? ReadOptionalString(value, "client_id");
        return new ParsedOAuthDocument(
            enabled,
            scopes,
            string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim());
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

    private static ParsedEditorSecretMap ReadSecretMap(
        JsonElement root,
        string propertyName,
        string? legacyPropertyName = null)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            if (legacyPropertyName is null || !root.TryGetProperty(legacyPropertyName, out value))
            {
                return ParsedEditorSecretMap.Empty;
            }

            propertyName = legacyPropertyName;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"'{propertyName}' must be an object of string values.");
        }

        var names = new List<string>();
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var uniqueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name))
            {
                throw new InvalidOperationException($"'{propertyName}' entries must use non-empty names.");
            }

            var name = property.Name.Trim();
            if (!uniqueNames.Add(name))
            {
                throw new InvalidOperationException(
                    $"'{propertyName}' contains duplicate or case-colliding name '{name}'.");
            }

            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    $"'{propertyName}.{property.Name}' must be a string value or null.");
            }

            names.Add(name);
            var replacement = property.Value.GetString();
            if (!string.IsNullOrWhiteSpace(replacement))
            {
                replacements[name] = replacement.Trim();
            }
        }

        return new ParsedEditorSecretMap(names, replacements);
    }

    private static IReadOnlyDictionary<string, string> BuildEditorSecretMap(
        IEnumerable<string> names,
        IReadOnlyDictionary<string, string> replacements)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            result[name] = replacements.TryGetValue(name, out var replacement)
                ? replacement
                : string.Empty;
        }

        return result;
    }

    private static string[] ReadOptionalStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var single = value.GetString();
            return string.IsNullOrWhiteSpace(single)
                ? []
                : single.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"'{propertyName}' must be a string or array of strings.");
        }

        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new InvalidOperationException($"'{propertyName}' must contain only non-empty strings.");
            }

            result.Add(item.GetString()!.Trim());
        }

        return [.. result.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonSerializerOptions);

    private sealed record ParsedOAuthDocument(bool Enabled, string[] Scopes, string? ClientId);

    private sealed record ParsedEditorDocument(
        ConfiguredMcpTransportType TransportType,
        bool Enabled,
        string[] CommandParts,
        string? WorkingDirectory,
        string? EndpointUrl,
        ParsedEditorSecretMap Headers,
        ParsedEditorSecretMap EnvironmentVariables,
        int? LegacyTimeoutMilliseconds,
        int? DiscoveryTimeoutMilliseconds,
        int? ToolTimeoutMilliseconds,
        string? DisplayName,
        string? Description,
        bool OAuthEnabled,
        string[] OAuthScopes,
        string? OAuthClientId);

    private sealed record ParsedEditorSecretMap(
        IReadOnlyList<string> Names,
        IReadOnlyDictionary<string, string> Replacements)
    {
        internal static ParsedEditorSecretMap Empty { get; } = new(
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }
}
