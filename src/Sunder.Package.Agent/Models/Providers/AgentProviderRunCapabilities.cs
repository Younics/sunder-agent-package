namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Describes the effective behavior a provider supports for a selected model and authorization mode.
/// </summary>
/// <remarks>
/// These values guide request construction; they do not replace readiness checks and cannot
/// guarantee that a remote service accepts a particular request at execution time.
/// </remarks>
/// <param name="SupportsNativeToolCalling">Whether tool declarations and tool results can be represented through the provider's native chat protocol.</param>
/// <param name="SupportsStreamingToolCalls">Whether tool-call identifiers and arguments can be assembled from incremental response updates.</param>
/// <param name="SupportsMultipleToolCalls">Whether one assistant response may request more than one tool call.</param>
/// <param name="Summary">A user-facing explanation of the effective run behavior and limitations.</param>
/// <param name="SupportsImageInput">Whether image content can be sent to the selected chat model.</param>
/// <param name="SupportsPdfInput">Whether PDF content can be sent to the selected chat model.</param>
/// <param name="SupportsAudioInput">Whether audio content can be sent to the selected chat model.</param>
/// <param name="SupportsVideoInput">Whether video content can be sent to the selected chat model.</param>
/// <param name="ContextWindowTokens">An optional effective total context limit in tokens; model catalog metadata fills it when omitted.</param>
/// <param name="MaxOutputTokens">An optional effective output limit in tokens; model catalog metadata fills it when omitted.</param>
public sealed record AgentProviderRunCapabilities(
    bool SupportsNativeToolCalling,
    bool SupportsStreamingToolCalls,
    bool SupportsMultipleToolCalls,
    string Summary,
    bool SupportsImageInput = false,
    bool SupportsPdfInput = false,
    bool SupportsAudioInput = false,
    bool SupportsVideoInput = false,
    int? ContextWindowTokens = null,
    int? MaxOutputTokens = null);
