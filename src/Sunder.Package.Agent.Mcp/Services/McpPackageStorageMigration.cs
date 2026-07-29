using System.Text.Json;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Storage;

namespace Sunder.Package.Agent.Mcp.Services;

internal sealed class McpPackageStorageMigration(IPackageContext packageContext)
{
    private const string LegacySecretPrefix = "mcp.servers.";
    private static readonly string[] LegacyFixedSecretSuffixes =
    [
        ".apiKey",
        ".authorization",
        ".oauth.tokens",
        ".oauth.registration",
        ".oauth.clientSecret",
    ];
    private static readonly string[] LegacyVersionedSecretSeparators =
    [
        ".headers.",
        ".environment.",
    ];
    private static readonly string[] PortableSecretPrefixes =
    [
        "mcp.secret.api-key.v1.",
        "mcp.secret.authorization.v1.",
        "mcp.secret.header.v1.",
        "mcp.secret.environment.v1.",
        "mcp.oauth.tokens.v1.",
        "mcp.oauth.registration.v1.",
        "mcp.oauth.client-secret.v1.",
    ];
    private static readonly PackageStorageKeyMigration[] StateMigrations =
    [
        PackageStorageKeyMigration.OpaqueIdWithCaseInsensitiveJsonIdentity(
            "mcp.servers.",
            string.Empty,
            "mcp.catalog.server",
            1,
            "serverId"),
    ];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly object _syncRoot = new();
    private Task? _migration;

    internal Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        Task migration;
        lock (_syncRoot)
        {
            migration = _migration ??= MigrateAsync();
        }

