using System.Text.Json;

namespace Sunder.Package.Agent.Contracts.Models;

public static class AgentChatModelSettingsJson
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static AgentChatModelSettings Parse(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return new AgentChatModelSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<AgentChatModelSettings>(settingsJson, JsonOptions)
                   ?? new AgentChatModelSettings();
        }
        catch (JsonException)
        {
            return new AgentChatModelSettings();
        }
    }

    public static string? Serialize(AgentChatModelSettings settings)
        => string.IsNullOrWhiteSpace(settings.ReasoningVariantId)
           && string.IsNullOrWhiteSpace(settings.SpeedOptionId)
           && string.IsNullOrWhiteSpace(settings.ModeOptionId)
            ? null
            : JsonSerializer.Serialize(settings, JsonOptions);
}
