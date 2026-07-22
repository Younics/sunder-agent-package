namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Defines shared defensive ceilings for untrusted provider, tool, MCP, and local-file payloads.
/// </summary>
/// <remarks>
/// These are maximum accepted sizes, not allocation targets or permission grants. Implementations
/// may enforce lower limits and must reject, omit, or explicitly mark truncated data rather than
/// silently treating an incomplete security-sensitive payload as complete.
/// </remarks>
public static class AgentPayloadLimits
{
    /// <summary>The maximum UTF-8 byte count accepted for one complete tool argument JSON document.</summary>
    public const int MaxToolArgumentBytes = 1024 * 1024;

    /// <summary>The maximum JSON nesting depth accepted for tool arguments and provider tool-call events.</summary>
    public const int MaxToolArgumentJsonDepth = 64;

    /// <summary>The maximum total number of object properties accepted across a tool argument document.</summary>
    public const int MaxToolArgumentProperties = 4096;

    /// <summary>The maximum UTF-8 byte count accepted for one provider server-sent-event line.</summary>
    public const int MaxProviderSseLineBytes = 1024 * 1024;

    /// <summary>The maximum accumulated UTF-8 byte count for arguments assembled from streamed tool-call fragments.</summary>
    public const int MaxStreamedToolArgumentBytes = MaxToolArgumentBytes;

    /// <summary>The maximum serialized UTF-8 byte count retained from one MCP tool result.</summary>
    public const int MaxMcpResultBytes = 4 * 1024 * 1024;

    /// <summary>The maximum number of MCP content blocks considered when serializing one result.</summary>
    public const int MaxMcpContentItems = 1024;

    /// <summary>The maximum raw byte size allowed for an unrestricted full read of a local text file.</summary>
    public const int MaxLocalFullFileReadBytes = 4 * 1024 * 1024;

    /// <summary>The maximum number of local directory entries returned by one listing.</summary>
    public const int MaxLocalDirectoryEntries = 10_000;
}
