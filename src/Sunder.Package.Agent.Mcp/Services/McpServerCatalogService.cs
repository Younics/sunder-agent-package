using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Mcp.Services;

public sealed class McpServerCatalogService(IPackageContext packageContext)
{
    private const string ServerKeyPrefix = "mcp.servers.";
    private readonly IPackageContext _packageContext = packageContext;
    private readonly ILogger<McpServerCatalogService> _logger = packageContext.LoggerFactory.CreateLogger<McpServerCatalogService>();
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private IReadOnlyList<McpCatalogDiagnostic> _lastDiagnostics = [];

    public event Action? ServersChanged;

    public IReadOnlyList<McpCatalogDiagnostic> LastDiagnostics => Volatile.Read(ref _lastDiagnostics);

    public void NotifyServersImported() => ServersChanged?.Invoke();

    public async Task<IReadOnlyList<ConfiguredMcpServerRecord>> ListServersAsync(CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<McpCatalogDiagnostic>();
        var servers = new List<ConfiguredMcpServerRecord>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = await _packageContext.Storage.State.ListKeysAsync(ServerKeyPrefix, cancellationToken).ConfigureAwait(false);
        foreach (var key in keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var payload = await _packageContext.Storage.State.GetValueAsync(key, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(payload))
                {
                    diagnostics.Add(new McpCatalogDiagnostic(key, "Stored MCP server metadata is empty."));
                    continue;
                }

                var deserialized = await McpServerRecordSerializer.DeserializeAsync(
                    payload, _packageContext.Secrets, NormalizeServerName, cancellationToken);
                if (deserialized.Server is not { } server)
                {
                    diagnostics.Add(new McpCatalogDiagnostic(key, deserialized.Error ?? "Stored MCP server metadata is invalid."));
                    continue;
                }

                if (!string.Equals(key, BuildServerKey(server.ServerId), StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(new McpCatalogDiagnostic(key, $"Stored ServerId '{server.ServerId}' does not match its storage key."));
                    continue;
                }

                if (!ids.Add(server.ServerId))
                {
                    diagnostics.Add(new McpCatalogDiagnostic(key, $"MCP server id '{server.ServerId}' is duplicated or case-colliding."));
                    continue;
                }

                if (!names.Add(server.Name))
                {
                    diagnostics.Add(new McpCatalogDiagnostic(key, $"Normalized MCP server name '{server.Name}' is duplicated."));
                    continue;
                }

                servers.Add(server);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnostics.Add(new McpCatalogDiagnostic(key, $"Stored MCP server metadata could not be read: {ex.Message}"));
            }
        }

        Volatile.Write(ref _lastDiagnostics, diagnostics.ToArray());
        return servers.OrderBy(server => server.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<ConfiguredMcpServerRecord?> GetServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        var payload = await _packageContext.Storage.State.GetValueAsync(BuildServerKey(serverId), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        return (await McpServerRecordSerializer.DeserializeAsync(
            payload, _packageContext.Secrets, NormalizeServerName, cancellationToken)).Server;
    }

    public async Task<string?> ExportServerJsonAsync(string serverId, CancellationToken cancellationToken = default)
    {
        var server = await GetServerAsync(serverId, cancellationToken).ConfigureAwait(false);
        return server is null
            ? null
            : McpConfigurationDocument.BuildEditorText(
                server,
                await GetHeadersAsync(server, cancellationToken),
                await GetEnvironmentVariablesAsync(server, cancellationToken));
    }

    public async Task SaveServerAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        CancellationToken cancellationToken = default)
        => await ApplyBatchAsync(
            [new McpServerCatalogWrite(server, headers, environmentVariables)],
            [],
            cancellationToken).ConfigureAwait(false);

    internal async Task ApplyBatchAsync(
        IReadOnlyList<McpServerCatalogWrite> writes,
        IReadOnlyCollection<string> deletedServerIds,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ApplyBatchCoreAsync(writes, deletedServerIds, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default)
        => await ApplyBatchAsync([], [serverId], cancellationToken).ConfigureAwait(false);

    private async Task ApplyBatchCoreAsync(
        IReadOnlyList<McpServerCatalogWrite> writes,
        IReadOnlyCollection<string> deletedServerIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var allServers = await ListServersAsync(cancellationToken).ConfigureAwait(false);
        var existingById = allServers.ToDictionary(server => server.ServerId, StringComparer.OrdinalIgnoreCase);
        var deletedIds = deletedServerIds
            .Select(id => existingById.TryGetValue(id, out var existing) ? existing.ServerId : id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var writeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var write in writes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(write.Server.ServerId);
            if (!writeIds.Add(write.Server.ServerId))
            {
                throw new InvalidOperationException($"MCP import contains duplicate or case-colliding server id '{write.Server.ServerId}'.");
            }
        }

        var finalNames = allServers
            .Where(server => !deletedIds.Contains(server.ServerId) && !writeIds.Contains(server.ServerId))
            .ToDictionary(server => server.Name, server => server.ServerId, StringComparer.OrdinalIgnoreCase);
        var stagedWrites = new List<StagedCatalogWrite>();
        foreach (var write in writes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            existingById.TryGetValue(write.Server.ServerId, out var existing);
            var normalizedName = NormalizeServerName(write.Server.Name);
            if (finalNames.TryGetValue(normalizedName, out var conflictingId)
                && !string.Equals(conflictingId, write.Server.ServerId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"An MCP server named '{normalizedName}' already exists.");
            }

            finalNames[normalizedName] = write.Server.ServerId;
            var persisted = write.Server with
            {
                ServerId = existing?.ServerId ?? write.Server.ServerId,
                Name = normalizedName,
                PersistenceVersion = Math.Max(existing?.PersistenceVersion ?? 0, 0) + 1,
                HeaderNames = NormalizeSecretNames(write.Server.HeaderNames, "header"),
                EnvironmentVariableNames = NormalizeSecretNames(write.Server.EnvironmentVariableNames, "environment variable"),
            };
            stagedWrites.Add(new StagedCatalogWrite(write, existing, persisted));
        }

        var persistedWriteIds = stagedWrites
            .Select(staged => staged.Persisted.ServerId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var affectedIds = persistedWriteIds.Concat(deletedIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var snapshots = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in affectedIds)
        {
            snapshots[id] = await _packageContext.Storage.State.GetValueAsync(BuildServerKey(id), cancellationToken).ConfigureAwait(false);
        }

        var stagedSecretKeys = new List<string>();
        try
        {
            foreach (var staged in stagedWrites)
            {
                stagedSecretKeys.AddRange(await StageVersionedSecretsAsync(
                    staged.Persisted,
                    staged.Write.Headers,
                    staged.Write.EnvironmentVariables,
                    cancellationToken).ConfigureAwait(false));
            }

            foreach (var staged in stagedWrites)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _packageContext.Storage.State.SetValueAsync(
                    BuildServerKey(staged.Persisted.ServerId),
                    JsonSerializer.Serialize(staged.Persisted),
                    cancellationToken).ConfigureAwait(false);
            }

            foreach (var id in deletedIds.Where(id => !persistedWriteIds.Contains(id)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _packageContext.Storage.State.DeleteValueAsync(BuildServerKey(id), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception commitError)
        {
            var errors = new List<Exception> { commitError };
            foreach (var snapshot in snapshots)
            {
                try
                {
                    if (snapshot.Value is null)
                    {
                        await _packageContext.Storage.State.DeleteValueAsync(BuildServerKey(snapshot.Key), CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        await _packageContext.Storage.State.SetValueAsync(BuildServerKey(snapshot.Key), snapshot.Value, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception rollbackError)
                {
                    errors.Add(rollbackError);
                }
            }

            var cleanupError = await CompensateStagedSecretsAsync(stagedSecretKeys, commitError, CancellationToken.None).ConfigureAwait(false);
            if (!ReferenceEquals(cleanupError, commitError))
            {
                errors.Add(cleanupError);
            }

            if (errors.Count > 1)
            {
                throw new AggregateException("MCP catalog mutation failed and its prior state could not be fully restored.", errors);
            }

            throw;
        }

        foreach (var staged in stagedWrites)
        {
            await CleanupSupersededSecretsAsync(staged.Existing, staged.Persisted, CancellationToken.None).ConfigureAwait(false);
        }

        foreach (var id in deletedIds.Where(id => !persistedWriteIds.Contains(id)))
        {
            existingById.TryGetValue(id, out var existing);
            await CleanupDeletedServerSecretsAsync(existing, id, CancellationToken.None).ConfigureAwait(false);
        }

        if (writes.Count > 0 || deletedIds.Count > 0)
        {
            ServersChanged?.Invoke();
        }
    }

    private static string[] NormalizeSecretNames(IEnumerable<string> names, string kind)
    {
        var result = new List<string>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException($"MCP {kind} names must not be empty.");
            }

            var normalized = name.Trim();
            if (!unique.Add(normalized))
            {
                throw new InvalidOperationException($"MCP configuration contains duplicate or case-colliding {kind} name '{normalized}'.");
            }

            result.Add(normalized);
        }

        return [.. result];
    }

    public Task<IReadOnlyDictionary<string, string>> GetHeadersAsync(
        ConfiguredMcpServerRecord server,
        CancellationToken cancellationToken = default)
        => ReadSecretsAsync(server, server.HeaderNames, BuildHeaderSecretKey, readLegacyHeaderFallbacks: true, cancellationToken);

    public Task<IReadOnlyDictionary<string, string>> GetEnvironmentVariablesAsync(
        ConfiguredMcpServerRecord server,
        CancellationToken cancellationToken = default)
        => ReadSecretsAsync(server, server.EnvironmentVariableNames, BuildEnvironmentSecretKey, readLegacyHeaderFallbacks: false, cancellationToken);

    public string NormalizeServerName(string? value)
        => NormalizeName(value);

    internal static string NormalizeName(string? value)
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

    private async Task<IReadOnlyList<string>> StageVersionedSecretsAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        CancellationToken cancellationToken)
    {
        var staged = new List<string>();
        try
        {
            await StageValuesAsync(server, server.HeaderNames, headers, BuildHeaderSecretKey, staged, cancellationToken);
            await StageValuesAsync(server, server.EnvironmentVariableNames, environmentVariables, BuildEnvironmentSecretKey, staged, cancellationToken);
            return staged;
        }
        catch (Exception ex)
        {
            throw await CompensateStagedSecretsAsync(staged, ex, CancellationToken.None);
        }
    }

    private async Task StageValuesAsync(
        ConfiguredMcpServerRecord server,
        IEnumerable<string> names,
        IReadOnlyDictionary<string, string> values,
        Func<string, int, string, string> buildKey,
        ICollection<string> staged,
        CancellationToken cancellationToken)
    {
        foreach (var name in names.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            {
                if (server.IsEnabled)
                {
                    throw new InvalidOperationException($"Enabled MCP server '{server.DisplayName}' is missing a value for '{name}'.");
                }

                continue;
            }

            var key = buildKey(server.ServerId, server.PersistenceVersion, name);
            staged.Add(key);
            await _packageContext.Secrets.SetSecretAsync(key, value.Trim(), cancellationToken);
        }
    }

    private async Task<Exception> CompensateStagedSecretsAsync(
        IEnumerable<string> keys,
        Exception original,
        CancellationToken cancellationToken)
    {
        var errors = new List<Exception> { original };
        foreach (var key in keys)
        {
            try
            {
                await _packageContext.Secrets.DeleteSecretAsync(key, cancellationToken);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        return errors.Count == 1 ? original : new AggregateException("MCP server save failed and staged secret cleanup was incomplete.", errors);
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadSecretsAsync(
        ConfiguredMcpServerRecord server,
        IEnumerable<string> names,
        Func<string, int, string, string> buildKey,
        bool readLegacyHeaderFallbacks,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var value = await _packageContext.Secrets.GetSecretAsync(
                buildKey(server.ServerId, server.PersistenceVersion, name), cancellationToken);
            if (server.PersistenceVersion == 0 && readLegacyHeaderFallbacks && string.IsNullOrWhiteSpace(value))
            {
                value = string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase)
                    ? await _packageContext.Secrets.GetSecretAsync(BuildAuthorizationSecretKey(server.ServerId), cancellationToken)
                    : null;
                value ??= await _packageContext.Secrets.GetSecretAsync(BuildApiKeySecretKey(server.ServerId), cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                values[name] = value.Trim();
            }
        }

        return values;
    }

    private async Task CleanupSupersededSecretsAsync(
        ConfiguredMcpServerRecord? existing,
        ConfiguredMcpServerRecord persisted,
        CancellationToken cancellationToken)
    {
        if (existing is not null)
        {
            await DeleteVersionedSecretsAsync(existing, cancellationToken);
        }

        await TryDeleteSecretAsync(BuildApiKeySecretKey(persisted.ServerId), cancellationToken);
        await TryDeleteSecretAsync(BuildAuthorizationSecretKey(persisted.ServerId), cancellationToken);
        if (existing?.OAuthEnabled == true && !persisted.OAuthEnabled)
        {
            await DeleteOAuthSecretsAsync(persisted.ServerId, cancellationToken);
        }
    }

    private async Task CleanupDeletedServerSecretsAsync(
        ConfiguredMcpServerRecord? existing,
        string serverId,
        CancellationToken cancellationToken)
    {
        if (existing is not null)
        {
            await DeleteVersionedSecretsAsync(existing, cancellationToken);
        }

        await TryDeleteSecretAsync(BuildApiKeySecretKey(serverId), cancellationToken);
        await TryDeleteSecretAsync(BuildAuthorizationSecretKey(serverId), cancellationToken);
        await DeleteOAuthSecretsAsync(serverId, cancellationToken);
    }

    private async Task DeleteVersionedSecretsAsync(ConfiguredMcpServerRecord server, CancellationToken cancellationToken)
    {
        foreach (var name in server.HeaderNames)
        {
            await TryDeleteSecretAsync(BuildHeaderSecretKey(server.ServerId, server.PersistenceVersion, name), cancellationToken);
        }

        foreach (var name in server.EnvironmentVariableNames)
        {
            await TryDeleteSecretAsync(BuildEnvironmentSecretKey(server.ServerId, server.PersistenceVersion, name), cancellationToken);
        }
    }

    private async Task DeleteOAuthSecretsAsync(string serverId, CancellationToken cancellationToken)
    {
        await TryDeleteSecretAsync(McpOAuthSecretKeys.TokenCache(serverId), cancellationToken);
        await TryDeleteSecretAsync(McpOAuthSecretKeys.ClientRegistration(serverId), cancellationToken);
        await TryDeleteSecretAsync(McpOAuthSecretKeys.ClientSecret(serverId), cancellationToken);
    }

    private async Task TryDeleteSecretAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await _packageContext.Secrets.DeleteSecretAsync(key, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up superseded MCP secret '{SecretKey}'.", key);
        }
    }

    private static string BuildServerKey(string serverId) => ServerKeyPrefix + serverId;

    internal static string BuildApiKeySecretKey(string serverId) => $"mcp.servers.{serverId}.apiKey";

    internal static string BuildAuthorizationSecretKey(string serverId) => $"mcp.servers.{serverId}.authorization";

    private static string BuildHeaderSecretKey(string serverId, int version, string name)
        => version <= 0
            ? $"mcp.servers.{serverId}.headers.{Uri.EscapeDataString(name)}"
            : $"mcp.servers.{serverId}.v{version}.headers.{Uri.EscapeDataString(name)}";

    private static string BuildEnvironmentSecretKey(string serverId, int version, string name)
        => version <= 0
            ? $"mcp.servers.{serverId}.environment.{Uri.EscapeDataString(name)}"
            : $"mcp.servers.{serverId}.v{version}.environment.{Uri.EscapeDataString(name)}";

    private sealed record StagedCatalogWrite(
        McpServerCatalogWrite Write,
        ConfiguredMcpServerRecord? Existing,
        ConfiguredMcpServerRecord Persisted);
}
