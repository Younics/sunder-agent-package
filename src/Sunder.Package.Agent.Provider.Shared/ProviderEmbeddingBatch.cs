using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Provider.Shared;

internal readonly record struct ProviderEmbeddingInput(int Index, string Text);

internal readonly record struct ProviderIndexedEmbedding(
    int VendorIndex,
    AgentEmbeddingGenerationResult Embedding);

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

    public static void ApplyIndexedResults(
        AgentEmbeddingGenerationResult?[] results,
        IReadOnlyList<ProviderEmbeddingInput> validInputs,
        IReadOnlyList<ProviderIndexedEmbedding> generatedEmbeddings)
    {
        var seenIndices = new bool[validInputs.Count];
        foreach (var generated in generatedEmbeddings)
        {
            if (generated.VendorIndex < 0 || generated.VendorIndex >= validInputs.Count)
            {
                throw new InvalidOperationException(
                    $"Embedding result index {generated.VendorIndex} is outside the expected range 0..{validInputs.Count - 1}.");
            }

            if (seenIndices[generated.VendorIndex])
            {
                throw new InvalidOperationException($"Embedding results contain duplicate index {generated.VendorIndex}.");
            }

            seenIndices[generated.VendorIndex] = true;
        }

        if (generatedEmbeddings.Count != validInputs.Count || seenIndices.Any(seen => !seen))
        {
            var missingIndices = seenIndices
                .Select((seen, index) => (seen, index))
                .Where(item => !item.seen)
                .Select(item => item.index);
            throw new InvalidOperationException(
                $"Embedding result count/index mismatch: expected {validInputs.Count}, received {generatedEmbeddings.Count}; " +
                $"missing indices: {string.Join(", ", missingIndices)}.");
        }

        foreach (var generated in generatedEmbeddings)
        {
            results[validInputs[generated.VendorIndex].Index] = generated.Embedding;
        }
    }
}
