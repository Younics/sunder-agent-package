using System.Buffers.Binary;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.HistorySearch;

internal static class HistoryVectorCodec
{
    internal static bool TryNormalize(
        AgentEmbeddingGenerationResult? result,
        string expectedModelId,
        int? expectedDimensions,
        out int dimensions,
        out byte[] vector)
    {
        dimensions = 0;
        vector = [];
        if (result is null
            || !string.Equals(result.ModelId?.Trim(), expectedModelId.Trim(), StringComparison.Ordinal)
            || result.Values.Count is <= 0 or > HistorySearchLimits.MaximumVectorDimensions
            || expectedDimensions is { } expected && result.Values.Count != expected)
        {
            return false;
        }

        double sum = 0;
        foreach (var value in result.Values)
        {
            if (!double.IsFinite(value))
            {
                return false;
            }
            sum += value * value;
        }
        if (!double.IsFinite(sum) || sum <= 1e-20)
        {
            return false;
        }

        dimensions = result.Values.Count;
        vector = new byte[checked(dimensions * sizeof(float))];
        var scale = 1d / Math.Sqrt(sum);
        for (var index = 0; index < dimensions; index++)
        {
            var normalized = (float)(result.Values[index] * scale);
            if (!float.IsFinite(normalized))
            {
                dimensions = 0;
                vector = [];
                return false;
            }
            BinaryPrimitives.WriteInt32LittleEndian(
                vector.AsSpan(index * sizeof(float), sizeof(float)),
                BitConverter.SingleToInt32Bits(normalized));
        }
        return true;
    }

    internal static double Dot(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, int dimensions)
    {
        if (dimensions <= 0
            || dimensions > HistorySearchLimits.MaximumVectorDimensions
            || left.Length != checked(dimensions * sizeof(float))
            || right.Length != checked(dimensions * sizeof(float)))
        {
            return double.NegativeInfinity;
        }
        double score = 0;
        for (var index = 0; index < dimensions; index++)
        {
            var offset = index * sizeof(float);
            var leftValue = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
                left.Slice(offset, sizeof(float))));
            var rightValue = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
                right.Slice(offset, sizeof(float))));
            if (!float.IsFinite(leftValue) || !float.IsFinite(rightValue))
            {
                return double.NegativeInfinity;
            }
            score += (double)leftValue * rightValue;
        }
        return double.IsFinite(score) ? score : double.NegativeInfinity;
    }
}
