namespace Sunder.Package.Agent.Provider.OpenAI;

internal static class OpenAiModelIds
{
    public static string Normalize(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return modelId;
        }

        const string openAiPrefix = "openai/";
        var normalized = modelId.StartsWith(openAiPrefix, StringComparison.OrdinalIgnoreCase)
            ? modelId[openAiPrefix.Length..]
            : modelId;

        const string fastSuffix = "-fast";
        return normalized.EndsWith(fastSuffix, StringComparison.OrdinalIgnoreCase)
            ? normalized[..^fastSuffix.Length]
            : normalized;
    }
}
