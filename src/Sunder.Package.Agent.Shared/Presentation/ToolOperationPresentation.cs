using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sunder.Package.Agent.Shared.Presentation;

// Display/continuation hint only. This never changes execution, permission or replay authority.
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class ToolOperationPresentation
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
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

    internal static bool ObservationUnavailable(string? payload)
    {
        if (Read(payload) is null) return false;
        using var json = JsonDocument.Parse(payload!);
        return json.RootElement.TryGetProperty("operationObservation", out var observation)
            && observation.ValueKind == JsonValueKind.String && observation.GetString() == "unavailable";
    }

    internal static bool IsTerminal(string? payload)
        => Read(payload) is "Applied" or "Opened" or "Input dispatched" or "Failed" or "Cancelled";

    internal static string Correlate(string? original, string observed)
    {
        using var json = JsonDocument.Parse(observed);
        var root = json.RootElement;
        return JsonSerializer.Serialize(new
        {
            operationStatus = root.GetProperty("operationStatus").GetString(),
            operationRef = Reference(observed),
            operationPhase = root.TryGetProperty("operationPhase", out var phase) && phase.ValueKind == JsonValueKind.String
                && phase.GetString() is { Length: <= 32 } text ? text : null,
            operationObservation = ObservationUnavailable(original) ? "unavailable" : null,
        }, Json);
    }
}
