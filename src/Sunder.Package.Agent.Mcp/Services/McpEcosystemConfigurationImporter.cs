using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sunder.Package.Agent.Mcp.Services;

public sealed class McpEcosystemConfigurationImporter(McpServerCatalogService serverCatalog)
{
    public const string SunderConfigurationSourceKind = "sunder-config";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public async Task<McpConfigurationImportResult> ImportCommonConfigurationsAsync(CancellationToken cancellationToken = default)
    {
        var result = new MutableImportResult();
        foreach (var path in EnumerateCommonConfigurationPaths().Where(File.Exists).Distinct(StringComparer.Ordinal))
        {
            var imported = await ImportFileAsync(path, cancellationToken).ConfigureAwait(false);
            result.Add(imported);
        }

        return result.ToResult();
    }

    public async Task<McpConfigurationImportResult> ImportFileAsync(string filePath, CancellationToken cancellationToken = default)
        => await ImportFileCoreAsync(filePath, McpConfigurationImportOptions.Manual, cancellationToken).ConfigureAwait(false);

    public async Task<McpConfigurationImportResult> ImportSunderConfigurationFileAsync(string filePath, CancellationToken cancellationToken = default)
        => await ImportFileCoreAsync(
            filePath,
            McpConfigurationImportOptions.SunderManaged(GetCanonicalSourceUri(filePath)),
            cancellationToken).ConfigureAwait(false);

    private async Task<McpConfigurationImportResult> ImportFileCoreAsync(
        string filePath,
        McpConfigurationImportOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            throw new InvalidOperationException("Select an existing MCP configuration file.");
        }

