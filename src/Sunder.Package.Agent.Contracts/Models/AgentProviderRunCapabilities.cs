namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentProviderRunCapabilities(
    bool SupportsNativeToolCalling,
    bool SupportsStreamingToolCalls,
    bool SupportsMultipleToolCalls,
    string Summary,
    bool SupportsImageInput = false,
    bool SupportsPdfInput = false,
    bool SupportsAudioInput = false,
    bool SupportsVideoInput = false);
