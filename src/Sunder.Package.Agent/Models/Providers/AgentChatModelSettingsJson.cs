using System.Text.Json;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Serializes the optional model settings stored with a chat model binding.
/// </summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public static class AgentChatModelSettingsJson
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    /// <summary>Parses persisted model settings without making malformed optional configuration fatal.</summary>
    /// <param name="settingsJson">The JSON settings document, or <see langword="null" /> when no options were stored.</param>
    /// <returns>The parsed settings; empty settings are returned for blank, malformed, or JSON <see langword="null" /> input.</returns>
    public static AgentChatModelSettings Parse(string? settingsJson)
        => TryParse(settingsJson, out var settings) ? settings : new AgentChatModelSettings();

    /// <summary>Parses optional settings while allowing callers to distinguish corrupt configuration from defaults.</summary>
    public static bool TryParse(string? settingsJson, out AgentChatModelSettings settings)
    {
        settings = new AgentChatModelSettings();
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return true;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<AgentChatModelSettings>(settingsJson, JsonOptions);
            if (parsed is null) return false;
            settings = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Serializes model option identifiers for persistence.</summary>
    /// <param name="settings">The settings to serialize.</param>
    /// <returns>Web-default JSON, or <see langword="null" /> when every option is blank.</returns>
    public static string? Serialize(AgentChatModelSettings settings)
        => string.IsNullOrWhiteSpace(settings.ReasoningVariantId)
           && string.IsNullOrWhiteSpace(settings.SpeedOptionId)
           && string.IsNullOrWhiteSpace(settings.ModeOptionId)
            ? null
            : JsonSerializer.Serialize(settings, JsonOptions);
}
