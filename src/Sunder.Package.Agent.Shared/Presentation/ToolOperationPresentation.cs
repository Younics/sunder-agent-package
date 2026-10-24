using System.Text.Json;

namespace Sunder.Package.Agent.Shared.Presentation;

// Display/continuation hint only. This never changes execution, permission or replay authority.
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class ToolOperationPresentation
{
    internal static string? Read(string? payload)
    {
        if (payload is null || payload.Length > 256) return null;
        try
        {
            using var json = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 2 });
            return json.RootElement.TryGetProperty("operationStatus", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() switch { "running" => "Running", "applied" => "Applied", "opened" => "Opened", "dispatched" => "Input dispatched", "unknown" => "Unconfirmed", "failed" or "rejected" => "Failed", "cancelled" => "Cancelled", _ => null } : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return null; }
    }

    internal static string? Compact(string? payload)
        => Read(payload) is null ? null : payload;

    internal static string? Reference(string? payload)
    {
        if (Read(payload) is null) return null;
        using var json = JsonDocument.Parse(payload!);
        return json.RootElement.TryGetProperty("operationRef", out var reference) && reference.ValueKind == JsonValueKind.String
            && reference.GetString() is { Length: > 0 and <= 40 } text ? text : null;
    }
}
