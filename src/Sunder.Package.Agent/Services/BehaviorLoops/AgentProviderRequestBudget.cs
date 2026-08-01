using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal readonly record struct AgentProviderRequestLimits(
    int ContextWindowTokens,
    int OutputReserveTokens,
    int CompactionBufferTokens,
    int HardInputLimitTokens,
    int ProactiveInputLimitTokens)
{
    private const int DefaultContextWindowTokens = 128_000;
    private const int DefaultOutputReserveTokens = 8_192;
    private const int MaximumCompactionBufferTokens = 20_000;

    public static AgentProviderRequestLimits Resolve(AgentProviderRunCapabilities capabilities)
    {
        var contextWindow = capabilities.ContextWindowTokens is > 0
            ? capabilities.ContextWindowTokens.Value
            : DefaultContextWindowTokens;
        var outputReserve = capabilities.MaxOutputTokens is > 0
            ? Math.Min(capabilities.MaxOutputTokens.Value, contextWindow / 2)
            : Math.Min(DefaultOutputReserveTokens, contextWindow / 4);
        var hardInputLimit = Math.Max(1, contextWindow - outputReserve);
        var compactionBuffer = Math.Min(
            Math.Min(MaximumCompactionBufferTokens, outputReserve),
            hardInputLimit / 4);
        var proactiveInputLimit = Math.Max(1, hardInputLimit - compactionBuffer);
        return new AgentProviderRequestLimits(
            contextWindow,
            outputReserve,
            compactionBuffer,
            hardInputLimit,
            proactiveInputLimit);
    }
}

internal readonly record struct AgentProviderRequestEstimate(
    long EstimatedInputTokens,
    long EstimatedTextTokens,
    long EstimatedMediaTokens,
    long Utf8TextBytes,
    long BinaryBytes,
    long EstimatedPayloadBytes);

internal readonly record struct AgentProviderRequestAssessment(
    AgentProviderRequestLimits Limits,
    AgentProviderRequestEstimate Estimate)
{
    public bool FitsPayloadLimit => Estimate.EstimatedPayloadBytes
                                    <= AgentProviderRequestBudget.MaxSerializedPayloadBytes;

    public bool FitsHardLimit => Estimate.EstimatedInputTokens <= Limits.HardInputLimitTokens
                                 && FitsPayloadLimit;

    public bool NeedsCompaction => Estimate.EstimatedInputTokens > Limits.HardInputLimitTokens
                                   || !FitsPayloadLimit;

    public int TargetReductionTokens => !NeedsCompaction
        ? 0
        : (int)Math.Min(int.MaxValue, Math.Max(
            1,
            Estimate.EstimatedInputTokens - Limits.ProactiveInputLimitTokens));
}

internal static class AgentProviderRequestBudget
{
    private const int RequestFramingTokens = 512;
    private const int MessageFramingTokens = 16;
    private const int ToolFramingTokens = 32;
    internal const long MaxSerializedPayloadBytes = 32L * 1024 * 1024;

