using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Mcp;

internal static class McpToolIdentity
{
    public static string CreateStable(string serverId, string toolName)
        => McpToolCardPresentation.CreateStableToolId(serverId, toolName);

    public static bool TryParseStable(string toolId, out string serverId, out string toolName)
        => McpToolCardPresentation.TryParseStableToolId(toolId, out serverId, out toolName);
}