        return cancellationToken.CanBeCanceled ? migration.WaitAsync(cancellationToken) : migration;
    }

    private async Task MigrateAsync()
    {
        if (packageContext.Storage.State is IPackageStorageKeyMigrator stateMigrator)
        {
            await stateMigrator.MigrateKeysAsync(StateMigrations, CancellationToken.None).ConfigureAwait(false);
        }

        if (packageContext.Secrets is not IPackageStorageKeyMigrator secretsMigrator)
        {
            return;
        }

        var routes = new McpSecretMigrationRoutes();
        var keys = await packageContext.Storage.State.ListKeysAsync(
            McpServerCatalogService.ServerKeyPrefix,
            CancellationToken.None).ConfigureAwait(false);
        foreach (var key in keys)
        {
            var payload = await packageContext.Storage.State.GetValueAsync(key, CancellationToken.None).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(payload))
            {
                continue;
            }

            ConfiguredMcpServerRecord? server;
            try
            {
                server = JsonSerializer.Deserialize<ConfiguredMcpServerRecord>(payload, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }
            if (server is null
                || string.IsNullOrWhiteSpace(server.ServerId)
                || !string.Equals(
                    key,
                    McpServerCatalogService.BuildServerKey(server.ServerId),
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (server.PersistenceVersion <= 0 || server.HeaderNames is not { Length: > 0 })
            {
                routes.Add(
                    McpServerCatalogService.BuildLegacyApiKeySecretKey(server.ServerId),
                    McpServerCatalogService.BuildApiKeySecretKey(server.ServerId));
                routes.Add(
                    McpServerCatalogService.BuildLegacyAuthorizationSecretKey(server.ServerId),
                    McpServerCatalogService.BuildAuthorizationSecretKey(server.ServerId));
            }
            if (server.OAuthEnabled)
            {
                routes.Add(McpOAuthSecretKeys.LegacyTokenCache(server.ServerId), McpOAuthSecretKeys.TokenCache(server.ServerId));
                routes.Add(McpOAuthSecretKeys.LegacyClientRegistration(server.ServerId), McpOAuthSecretKeys.ClientRegistration(server.ServerId));
                routes.Add(McpOAuthSecretKeys.LegacyClientSecret(server.ServerId), McpOAuthSecretKeys.ClientSecret(server.ServerId));
            }
            foreach (var name in server.HeaderNames ?? [])
            {
                routes.Add(
                    McpServerCatalogService.BuildLegacyHeaderSecretKey(server.ServerId, server.PersistenceVersion, name),
                    McpServerCatalogService.BuildHeaderSecretKey(server.ServerId, server.PersistenceVersion, name));
            }
            foreach (var name in server.EnvironmentVariableNames ?? [])
            {
                routes.Add(
                    McpServerCatalogService.BuildLegacyEnvironmentSecretKey(server.ServerId, server.PersistenceVersion, name),
                    McpServerCatalogService.BuildEnvironmentSecretKey(server.ServerId, server.PersistenceVersion, name));
            }
        }

        await secretsMigrator.MigrateKeysAsync(
        [
            PackageStorageKeyMigration.DynamicCleanup(routes.Resolve),
        ], CancellationToken.None).ConfigureAwait(false);
    }

    private static bool IsRecognizedLegacySecretKey(string key)
    {
        if (!key.StartsWith(LegacySecretPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var body = key[LegacySecretPrefix.Length..];
        if (LegacyFixedSecretSuffixes.Any(suffix =>
                body.Length > suffix.Length
                && body.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return LegacyVersionedSecretSeparators.Any(separator =>
        {
            var separatorIndex = body.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
            return separatorIndex > 0
                   && separatorIndex + separator.Length < body.Length;
        });
    }

    private static bool IsRecognizedPortableSecretKey(string key)
        => PortableSecretPrefixes.Any(prefix =>
            key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && key.Length == prefix.Length + 64
            && key.AsSpan(prefix.Length).ContainsOnlyHexDigits());

    private static bool StartsWithOwnedSecretPrefix(string key)
        => key.StartsWith(LegacySecretPrefix, StringComparison.OrdinalIgnoreCase)
           || PortableSecretPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private sealed class McpSecretMigrationRoutes
    {
        private readonly Dictionary<string, SecretRoute> _exactLegacy = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SecretRoute> _caseInsensitiveLegacy = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _exactCurrent = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SecretRoute> _caseInsensitiveCurrent = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string legacyKey, string destinationKey)
        {
            AddRoute(_exactLegacy, legacyKey, destinationKey);
            AddRoute(_caseInsensitiveLegacy, legacyKey, destinationKey);
            _exactCurrent.Add(destinationKey);
            AddRoute(_caseInsensitiveCurrent, destinationKey, destinationKey);
        }

        public PackageStorageKeyMigrationAction Resolve(string key)
        {
            if (_exactCurrent.Contains(key))
            {
                return PackageStorageKeyMigrationAction.NoMatch;
            }
            if (_caseInsensitiveCurrent.TryGetValue(key, out var currentRoute))
            {
                return currentRoute.DestinationKey is { } currentDestination
                    ? PackageStorageKeyMigrationAction.Rewrite(currentDestination, precedence: 1)
                    : PackageStorageKeyMigrationAction.Delete;
            }
            if (_exactLegacy.TryGetValue(key, out var exactRoute))
            {
                return exactRoute.DestinationKey is { } exactDestination
                    ? PackageStorageKeyMigrationAction.Rewrite(exactDestination, precedence: 1)
                    : PackageStorageKeyMigrationAction.Delete;
            }
            if (_caseInsensitiveLegacy.TryGetValue(key, out var insensitiveRoute))
            {
                return insensitiveRoute.DestinationKey is { } insensitiveDestination
                    ? PackageStorageKeyMigrationAction.Rewrite(insensitiveDestination)
                    : PackageStorageKeyMigrationAction.Delete;
            }
            if (IsRecognizedLegacySecretKey(key) || IsRecognizedPortableSecretKey(key))
            {
                return PackageStorageKeyMigrationAction.Delete;
            }
            if (StartsWithOwnedSecretPrefix(key))
            {
                throw new InvalidDataException($"Stored MCP secret key '{key}' has an unrecognized physical shape.");
            }
            return PackageStorageKeyMigrationAction.NoMatch;
        }

        private static void AddRoute(
            IDictionary<string, SecretRoute> routes,
            string key,
            string destinationKey)
        {
            if (routes.TryGetValue(key, out var existing)
                && !string.Equals(existing.DestinationKey, destinationKey, StringComparison.Ordinal))
            {
                routes[key] = new SecretRoute(null);
                return;
            }
            routes[key] = new SecretRoute(destinationKey);
        }
    }

    private sealed record SecretRoute(string? DestinationKey);
}

internal static class McpStorageKeySpanExtensions
{
    public static bool ContainsOnlyHexDigits(this ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }
        return true;
    }
}

internal sealed class McpPackageRuntimeStartupService(
    McpPackageStorageMigration storageMigration,
    McpConfigurationCoordinator configurationCoordinator) : IPackageBackgroundService
{
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await storageMigration.EnsureAsync(cancellationToken).ConfigureAwait(false);
        configurationCoordinator.Start();
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