    public static AgentProviderRequestAssessment Assess(
        IReadOnlyList<ChatMessage> messages,
        string? systemInstructions,
        IReadOnlyList<AgentToolDescriptor> tools,
        AgentProviderRunCapabilities capabilities)
    {
        long textBytes = 0;
        long serializedTextBytes = 0;
        long binaryBytes = 0;
        long encodedBinaryBytes = 0;
        var contentCount = 0;
        AddRequestText(systemInstructions, ref textBytes, ref serializedTextBytes);
        foreach (var tool in tools)
        {
            AddRequestText(tool.ToolId, ref textBytes, ref serializedTextBytes);
            AddRequestText(tool.DisplayName, ref textBytes, ref serializedTextBytes);
            AddRequestText(tool.Description, ref textBytes, ref serializedTextBytes);
            AddRequestText(tool.ArgumentsJsonSchema, ref textBytes, ref serializedTextBytes);
        }

        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                contentCount++;
                switch (content)
                {
                    case TextContent text:
                        AddRequestText(text.Text, ref textBytes, ref serializedTextBytes);
                        break;
                    case FunctionCallContent call:
                        AddRequestText(call.CallId, ref textBytes, ref serializedTextBytes);
                        AddRequestText(call.Name, ref textBytes, ref serializedTextBytes);
                        AddRequestJson(call.Arguments, ref textBytes, ref serializedTextBytes);
                        break;
                    case FunctionResultContent result:
                        AddRequestText(result.CallId, ref textBytes, ref serializedTextBytes);
                        AddRequestJson(result.Result, ref textBytes, ref serializedTextBytes);
                        break;
                    case DataContent data:
                        binaryBytes = SaturatingAdd(binaryBytes, data.Data.Length);
                        encodedBinaryBytes = SaturatingAdd(
                            encodedBinaryBytes,
                            EstimateBase64EncodedLength(data.Data.Length));
                        AddRequestText(data.MediaType, ref textBytes, ref serializedTextBytes);
                        AddRequestText(data.Name, ref textBytes, ref serializedTextBytes);
                        break;
                    default:
                        AddRequestText(content.ToString(), ref textBytes, ref serializedTextBytes);
                        break;
                }
            }
        }

        var textTokens = DivideRoundUp(textBytes, 4);
        var mediaTokens = DivideRoundUp(binaryBytes, 3);
        var framingTokens = SaturatingAdd(
            RequestFramingTokens,
            SaturatingAdd(
                messages.Count * (long)MessageFramingTokens,
                tools.Count * (long)ToolFramingTokens));
        // Providers meter decoded media by modality and dimensions rather than raw/base64 bytes.
        // Keep media sizing observable, but let provider overflow recovery handle its context cost.
        var estimatedInputTokens = SaturatingAdd(framingTokens, textTokens);
        var payloadFramingBytes = SaturatingAdd(
            4_096,
            SaturatingAdd(
                messages.Count * 256L,
                SaturatingAdd(tools.Count * 512L, contentCount * 256L)));
        var estimatedPayloadBytes = SaturatingAdd(
            payloadFramingBytes,
            SaturatingAdd(serializedTextBytes, encodedBinaryBytes));
        return new AgentProviderRequestAssessment(
            AgentProviderRequestLimits.Resolve(capabilities),
            new AgentProviderRequestEstimate(
                estimatedInputTokens,
                textTokens,
                mediaTokens,
                textBytes,
                binaryBytes,
                estimatedPayloadBytes));
    }

    public static int EstimateTurnTokens(AgentTurnRecord turn)
    {
        long textBytes = 0;
        foreach (var item in turn.Items)
        {
            AddText(item.TextContent, ref textBytes);
            AddText(item.ArgumentsJson, ref textBytes);
            AddText(item.ResultSummary, ref textBytes);
            AddText(item.StructuredPayloadJson, ref textBytes);
            AddText(item.SourcesJson, ref textBytes);
        }

        return (int)Math.Min(
            int.MaxValue,
            SaturatingAdd(MessageFramingTokens, DivideRoundUp(textBytes, 4)));
    }

    public static int EstimateTextTokens(IEnumerable<string?> values)
    {
        long textBytes = 0;
        foreach (var value in values)
        {
            AddText(value, ref textBytes);
        }

        return (int)Math.Min(int.MaxValue, DivideRoundUp(textBytes, 4));
    }

    private static void AddRequestJson(
        object? value,
        ref long textBytes,
        ref long serializedTextBytes)
    {
        if (value is null)
        {
            return;
        }

        try
        {
            AddRequestText(
                JsonSerializer.Serialize(value),
                ref textBytes,
                ref serializedTextBytes);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            AddRequestText(value.ToString(), ref textBytes, ref serializedTextBytes);
        }
    }

    private static void AddRequestText(
        string? value,
        ref long textBytes,
        ref long serializedTextBytes)
    {
        AddText(value, ref textBytes);
        if (!string.IsNullOrEmpty(value))
        {
            serializedTextBytes = SaturatingAdd(
                serializedTextBytes,
                SaturatingAdd(JsonEncodedText.Encode(value).EncodedUtf8Bytes.Length, 2));
        }
    }

    private static void AddText(string? value, ref long textBytes)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        textBytes = SaturatingAdd(textBytes, Encoding.UTF8.GetByteCount(value));
    }

    private static long DivideRoundUp(long value, int divisor)
        => value <= 0 ? 0 : 1 + ((value - 1) / divisor);

    private static long EstimateBase64EncodedLength(long byteCount)
        => byteCount <= 0
            ? 0
            : SaturatingMultiply(DivideRoundUp(byteCount, 3), 4);

    private static long SaturatingAdd(long left, long right)
        => right > long.MaxValue - left ? long.MaxValue : left + right;

    private static long SaturatingMultiply(long value, int multiplier)
        => value > long.MaxValue / multiplier ? long.MaxValue : value * multiplier;
}

internal sealed class AgentPromptTooLargeException(string message) : InvalidOperationException(message);
