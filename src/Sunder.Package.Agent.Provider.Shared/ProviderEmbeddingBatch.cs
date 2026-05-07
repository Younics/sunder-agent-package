using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Provider.Shared;

internal readonly record struct ProviderEmbeddingInput(int Index, string Text);

internal static class ProviderEmbeddingBatch
{
    public static AgentEmbeddingGenerationResult?[] CreateResultBuffer(
        IReadOnlyList<string> texts,
        out IReadOnlyList<ProviderEmbeddingInput> validInputs)
    {
        var results = new AgentEmbeddingGenerationResult?[texts.Count];
        if (texts.Count == 0)
        {
            validInputs = [];
            return results;
        }

        var inputs = new List<ProviderEmbeddingInput>();
        for (var index = 0; index < texts.Count; index++)
        {
            if (!string.IsNullOrWhiteSpace(texts[index]))
            {
                inputs.Add(new ProviderEmbeddingInput(index, texts[index]));
            }
        }

        validInputs = inputs;
        return results;
    }

    public static void ApplyOrderedResults(
        AgentEmbeddingGenerationResult?[] results,
        IReadOnlyList<ProviderEmbeddingInput> validInputs,
        IReadOnlyList<AgentEmbeddingGenerationResult> generatedEmbeddings)
    {
        for (var index = 0; index < validInputs.Count && index < generatedEmbeddings.Count; index++)
        {
            results[validInputs[index].Index] = generatedEmbeddings[index];
        }
    }
}
