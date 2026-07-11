using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sunder.Package.Agent.Mcp.Services;

public sealed class McpEcosystemConfigurationImporter(McpServerCatalogService serverCatalog)
{
    public const string SunderConfigurationSourceKind = "sunder-config";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
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
        var parsedFile = McpConfigurationFileParser.Parse(document.RootElement, filePath, Path.GetFileNameWithoutExtension(filePath));
        result.Add(parsedFile);
        var discoveredServers = parsedFile.Servers;
        var sourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var importedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var discovered in discoveredServers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var normalizedName = serverCatalog.NormalizeServerName(discovered.Name);
                if (!importedNames.Add(normalizedName))
                {
                    result.Skipped++;
                    result.Warnings.Add($"Skipped MCP server '{discovered.Name}' from {Path.GetFileName(filePath)} because normalized name '{normalizedName}' is duplicated in the file.");
                    continue;
                }

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
            catch (Exception ex) when (ex is not OperationCanceledException)
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

        public void Add(McpConfigurationFileParseResult result)
        {
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
