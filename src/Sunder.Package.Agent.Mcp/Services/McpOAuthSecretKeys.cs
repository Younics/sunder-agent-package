using Sunder.Package.Agent.Shared.Storage;

namespace Sunder.Package.Agent.Mcp.Services;

internal static class McpOAuthSecretKeys
{
    public static string TokenCache(string serverId)
        => CurrentPackageStorageKey.Create(
            "mcp.oauth.tokens",
            1,
            McpServerCatalogService.CanonicalizeServerId(serverId));

    public static string ClientRegistration(string serverId)
        => CurrentPackageStorageKey.Create(
            "mcp.oauth.registration",
            1,
            McpServerCatalogService.CanonicalizeServerId(serverId));

    public static string ClientSecret(string serverId)
        => CurrentPackageStorageKey.Create(
            "mcp.oauth.client-secret",
            1,
            McpServerCatalogService.CanonicalizeServerId(serverId));
}
