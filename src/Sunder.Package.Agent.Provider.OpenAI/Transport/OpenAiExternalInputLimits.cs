namespace Sunder.Package.Agent.Provider.OpenAI.Transport;

/// <summary>Bounds memory retained from the upstream OpenAI SSE connection.</summary>
/// <remarks>These are provider input-safety policies, not Agent RPC transport limits.</remarks>
internal static class OpenAiExternalInputLimits
{
    internal const int MaximumSseLineBytes = 1024 * 1024;
    internal const int MaximumStreamedToolArgumentBytes = 1024 * 1024;
}
