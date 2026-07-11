using Sunder.Package.Agent.Provider.Shared;

namespace Sunder.Package.Agent.Provider.OpenAI;

internal static class OpenAiModelIds
{
    public static string Normalize(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return modelId;
        }

        var normalized = ProviderModelId.RemovePrefix(modelId, "openai");

        const string fastSuffix = "-fast";
        return normalized.EndsWith(fastSuffix, StringComparison.OrdinalIgnoreCase)
            ? normalized[..^fastSuffix.Length]
            : normalized;
    }
}
