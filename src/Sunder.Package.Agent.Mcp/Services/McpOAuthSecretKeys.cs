using Sunder.Sdk.Storage;

namespace Sunder.Package.Agent.Mcp.Services;

internal static class McpOAuthSecretKeys
{
    public static string TokenCache(string serverId)
        => PackageStorageKeyFactory.Create(
            "mcp.oauth.tokens",
            1,
            McpServerCatalogService.CanonicalizeServerId(serverId));

    public static string ClientRegistration(string serverId)
        => PackageStorageKeyFactory.Create(
            "mcp.oauth.registration",
            1,
            McpServerCatalogService.CanonicalizeServerId(serverId));

    public static string ClientSecret(string serverId)
        => PackageStorageKeyFactory.Create(
            "mcp.oauth.client-secret",
            1,
            McpServerCatalogService.CanonicalizeServerId(serverId));

    internal static string LegacyTokenCache(string serverId) => $"mcp.servers.{serverId}.oauth.tokens";

    internal static string LegacyClientRegistration(string serverId) => $"mcp.servers.{serverId}.oauth.registration";

    internal static string LegacyClientSecret(string serverId) => $"mcp.servers.{serverId}.oauth.clientSecret";
}