        var servers = await serverCatalog.ListServersAsync(cancellationToken).ConfigureAwait(false);
        var existingByName = servers.ToDictionary(server => server.Name, StringComparer.OrdinalIgnoreCase);
        var result = new MutableImportResult();

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false), DocumentOptions);
        var discoveredServers = EnumerateServerObjects(document.RootElement, filePath, result, Path.GetFileNameWithoutExtension(filePath)).ToArray();
        var sourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var discovered in discoveredServers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var normalizedName = serverCatalog.NormalizeServerName(discovered.Name);
                var sourceName = string.IsNullOrWhiteSpace(discovered.Name) ? normalizedName : discovered.Name.Trim();
                sourceNames.Add(sourceName);
                existingByName.TryGetValue(normalizedName, out var existing);
                if (options.IsExternallyManaged && existing is not null && !CanUpdateManagedServer(existing, options))
                {
                    result.Skipped++;
                    result.Warnings.Add($"Skipped MCP server '{discovered.Name}' from {Path.GetFileName(filePath)} because an MCP server named '{normalizedName}' already exists outside this Sunder config source.");
                    continue;
                }

                var importHash = ComputeImportHash(normalizedName, discovered.Json);
                if (options.IsExternallyManaged
                    && existing is not null
                    && string.Equals(existing.LastImportedHash, importHash, StringComparison.OrdinalIgnoreCase)
                    && CanUpdateManagedServer(existing, options))
                {
                    existingByName[normalizedName] = existing;
                    continue;
                }

                var parsed = McpConfigurationDocument.Parse(
                    existing?.ServerId ?? Guid.NewGuid().ToString("N"),
                    normalizedName,
                    discovered.Json,
                    existing);
                var server = options.IsExternallyManaged
                    ? parsed.Server with
                    {
                        SourceKind = options.SourceKind,
                        SourceUri = options.SourceUri,
                        SourceName = sourceName,
                        LastImportedHash = importHash,
                        IsExternallyManaged = true,
                    }
                    : parsed.Server;
                await serverCatalog.SaveServerAsync(server, parsed.Headers, parsed.EnvironmentVariables, cancellationToken).ConfigureAwait(false);
                existingByName[normalizedName] = server;
                result.Imported++;
            }
            catch (Exception ex)
            {
                result.Skipped++;
                result.Warnings.Add($"Skipped MCP server '{discovered.Name}' from {Path.GetFileName(filePath)}: {ex.Message}");
            }
        }

        if (options.DeleteMissingFromSource && options.SourceUri is not null)
        {
            await DeleteMissingManagedServersAsync(options, sourceNames, cancellationToken).ConfigureAwait(false);
        }

        return result.ToResult();
    }

    private async Task DeleteMissingManagedServersAsync(
        McpConfigurationImportOptions options,
        ISet<string> sourceNames,
        CancellationToken cancellationToken)
    {
        var servers = await serverCatalog.ListServersAsync(cancellationToken).ConfigureAwait(false);
        foreach (var server in servers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!server.IsExternallyManaged
                || !string.Equals(server.SourceKind, options.SourceKind, StringComparison.OrdinalIgnoreCase)
                || !SourceUriEquals(server.SourceUri, options.SourceUri))
            {
                continue;
            }

            var sourceName = string.IsNullOrWhiteSpace(server.SourceName) ? server.Name : server.SourceName;
            if (!sourceNames.Contains(sourceName))
            {
                await serverCatalog.DeleteServerAsync(server.ServerId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static IEnumerable<DiscoveredMcpServer> EnumerateServerObjects(JsonElement root, string sourcePath, MutableImportResult result, string? defaultName = null)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        var yieldedWrappedServers = false;
        if (root.TryGetProperty("mcp", out var opencodeMcp) && opencodeMcp.ValueKind == JsonValueKind.Object)
        {
            foreach (var server in EnumerateNamedServerMap(opencodeMcp, sourcePath, result))
            {
                yieldedWrappedServers = true;
                yield return server;
            }
        }

        if (root.TryGetProperty("mcpServers", out var mcpServers) && mcpServers.ValueKind == JsonValueKind.Object)
        {
            foreach (var server in EnumerateNamedServerMap(mcpServers, sourcePath, result))
            {
                yieldedWrappedServers = true;
                yield return server;
            }
        }

        if (root.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Object)
        {
            foreach (var server in EnumerateNamedServerMap(servers, sourcePath, result))
            {
                yieldedWrappedServers = true;
                yield return server;
            }
        }

        if (!yieldedWrappedServers && LooksLikeBareServerObject(root) && !string.IsNullOrWhiteSpace(defaultName))
        {
            if (TryBuildSunderServerJson(defaultName, root, out var json, out var error))
            {
                yield return new DiscoveredMcpServer(defaultName, json);
            }
            else
            {
                result.Skipped++;
                result.Warnings.Add($"Skipped MCP server '{defaultName}' from {Path.GetFileName(sourcePath)}: {error}");
            }
        }
    }

    private static IEnumerable<DiscoveredMcpServer> EnumerateNamedServerMap(JsonElement map, string sourcePath, MutableImportResult result)
    {
        foreach (var property in map.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                result.Skipped++;
                result.Warnings.Add($"Skipped MCP server '{property.Name}' from {Path.GetFileName(sourcePath)} because its value is not an object.");
                continue;
            }

            if (TryBuildSunderServerJson(property.Name, property.Value, out var json, out var error))
            {
                yield return new DiscoveredMcpServer(property.Name, json);
                continue;
            }

            result.Skipped++;
            result.Warnings.Add($"Skipped MCP server '{property.Name}' from {Path.GetFileName(sourcePath)}: {error}");
        }
    }

    private static bool TryBuildSunderServerJson(string name, JsonElement source, out string json, out string? error)
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
            foreach (var item in command.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    parts.Add(item.GetString()!.Trim());
                }
            }
        }
        else if (command.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(command.GetString()))
        {
            parts.Add(command.GetString()!.Trim());
        }

        if (source.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in args.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    parts.Add(item.GetString()!.Trim());
                }
            }
        }

        return [.. parts];
    }

    private static Dictionary<string, string>? ReadStringMap(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                result[property.Name] = property.Value.GetString()!.Trim();
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static object? ReadOAuth(JsonElement source)
    {
        if (!source.TryGetProperty("oauth", out var oauth))
        {
            return null;
        }

        return oauth.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Object => JsonSerializer.Deserialize<Dictionary<string, object?>>(oauth.GetRawText(), SerializerOptions),
            _ => null,
        };
    }

    private static string? ReadString(JsonElement source, string propertyName)
        => source.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void AddIfNotNull(IDictionary<string, object?> target, string key, object? value)
    {
        if (value is not null)
        {
            target[key] = value;
        }
    }

    private static bool? ReadBool(JsonElement source, string propertyName)
        => source.TryGetProperty(propertyName, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    private static string Serialize(object value) => JsonSerializer.Serialize(value, SerializerOptions);

    private static bool LooksLikeBareServerObject(JsonElement root)
        => root.TryGetProperty("type", out _)
           || root.TryGetProperty("command", out _)
           || root.TryGetProperty("url", out _);

    private static string ComputeImportHash(string normalizedName, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(normalizedName + "\n" + json);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static bool CanUpdateManagedServer(ConfiguredMcpServerRecord existing, McpConfigurationImportOptions options)
        => existing.IsExternallyManaged
           && string.Equals(existing.SourceKind, options.SourceKind, StringComparison.OrdinalIgnoreCase)
           && SourceUriEquals(existing.SourceUri, options.SourceUri);

    private static bool SourceUriEquals(string? left, string? right)
        => string.Equals(left, right, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string GetCanonicalSourceUri(string filePath)
        => Path.GetFullPath(filePath);

    private static IEnumerable<string> EnumerateCommonConfigurationPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            yield break;
        }

        yield return Path.Combine(home, ".config", "opencode", "opencode.json");
        yield return Path.Combine(home, ".config", "opencode", "opencode.jsonc");
        yield return Path.Combine(home, ".cursor", "mcp.json");
        yield return Path.Combine(home, ".claude", "mcp.json");
        yield return Path.Combine(home, "Library", "Application Support", "Claude", "claude_desktop_config.json");
        yield return Path.Combine(home, "AppData", "Roaming", "Claude", "claude_desktop_config.json");
    }

    private sealed record DiscoveredMcpServer(string Name, string Json);

    private sealed record McpConfigurationImportOptions(
        string? SourceKind,
        string? SourceUri,
        bool IsExternallyManaged,
        bool DeleteMissingFromSource)
    {
        public static McpConfigurationImportOptions Manual { get; } = new(null, null, IsExternallyManaged: false, DeleteMissingFromSource: false);

        public static McpConfigurationImportOptions SunderManaged(string sourceUri)
            => new(SunderConfigurationSourceKind, sourceUri, IsExternallyManaged: true, DeleteMissingFromSource: true);
    }

    private sealed class MutableImportResult
    {
        public int Imported { get; set; }

        public int Skipped { get; set; }

        public List<string> Warnings { get; } = [];

        public void Add(McpConfigurationImportResult result)
        {
            Imported += result.ImportedCount;
            Skipped += result.SkippedCount;
            Warnings.AddRange(result.Warnings);
        }

        public McpConfigurationImportResult ToResult() => new(Imported, Skipped, Warnings.ToArray());
    }
}

public sealed record McpConfigurationImportResult(
    int ImportedCount,
    int SkippedCount,
    IReadOnlyList<string> Warnings);
