using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Sunder.Package.Agent.Provider.Shared;

internal static class ProviderToolResult
{
    public static IReadOnlyList<AIContent>? Parts(object? result) => result is IEnumerable<AIContent> parts ? parts.ToArray() : null;
    public static string RenderText(object? result)
        => result switch
        {
            null => string.Empty,
            string text => text,
            IEnumerable<AIContent> parts when parts.All(part => part is TextContent) => string.Join("\n", parts.OfType<TextContent>().Select(part => part.Text)),
            IEnumerable<AIContent> => throw new NotSupportedException("This provider cannot stringify a tool image. Use an image-capable provider transport."),
            JsonElement jsonElement => jsonElement.GetRawText(),
            _ => JsonSerializer.Serialize(result),
        };
}
