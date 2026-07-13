namespace Sunder.Package.Agent.Contracts.Models;

public static class AgentPayloadLimits
{
    public const int MaxToolArgumentBytes = 1024 * 1024;
    public const int MaxToolArgumentJsonDepth = 64;
    public const int MaxToolArgumentProperties = 4096;
    public const int MaxProviderSseLineBytes = 1024 * 1024;
    public const int MaxStreamedToolArgumentBytes = MaxToolArgumentBytes;
    public const int MaxMcpResultBytes = 4 * 1024 * 1024;
    public const int MaxMcpContentItems = 1024;
    public const int MaxLocalFullFileReadBytes = 4 * 1024 * 1024;
    public const int MaxLocalDirectoryEntries = 10_000;
}
