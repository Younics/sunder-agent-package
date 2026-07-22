using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;

namespace Sunder.Package.Agent.Mcp.Services;

public sealed partial class McpClientConnectionManager
{
    private static void ValidateDiscoveredTools(
        ConfiguredMcpServerRecord server,
        IReadOnlyList<McpClientTool> tools)
    {
        if (tools.Count > MaxDiscoveredToolsPerServer)
        {
            throw new InvalidDataException(
                $"MCP server '{server.DisplayName}' exposed {tools.Count} tools; the limit is {MaxDiscoveredToolsPerServer}.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metadataBytes = 0;
        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name) || !names.Add(tool.Name))
            {
                throw new InvalidDataException(
                    $"MCP server '{server.DisplayName}' exposed an empty, duplicate, or case-colliding tool name.");
            }

            metadataBytes = checked(metadataBytes
                + Encoding.UTF8.GetByteCount(tool.Name)
                + Encoding.UTF8.GetByteCount(tool.Title ?? string.Empty)
                + Encoding.UTF8.GetByteCount(tool.Description ?? string.Empty)
                + Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(tool.JsonSchema)));
            if (metadataBytes > MaxDiscoveryMetadataBytes)
            {
                throw new InvalidDataException(
                    $"MCP server '{server.DisplayName}' tool metadata exceeded the {MaxDiscoveryMetadataBytes}-byte discovery limit.");
            }
        }
    }
}
