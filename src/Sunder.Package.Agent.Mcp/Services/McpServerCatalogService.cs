using System.Text;
using System.Text.Json;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Mcp.Services;

public sealed class McpServerCatalogService(IPackageContext packageContext)
{
    private const string ServerKeyPrefix = "mcp.servers.";

    private readonly IPackageContext _packageContext = packageContext;

    public event Action? ServersChanged;

    public async Task<IReadOnlyList<ConfiguredMcpServerRecord>> ListServersAsync(CancellationToken cancellationToken = default)
    {
        var keys = await _packageContext.Storage.State.ListKeysAsync(ServerKeyPrefix, cancellationToken);
        var servers = new List<ConfiguredMcpServerRecord>();
        foreach (var key in keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
        {
            var payload = await _packageContext.Storage.State.GetValueAsync(key, cancellationToken);
            if (string.IsNullOrWhiteSpace(payload))
            {
                continue;
            }

            var server = DeserializeServer(payload);
            if (server is not null)
            {
                servers.Add(server);
            }
        }

        return servers
            .OrderBy(server => server.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<ConfiguredMcpServerRecord?> GetServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        var payload = await _packageContext.Storage.State.GetValueAsync(BuildServerKey(serverId), cancellationToken);
        return string.IsNullOrWhiteSpace(payload)
            ? null
            : DeserializeServer(payload);
    }

    public async Task<string?> ExportServerJsonAsync(string serverId, CancellationToken cancellationToken = default)
    {
        var server = await GetServerAsync(serverId, cancellationToken);
        return server is null ? null : McpConfigurationDocument.BuildEditorText(server, GetHeaders(server), GetEnvironmentVariables(server));
    }

    public async Task SaveServerAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        CancellationToken cancellationToken = default)
    {
        var existing = await GetServerAsync(server.ServerId, cancellationToken);

        await _packageContext.Storage.State.SetValueAsync(
            BuildServerKey(server.ServerId),
            JsonSerializer.Serialize(server),
            cancellationToken);

        foreach (var headerName in existing?.HeaderNames.Except(server.HeaderNames, StringComparer.OrdinalIgnoreCase) ?? [])
        {
            _packageContext.Secrets.DeleteSecret(BuildHeaderSecretKey(server.ServerId, headerName));
        }

        foreach (var variableName in existing?.EnvironmentVariableNames.Except(server.EnvironmentVariableNames, StringComparer.OrdinalIgnoreCase) ?? [])
        {
            _packageContext.Secrets.DeleteSecret(BuildEnvironmentSecretKey(server.ServerId, variableName));
        }

        foreach (var headerName in server.HeaderNames)
        {
            SetOptionalSecret(BuildHeaderSecretKey(server.ServerId, headerName), headers.TryGetValue(headerName, out var value) ? value : null);
        }

        foreach (var variableName in server.EnvironmentVariableNames)
        {
            SetOptionalSecret(BuildEnvironmentSecretKey(server.ServerId, variableName), environmentVariables.TryGetValue(variableName, out var value) ? value : null);
        }

        // Clear legacy special-case secrets once the normalized config has been saved.
        _packageContext.Secrets.DeleteSecret(BuildApiKeySecretKey(server.ServerId));
        _packageContext.Secrets.DeleteSecret(BuildAuthorizationSecretKey(server.ServerId));
        ServersChanged?.Invoke();
    }

    public async Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        var existing = await GetServerAsync(serverId, cancellationToken);
        await _packageContext.Storage.State.DeleteValueAsync(BuildServerKey(serverId), cancellationToken);

        foreach (var headerName in existing?.HeaderNames ?? [])
        {
            _packageContext.Secrets.DeleteSecret(BuildHeaderSecretKey(serverId, headerName));
        }

        foreach (var variableName in existing?.EnvironmentVariableNames ?? [])
        {
            _packageContext.Secrets.DeleteSecret(BuildEnvironmentSecretKey(serverId, variableName));
        }

        _packageContext.Secrets.DeleteSecret(BuildApiKeySecretKey(serverId));
        _packageContext.Secrets.DeleteSecret(BuildAuthorizationSecretKey(serverId));
        ServersChanged?.Invoke();
    }

