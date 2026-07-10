namespace Sunder.Package.Agent.Mcp.Services;

internal static class McpOAuthSecretKeys
{
    public static string TokenCache(string serverId) => $"mcp.servers.{serverId}.oauth.tokens";

    public static string ClientRegistration(string serverId) => $"mcp.servers.{serverId}.oauth.registration";

    public static string ClientSecret(string serverId) => $"mcp.servers.{serverId}.oauth.clientSecret";
}
