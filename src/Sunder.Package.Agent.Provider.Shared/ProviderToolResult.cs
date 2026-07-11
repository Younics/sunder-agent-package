using System.Text.Json;

namespace Sunder.Package.Agent.Provider.Shared;

internal static class ProviderToolResult
{
    public static string RenderText(object? result)
        => result switch
        {
            null => string.Empty,
            string text => text,
            JsonElement jsonElement => jsonElement.GetRawText(),
            _ => JsonSerializer.Serialize(result),
        };
}
