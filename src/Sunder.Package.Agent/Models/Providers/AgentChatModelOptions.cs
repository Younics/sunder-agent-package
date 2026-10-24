using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Sunder.Package.Agent.Contracts.Models;

// One boundary for exact persisted option IDs and legacy MEAI hints. In particular,
// max must never be coerced into ExtraHigh or disappear during RPC adaptation.
internal static class AgentChatModelOptions
{
    public static string? ReasoningId(ChatOptions? options)
    {
        var selected = Read(options, AgentChatModelOptionKeys.ReasoningVariantId);
        var legacy = options?.Reasoning?.Effort switch
        {
            null => null,
            ReasoningEffort.None => "none",
            ReasoningEffort.Low => "low",
            ReasoningEffort.Medium => "medium",
            ReasoningEffort.High => "high",
            ReasoningEffort.ExtraHigh => "xhigh",
            _ => throw Invalid("reasoning", options?.Reasoning?.Effort.ToString() ?? "unknown"),
        };
        if (selected is not null && legacy is not null && selected != legacy)
            throw Invalid("conflicting reasoning", selected);
        return selected ?? legacy;
    }

    public static string? SpeedId(ChatOptions? options) => Read(options, AgentChatModelOptionKeys.SpeedOptionId);

    public static string? ModeId(ChatOptions? options) => Read(options, AgentChatModelOptionKeys.ModeOptionId);

    public static void Validate(
        ChatOptions? options,
        string modelId,
        IEnumerable<string>? reasoning,
        IEnumerable<string>? speeds,
        IEnumerable<string>? modes = null)
    {
        ValidateChoice("reasoning", ReasoningId(options), reasoning, modelId);
        var speed = SpeedId(options);
        if (speed != "normal") ValidateChoice("speed", speed, speeds, modelId);
        ValidateChoice("mode", ModeId(options), modes, modelId);
    }

    private static void ValidateChoice(string kind, string? id, IEnumerable<string>? allowed, string modelId)
    {
        if (id is not null && !(allowed?.Contains(id, StringComparer.OrdinalIgnoreCase) ?? false))
            throw Invalid(kind, id, modelId);
    }

    private static string? Read(ChatOptions? options, string key)
    {
        if (options?.AdditionalProperties?.TryGetValue(key, out var value) != true || value is null) return null;
        var text = value switch
        {
            string item => item,
            JsonElement { ValueKind: JsonValueKind.String } item => item.GetString(),
            _ => throw Invalid("option", key),
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim().ToLowerInvariant();
    }

    private static AgentChatProviderException Invalid(string kind, string id, string? modelId = null)
    {
        var message = $"The selected {kind} option '{id}' is not supported" +
                      (modelId is null ? "." : $" by '{modelId}'.");
        return new AgentChatProviderException(message, message, "agent-model-option-unsupported");
    }
}
