using Sunder.Package.Agent.Provider.Shared;

namespace Sunder.Package.Agent.Provider.OpenAI;

internal static class OpenAiErrorDiagnosticExtractor
{
    public static string? Extract(string? payload)
        => ProviderErrorDiagnosticExtractor.Extract(payload);
}