    public IReadOnlyDictionary<string, string> GetHeaders(ConfiguredMcpServerRecord server)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var headerName in server.HeaderNames)
        {
            var value = _packageContext.Secrets.GetSecret(BuildHeaderSecretKey(server.ServerId, headerName));
            if (string.IsNullOrWhiteSpace(value) && string.Equals(headerName, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                value = _packageContext.Secrets.GetSecret(BuildAuthorizationSecretKey(server.ServerId));
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                value = _packageContext.Secrets.GetSecret(BuildApiKeySecretKey(server.ServerId));
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                headers[headerName] = value.Trim();
            }
        }

        return headers;
    }

    public IReadOnlyDictionary<string, string> GetEnvironmentVariables(ConfiguredMcpServerRecord server)
    {
        var environmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variableName in server.EnvironmentVariableNames)
        {
            var value = _packageContext.Secrets.GetSecret(BuildEnvironmentSecretKey(server.ServerId, variableName));
            if (!string.IsNullOrWhiteSpace(value))
            {
                environmentVariables[variableName] = value.Trim();
            }
        }

        return environmentVariables;
    }

    public string NormalizeServerName(string? value)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? "mcp_server" : value.Trim().ToLowerInvariant();
        var builder = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        var normalized = builder.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(normalized) ? "mcp_server" : normalized;
    }

    private ConfiguredMcpServerRecord? DeserializeServer(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var root = document.RootElement;
        var serverId = ReadString(root, "ServerId") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(serverId))
        {
            return null;
        }

        var name = NormalizeServerName(ReadString(root, "Name"));
        var displayName = ReadString(root, "DisplayName");
        var description = ReadString(root, "Description");
        var transportType = ReadTransportType(root);
        var commandParts = ReadStringArray(root, "CommandParts");
        if (commandParts.Length == 0)
        {
            commandParts = ReadLegacyCommandParts(root);
        }

        var headerNames = ReadStringArray(root, "HeaderNames");
        if (headerNames.Length == 0)
        {
            headerNames = ReadLegacyHeaderNames(serverId, root);
        }

        return new ConfiguredMcpServerRecord
        {
            ServerId = serverId,
            Name = name,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            IsEnabled = ReadBool(root, "IsEnabled") ?? true,
            TransportType = transportType,
            CommandParts = commandParts,
            WorkingDirectory = ReadString(root, "WorkingDirectory"),
            EndpointUrl = ReadString(root, "EndpointUrl"),
            TimeoutMilliseconds = ReadInt(root, "TimeoutMilliseconds"),
            DiscoveryTimeoutMilliseconds = ReadInt(root, "DiscoveryTimeoutMilliseconds") ?? ReadInt(root, "TimeoutMilliseconds"),
            ToolTimeoutMilliseconds = ReadInt(root, "ToolTimeoutMilliseconds") ?? ReadInt(root, "TimeoutMilliseconds"),
            HeaderNames = headerNames,
            EnvironmentVariableNames = ReadStringArray(root, "EnvironmentVariableNames"),
            CreatedAtUtc = ReadDateTimeOffset(root, "CreatedAtUtc") ?? DateTimeOffset.UtcNow,
            UpdatedAtUtc = ReadDateTimeOffset(root, "UpdatedAtUtc") ?? DateTimeOffset.UtcNow,
        };
    }

    private string[] ReadLegacyHeaderNames(string serverId, JsonElement root)
    {
        var headerNames = new List<string>();
        var apiKeyHeaderName = ReadString(root, "ApiKeyHeaderName");
        if (!string.IsNullOrWhiteSpace(apiKeyHeaderName)
            && !string.IsNullOrWhiteSpace(_packageContext.Secrets.GetSecret(BuildApiKeySecretKey(serverId))))
        {
            headerNames.Add(apiKeyHeaderName.Trim());
        }

        if (!string.IsNullOrWhiteSpace(_packageContext.Secrets.GetSecret(BuildAuthorizationSecretKey(serverId))))
        {
            headerNames.Add("Authorization");
        }

        return [.. headerNames.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string[] ReadLegacyCommandParts(JsonElement root)
    {
        var command = ReadString(root, "Command");
        if (string.IsNullOrWhiteSpace(command))
        {
            return [];
        }

        return [command.Trim(), .. SplitArguments(ReadString(root, "Arguments"))];
    }

    private void SetOptionalSecret(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _packageContext.Secrets.DeleteSecret(key);
            return;
        }

        _packageContext.Secrets.SetSecret(key, value.Trim());
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

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var enumValue) && Enum.IsDefined(typeof(ConfiguredMcpTransportType), enumValue))
        {
            return (ConfiguredMcpTransportType)enumValue;
        }

        if (value.ValueKind == JsonValueKind.String && Enum.TryParse<ConfiguredMcpTransportType>(value.GetString(), ignoreCase: true, out var parsedValue))
        {
            return parsedValue;
        }

        return ConfiguredMcpTransportType.Stdio;
    }

    private static string[] ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            .Select(item => item.GetString()!.Trim())
            .ToArray();
    }

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
                continue;
            }

            if (ch == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return [.. tokens];
    }

    private static string BuildServerKey(string serverId) => ServerKeyPrefix + serverId;

    private static string BuildApiKeySecretKey(string serverId) => $"mcp.servers.{serverId}.apiKey";

    private static string BuildAuthorizationSecretKey(string serverId) => $"mcp.servers.{serverId}.authorization";

    private static string BuildHeaderSecretKey(string serverId, string headerName)
        => $"mcp.servers.{serverId}.headers.{Uri.EscapeDataString(headerName)}";

    private static string BuildEnvironmentSecretKey(string serverId, string variableName)
        => $"mcp.servers.{serverId}.environment.{Uri.EscapeDataString(variableName)}";
}
