using System.Text.Json;

namespace Sunder.Package.Agent.Shared.Presentation;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class FileToolHeaderHint
{
    internal static string? FromArguments(string? toolId, string? argumentsJson)
    {
        if (!string.Equals(toolId, "write", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String
                ? BoundPath(path.GetString()) : null;
        }
        catch (JsonException) { return null; }
    }

    internal static string? BoundPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var text = new string(path.Take(241).Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        return text.Length > 240 ? text[..237] + "..." : text;
    }
}
