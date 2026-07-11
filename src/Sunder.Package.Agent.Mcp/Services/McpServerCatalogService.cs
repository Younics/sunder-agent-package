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

                if (!McpServerRecordSerializer.TryDeserialize(payload, _packageContext.Secrets, NormalizeServerName, out var server, out var error)
                    || server is null)
                {
                    diagnostics.Add(new McpCatalogDiagnostic(key, error ?? "Stored MCP server metadata is invalid."));
                    continue;
                }

                if (!string.Equals(key, BuildServerKey(server.ServerId), StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(new McpCatalogDiagnostic(key, $"Stored ServerId '{server.ServerId}' does not match its storage key."));
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
        return !string.IsNullOrWhiteSpace(payload)
               && McpServerRecordSerializer.TryDeserialize(payload, _packageContext.Secrets, NormalizeServerName, out var server, out _)
            ? server
            : null;
    }

    public async Task<string?> ExportServerJsonAsync(string serverId, CancellationToken cancellationToken = default)
    {
        var server = await GetServerAsync(serverId, cancellationToken).ConfigureAwait(false);
        return server is null ? null : McpConfigurationDocument.BuildEditorText(server, GetHeaders(server), GetEnvironmentVariables(server));
    }

    public async Task SaveServerAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server.ServerId);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var normalizedName = NormalizeServerName(server.Name);
            var allServers = await ListServersAsync(cancellationToken).ConfigureAwait(false);
            if (allServers.Any(item => !string.Equals(item.ServerId, server.ServerId, StringComparison.OrdinalIgnoreCase)
                                       && string.Equals(item.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"An MCP server named '{normalizedName}' already exists.");
            }

            var existing = await GetServerAsync(server.ServerId, cancellationToken).ConfigureAwait(false);
            var persisted = server with
            {
                Name = normalizedName,
                PersistenceVersion = Math.Max(existing?.PersistenceVersion ?? 0, 0) + 1,
                HeaderNames = [.. server.HeaderNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)],
                EnvironmentVariableNames = [.. server.EnvironmentVariableNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)],
            };
            var stagedSecretKeys = StageVersionedSecrets(persisted, headers, environmentVariables);
            try
            {
                await _packageContext.Storage.State.SetValueAsync(
                    BuildServerKey(persisted.ServerId),
                    JsonSerializer.Serialize(persisted),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception commitError)
            {
                throw CompensateStagedSecrets(stagedSecretKeys, commitError);
            }

            CleanupSupersededSecrets(existing, persisted);
            ServersChanged?.Invoke();
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await GetServerAsync(serverId, cancellationToken).ConfigureAwait(false);
            await _packageContext.Storage.State.DeleteValueAsync(BuildServerKey(serverId), cancellationToken).ConfigureAwait(false);
            CleanupDeletedServerSecrets(existing, serverId);
            ServersChanged?.Invoke();
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public IReadOnlyDictionary<string, string> GetHeaders(ConfiguredMcpServerRecord server)
        => ReadSecrets(server, server.HeaderNames, BuildHeaderSecretKey, readLegacyHeaderFallbacks: true);

    public IReadOnlyDictionary<string, string> GetEnvironmentVariables(ConfiguredMcpServerRecord server)
        => ReadSecrets(server, server.EnvironmentVariableNames, BuildEnvironmentSecretKey, readLegacyHeaderFallbacks: false);

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

    private IReadOnlyList<string> StageVersionedSecrets(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables)
    {
        var staged = new List<string>();
        try
        {
            StageValues(server, server.HeaderNames, headers, BuildHeaderSecretKey, staged);
            StageValues(server, server.EnvironmentVariableNames, environmentVariables, BuildEnvironmentSecretKey, staged);
            return staged;
        }
        catch (Exception ex)
        {
            throw CompensateStagedSecrets(staged, ex);
        }
    }

    private void StageValues(
        ConfiguredMcpServerRecord server,
        IEnumerable<string> names,
        IReadOnlyDictionary<string, string> values,
        Func<string, int, string, string> buildKey,
        ICollection<string> staged)
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
            _packageContext.Secrets.SetSecret(key, value.Trim());
        }
    }

    private Exception CompensateStagedSecrets(IEnumerable<string> keys, Exception original)
    {
        var errors = new List<Exception> { original };
        foreach (var key in keys)
        {
            try
            {
                _packageContext.Secrets.DeleteSecret(key);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        return errors.Count == 1 ? original : new AggregateException("MCP server save failed and staged secret cleanup was incomplete.", errors);
    }

    private IReadOnlyDictionary<string, string> ReadSecrets(
        ConfiguredMcpServerRecord server,
        IEnumerable<string> names,
        Func<string, int, string, string> buildKey,
        bool readLegacyHeaderFallbacks)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var value = _packageContext.Secrets.GetSecret(buildKey(server.ServerId, server.PersistenceVersion, name));
            if (server.PersistenceVersion == 0 && readLegacyHeaderFallbacks && string.IsNullOrWhiteSpace(value))
            {
                value = string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase)
                    ? _packageContext.Secrets.GetSecret(BuildAuthorizationSecretKey(server.ServerId))
                    : null;
                value ??= _packageContext.Secrets.GetSecret(BuildApiKeySecretKey(server.ServerId));
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                values[name] = value.Trim();
            }
        }

        return values;
    }

    private void CleanupSupersededSecrets(ConfiguredMcpServerRecord? existing, ConfiguredMcpServerRecord persisted)
    {
        if (existing is not null)
        {
            DeleteVersionedSecrets(existing);
        }

        TryDeleteSecret(BuildApiKeySecretKey(persisted.ServerId));
        TryDeleteSecret(BuildAuthorizationSecretKey(persisted.ServerId));
        if (existing?.OAuthEnabled == true && !persisted.OAuthEnabled)
        {
            DeleteOAuthSecrets(persisted.ServerId);
        }
    }

    private void CleanupDeletedServerSecrets(ConfiguredMcpServerRecord? existing, string serverId)
    {
        if (existing is not null)
        {
            DeleteVersionedSecrets(existing);
        }

        TryDeleteSecret(BuildApiKeySecretKey(serverId));
        TryDeleteSecret(BuildAuthorizationSecretKey(serverId));
        DeleteOAuthSecrets(serverId);
    }

    private void DeleteVersionedSecrets(ConfiguredMcpServerRecord server)
    {
        foreach (var name in server.HeaderNames)
        {
            TryDeleteSecret(BuildHeaderSecretKey(server.ServerId, server.PersistenceVersion, name));
        }

        foreach (var name in server.EnvironmentVariableNames)
        {
            TryDeleteSecret(BuildEnvironmentSecretKey(server.ServerId, server.PersistenceVersion, name));
        }
    }

    private void DeleteOAuthSecrets(string serverId)
    {
        TryDeleteSecret(McpOAuthSecretKeys.TokenCache(serverId));
        TryDeleteSecret(McpOAuthSecretKeys.ClientRegistration(serverId));
        TryDeleteSecret(McpOAuthSecretKeys.ClientSecret(serverId));
    }

    private void TryDeleteSecret(string key)
    {
        try
        {
            _packageContext.Secrets.DeleteSecret(key);
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
}
