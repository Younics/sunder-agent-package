using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Mcp;

internal static class McpToolIdentity
{
    public static string CreateStable(string serverId, string toolName)
        => McpToolCardPresentation.CreateStableToolId(serverId, toolName);

    public static string CreateLegacyAlias(string normalizedServerName, string toolName)
        => $"{normalizedServerName}_{toolName}";

    public static bool TryParseLegacy(
        string toolId,
        IEnumerable<ConfiguredMcpServerRecord> servers,
        out ConfiguredMcpServerRecord? server,
        out string toolName)
    {
        var candidates = servers
            .Where(item => item.IsEnabled && toolId.StartsWith(item.Name + "_", StringComparison.OrdinalIgnoreCase))
            .Select(item => (Server: item, ToolName: toolId[(item.Name.Length + 1)..]))
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.ToolName))
            .ToArray();
        if (candidates.Length != 1)
        {
            server = null;
            toolName = string.Empty;
            return false;
        }

        server = candidates[0].Server;
        toolName = candidates[0].ToolName;
        return true;
    }

    public static bool TryParseStable(string toolId, out string serverId, out string toolName)
        => McpToolCardPresentation.TryParseStableToolId(toolId, out serverId, out toolName);
}
