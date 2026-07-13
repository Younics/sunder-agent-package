using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sunder.Package.Agent.Contracts.Models;

public sealed class AgentToolArgumentObject
{
    private readonly JsonElement _root;

    private AgentToolArgumentObject(JsonElement root)
    {
        _root = root;
    }

    public static bool TryParse(string? argumentsJson, out AgentToolArgumentObject? arguments, out string? error)
    {
        arguments = null;
        error = null;
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            using var emptyDocument = JsonDocument.Parse("{}");
            arguments = new AgentToolArgumentObject(emptyDocument.RootElement.Clone());
            return true;
        }

        try
        {
            if (Encoding.UTF8.GetByteCount(argumentsJson) > AgentPayloadLimits.MaxToolArgumentBytes)
            {
                error = $"Arguments exceed the {AgentPayloadLimits.MaxToolArgumentBytes}-byte limit.";
                return false;
            }

            using var document = JsonDocument.Parse(argumentsJson, new JsonDocumentOptions
            {
                MaxDepth = AgentPayloadLimits.MaxToolArgumentJsonDepth,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Arguments must be a JSON object.";
                return false;
            }

            if (!TryValidateProperties(document.RootElement, out error))
            {
                return false;
            }

            arguments = new AgentToolArgumentObject(document.RootElement.Clone());
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Arguments must be valid JSON: {ex.Message}";
            return false;
        }
    }

    public bool TryGetProperty(string propertyName, out JsonElement value)
        => TryGetProperty(_root, propertyName, out value);

    public bool TryReadRequiredString(string propertyName, out string? value, out string? error)
    {
        if (!TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            value = null;
            error = $"The `{propertyName}` parameter is required.";
            return false;
        }

        return TryReadStringProperty(propertyName, property, out value, out error);
    }

    public bool TryReadOptionalString(string propertyName, out string? value, out string? error)
    {
        if (!TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            value = null;
            error = null;
            return true;
        }

        return TryReadStringProperty(propertyName, property, out value, out error);
    }

    public bool TryReadOptionalInt32(string propertyName, out int? value, out string? error)
    {
        value = null;
        error = null;
        if (!TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var intValue))
        {
            value = intValue;
            return true;
        }

        if (property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
        {
            value = intValue;
            return true;
        }

        error = $"The `{propertyName}` parameter must be an integer.";
        return false;
    }

    public bool TryReadOptionalBoolean(string propertyName, out bool? value, out string? error)
    {
        value = null;
        error = null;
        if (!TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var intValue) && intValue is 0 or 1)
        {
            value = intValue == 1;
            return true;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            var text = property.GetString()?.Trim();
            if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "1", StringComparison.OrdinalIgnoreCase))
            {
                value = true;
                return true;
            }

            if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "no", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "0", StringComparison.OrdinalIgnoreCase))
            {
                value = false;
                return true;
            }
        }

        error = $"The `{propertyName}` parameter must be true or false.";
        return false;
    }

    public bool TryReadObjectArray(string propertyName, bool allowSingleObject, out IReadOnlyList<JsonElement> values, out string? error)
    {
        values = [];
        error = null;
        if (!TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.Array)
        {
            var items = new List<JsonElement>();
            foreach (var item in property.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    error = $"Each `{propertyName}` item must be a JSON object.";
                    return false;
                }

                items.Add(item.Clone());
            }

            values = items;
            return true;
        }

        if (allowSingleObject && property.ValueKind == JsonValueKind.Object)
        {
            values = [property.Clone()];
            return true;
        }

        error = $"The `{propertyName}` parameter must be an array.";
        return false;
    }

    public IReadOnlyDictionary<string, object?> ToDictionary()
        => JsonSerializer.Deserialize<Dictionary<string, object?>>(_root.GetRawText()) ?? new Dictionary<string, object?>();

    public static bool TryReadOptionalString(JsonElement element, string propertyName, out string? value, out string? error)
    {
        if (!TryGetProperty(element, propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            value = null;
            error = null;
            return true;
        }

        return TryReadStringProperty(propertyName, property, out value, out error);
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryValidateProperties(JsonElement root, out string? error)
    {
        var pending = new Stack<JsonElement>();
        pending.Push(root);
        var propertyCount = 0;
        while (pending.TryPop(out var element))
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                {
                    propertyCount++;
                    if (propertyCount > AgentPayloadLimits.MaxToolArgumentProperties)
                    {
                        error = $"Arguments exceed the {AgentPayloadLimits.MaxToolArgumentProperties}-property limit.";
                        return false;
                    }

                    if (!names.Add(property.Name))
                    {
                        error = $"Arguments contain duplicate or case-colliding property '{property.Name}'.";
                        return false;
                    }

                    pending.Push(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    pending.Push(item);
                }
            }
        }

        error = null;
        return true;
    }

    private static bool TryReadStringProperty(string propertyName, JsonElement property, out string? value, out string? error)
    {
        if (property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            error = null;
            return true;
        }

        value = null;
        error = $"The `{propertyName}` parameter must be a string.";
        return false;
    }
}
