using System.Text.Json;

namespace Sunder.Package.Agent.Provider.Shared;

internal static class ProviderJson
{
    public static bool TryParseObjectArguments(
        string? argumentsJson,
        out IDictionary<string, object?> arguments)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                arguments = null!;
                return false;
            }

            arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                arguments[property.Name] = property.Value.Clone();
            }

            return true;
        }
        catch (JsonException)
        {
            arguments = null!;
            return false;
        }
    }
}
