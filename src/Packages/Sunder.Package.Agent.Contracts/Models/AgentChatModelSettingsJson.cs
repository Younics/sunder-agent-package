using System.Text.Json;

namespace Sunder.Package.Agent.Contracts.Models;

public static class AgentChatModelSettingsJson
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static AgentChatModelSettings Parse(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return new AgentChatModelSettings(null);
        }

        try
        {
            return JsonSerializer.Deserialize<AgentChatModelSettings>(settingsJson, JsonOptions)
                   ?? new AgentChatModelSettings(null);
        }
        catch (JsonException)
        {
            return new AgentChatModelSettings(null);
        }
    }

    public static string? Serialize(AgentChatModelSettings settings)
        => string.IsNullOrWhiteSpace(settings.ReasoningVariantId)
            ? null
            : JsonSerializer.Serialize(settings, JsonOptions);
}
