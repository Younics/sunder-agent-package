using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Mcp;

internal static class McpToolTimeoutMetadata
{
    internal const string MetadataMarkerPropertyName = "__sunder_mcp_metadata_version";
    internal const string MetadataMarkerValue = "sunder.mcp.call/1";
    internal const string TimeoutPropertyName = "__sunder_expected_timeout_seconds";
    internal const string EnvelopePropertyName = "__sunder_mcp_call";
    internal const string EnvelopeArgumentsPropertyName = "arguments";
    internal const int MinimumSeconds = 1;
    internal const int MaximumSeconds = 1800;

    private const string EmbeddedArgumentsSchemaId = "urn:sunder:mcp:arguments:1";
    private const string EmbeddedArgumentsSchemaMarker = "sunder:mcp-arguments:1";
    private const string TimeoutDescription =
        "Optional Sunder-only expected execution limit for this MCP call, in whole seconds (1-1800). "
        + "Use null or omit it when no expected per-call limit is needed. Sunder never forwards this field to the MCP server.";

    private static readonly string[] FlatUnsafeKeywords =
    [
        "$schema", "$id", "$anchor", "$dynamicAnchor", "$recursiveAnchor", "$vocabulary",
        "$defs", "definitions",
        "$ref", "$dynamicRef", "$recursiveRef",
        "allOf", "anyOf", "oneOf", "not", "if", "then", "else",
        "patternProperties", "propertyNames", "unevaluatedProperties",
        "dependentRequired", "dependentSchemas", "dependencies",
        "contains", "minContains", "maxContains", "prefixItems", "unevaluatedItems",
        "minProperties", "maxProperties", "const", "enum",
    ];

    public static AugmentedMcpToolSchema AugmentSchema(JsonElement originalSchema)
    {
        var root = originalSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? new JsonObject { ["type"] = "object" }
            : JsonNode.Parse(originalSchema.GetRawText()) as JsonObject
              ?? throw new InvalidDataException("An MCP tool input schema must be a JSON object.");
        return CanUseFlatMetadata(root)
            ? new AugmentedMcpToolSchema(
                McpToolArgumentSchemaMode.Flat,
                JsonSerializer.SerializeToElement(CreateFlatSchema(root)))
            : new AugmentedMcpToolSchema(
                McpToolArgumentSchemaMode.Envelope,
                JsonSerializer.SerializeToElement(CreateEnvelopeSchema(root)));
    }

    public static bool TryExtract(
        AgentToolArgumentObject parsedArguments,
        out IReadOnlyDictionary<string, object?> forwardedArguments,
        out int? timeoutSeconds,
        out string? error)
    {
        var arguments = new Dictionary<string, object?>(parsedArguments.ToDictionary(), StringComparer.OrdinalIgnoreCase);
        if (parsedArguments.TryGetProperty(EnvelopePropertyName, out var envelope)
            && LooksLikeEnvelope(envelope))
        {
            return TryExtractEnvelope(
                arguments,
                envelope,
                out forwardedArguments,
                out timeoutSeconds,
                out error);
        }

        if (parsedArguments.TryGetProperty(MetadataMarkerPropertyName, out var marker))
        {
            return TryExtractFlat(
                arguments,
                marker,
                parsedArguments,
                out forwardedArguments,
                out timeoutSeconds,
                out error);
        }

        forwardedArguments = arguments;
        timeoutSeconds = null;
        error = null;
        return true;
    }

    public static McpToolArgumentPresentation SeparateForPresentation(string argumentsJson)
    {
        try
        {
            var root = JsonNode.Parse(argumentsJson) as JsonObject;
            if (root is null)
            {
                return new McpToolArgumentPresentation(argumentsJson, null);
            }

            if (TryGetProperty(root, EnvelopePropertyName, out _, out var envelopeNode)
                && envelopeNode is JsonObject envelope
                && HasExpectedMarker(envelope)
                && TryGetProperty(envelope, EnvelopeArgumentsPropertyName, out _, out var forwardedNode)
                && forwardedNode is JsonObject forwarded)
            {
                return new McpToolArgumentPresentation(
                    forwarded.ToJsonString(),
                    ReadPresentationLimit(envelope));
            }

            if (!HasExpectedMarker(root))
            {
                return new McpToolArgumentPresentation(argumentsJson, null);
            }

            var flat = (JsonObject)root.DeepClone();
            RemoveProperty(flat, MetadataMarkerPropertyName);
            var expectedLimitText = ReadPresentationLimit(flat);
            RemoveProperty(flat, TimeoutPropertyName);
            return new McpToolArgumentPresentation(flat.ToJsonString(), expectedLimitText);
        }
        catch (JsonException)
        {
            return new McpToolArgumentPresentation(argumentsJson, null);
        }
    }

    private static JsonObject CreateFlatSchema(JsonObject root)
    {
        var properties = (JsonObject)root["properties"]!;
        properties[MetadataMarkerPropertyName] = CreateMarkerSchema();
        properties[TimeoutPropertyName] = CreateTimeoutSchema();
        var required = root["required"] as JsonArray ?? new JsonArray();
        required.Add(MetadataMarkerPropertyName);
        root["required"] = required;
        return root;
    }

    private static JsonObject CreateEnvelopeSchema(JsonObject originalSchema)
    {
        var embeddedArgumentsSchema = EmbedArgumentsSchema(originalSchema);

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [EnvelopePropertyName] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "Sunder MCP call envelope. Put the complete MCP tool argument object in 'arguments'.",
                    ["properties"] = new JsonObject
                    {
                        [MetadataMarkerPropertyName] = CreateMarkerSchema(),
                        [EnvelopeArgumentsPropertyName] = embeddedArgumentsSchema,
                        [TimeoutPropertyName] = CreateTimeoutSchema(),
                    },
                    ["required"] = new JsonArray(MetadataMarkerPropertyName, EnvelopeArgumentsPropertyName),
                    ["additionalProperties"] = false,
                },
            },
            ["required"] = new JsonArray(EnvelopePropertyName),
            ["additionalProperties"] = false,
        };
    }

    private static JsonObject EmbedArgumentsSchema(JsonObject originalSchema)
    {
        if (!originalSchema.ContainsKey("$id"))
        {
            originalSchema["$id"] = EmbeddedArgumentsSchemaId;
            return originalSchema;
        }

        return new JsonObject
        {
            ["$comment"] = EmbeddedArgumentsSchemaMarker,
            ["allOf"] = new JsonArray(originalSchema),
        };
    }

    private static JsonObject CreateMarkerSchema()
        => new()
        {
            ["type"] = "string",
            ["const"] = MetadataMarkerValue,
            ["description"] = $"Required Sunder envelope marker. Always provide exactly '{MetadataMarkerValue}'.",
        };

    private static JsonObject CreateTimeoutSchema()
        => new()
        {
            ["type"] = new JsonArray("integer", "null"),
            ["minimum"] = MinimumSeconds,
            ["maximum"] = MaximumSeconds,
            ["description"] = TimeoutDescription,
        };

    private static bool CanUseFlatMetadata(JsonObject root)
    {
        if (root["type"] is not JsonValue type
            || !type.TryGetValue<string>(out var typeName)
            || !string.Equals(typeName, "object", StringComparison.Ordinal)
            || root["properties"] is not JsonObject properties
            || root["additionalProperties"] is not JsonValue additionalProperties
            || !additionalProperties.TryGetValue<bool>(out var allowsAdditionalProperties)
            || allowsAdditionalProperties
            || ContainsUnsafeSchemaKeyword(root))
        {
            return false;
        }

        var required = root["required"] as JsonArray;
        if (root["required"] is not null
            && (required is null || required.Any(item => item is not JsonValue value || !value.TryGetValue<string>(out _))))
        {
            return false;
        }

        return !ContainsName(properties.Select(static property => property.Key), MetadataMarkerPropertyName)
               && !ContainsName(properties.Select(static property => property.Key), TimeoutPropertyName)
               && !ContainsName(properties.Select(static property => property.Key), EnvelopePropertyName)
               && !ContainsName(ReadRequiredNames(required), MetadataMarkerPropertyName)
               && !ContainsName(ReadRequiredNames(required), TimeoutPropertyName)
               && !ContainsName(ReadRequiredNames(required), EnvelopePropertyName);
    }

    private static bool TryExtractFlat(
        Dictionary<string, object?> arguments,
        JsonElement marker,
        AgentToolArgumentObject parsedArguments,
        out IReadOnlyDictionary<string, object?> forwardedArguments,
        out int? timeoutSeconds,
        out string? error)
    {
        if (!IsExpectedMarker(marker))
        {
            forwardedArguments = arguments;
            timeoutSeconds = null;
            error = $"The `{MetadataMarkerPropertyName}` Sunder metadata marker is invalid.";
            return false;
        }

        arguments.Remove(MetadataMarkerPropertyName);
        if (!parsedArguments.TryGetProperty(TimeoutPropertyName, out var timeout))
        {
            forwardedArguments = arguments;
            timeoutSeconds = null;
            error = null;
            return true;
        }

        arguments.Remove(TimeoutPropertyName);
        forwardedArguments = arguments;
        return TryReadTimeout(timeout, out timeoutSeconds, out error);
    }

    private static bool TryExtractEnvelope(
        IReadOnlyDictionary<string, object?> rootArguments,
        JsonElement envelope,
        out IReadOnlyDictionary<string, object?> forwardedArguments,
        out int? timeoutSeconds,
        out string? error)
    {
        forwardedArguments = new Dictionary<string, object?>();
        timeoutSeconds = null;
        if (envelope.ValueKind != JsonValueKind.Object
            || !TryGetProperty(envelope, MetadataMarkerPropertyName, out var marker)
            || !IsExpectedMarker(marker))
        {
            error = $"The `{EnvelopePropertyName}` Sunder envelope marker is invalid.";
            return false;
        }
        if (rootArguments.Count != 1)
        {
            error = $"The `{EnvelopePropertyName}` Sunder envelope cannot be combined with top-level MCP arguments.";
            return false;
        }
        if (!HasOnlyEnvelopeProperties(envelope))
        {
            error = $"The `{EnvelopePropertyName}` Sunder envelope contains an unsupported property.";
            return false;
        }
        if (!TryGetProperty(envelope, EnvelopeArgumentsPropertyName, out var arguments)
            || arguments.ValueKind != JsonValueKind.Object)
        {
            error = $"The `{EnvelopePropertyName}.{EnvelopeArgumentsPropertyName}` value must be an MCP argument object.";
            return false;
        }

        forwardedArguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments.GetRawText())
                             ?? new Dictionary<string, object?>();
        if (!TryGetProperty(envelope, TimeoutPropertyName, out var timeout))
        {
            error = null;
            return true;
        }
        return TryReadTimeout(timeout, out timeoutSeconds, out error);
    }

    private static bool TryReadTimeout(JsonElement value, out int? timeoutSeconds, out string? error)
    {
        timeoutSeconds = null;
        error = null;
        if (value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var seconds)
            || seconds is < MinimumSeconds or > MaximumSeconds)
        {
            error = $"The `{TimeoutPropertyName}` Sunder expected timeout must be null or a whole number from {MinimumSeconds} to {MaximumSeconds} seconds.";
            return false;
        }

        timeoutSeconds = seconds;
        return true;
    }

    private static bool LooksLikeEnvelope(JsonElement envelope)
        => envelope.ValueKind == JsonValueKind.Object
           && TryGetProperty(envelope, MetadataMarkerPropertyName, out var marker)
           && IsExpectedMarker(marker);

    private static bool ContainsUnsafeSchemaKeyword(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (FlatUnsafeKeywords.Contains(property.Key, StringComparer.Ordinal)
                    || ContainsUnsafeSchemaKeyword(property.Value))
                {
                    return true;
                }
            }
        }
        else if (node is JsonArray array)
        {
            return array.Any(ContainsUnsafeSchemaKeyword);
        }

        return false;
    }

    private static bool HasOnlyEnvelopeProperties(JsonElement envelope)
        => envelope.EnumerateObject().All(property =>
            string.Equals(property.Name, MetadataMarkerPropertyName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(property.Name, EnvelopeArgumentsPropertyName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(property.Name, TimeoutPropertyName, StringComparison.OrdinalIgnoreCase));

    private static bool HasExpectedMarker(JsonObject value)
        => TryGetProperty(value, MetadataMarkerPropertyName, out _, out var marker)
           && marker is JsonValue markerValue
           && markerValue.TryGetValue<string>(out var markerText)
           && string.Equals(markerText, MetadataMarkerValue, StringComparison.Ordinal);

    private static bool IsExpectedMarker(JsonElement marker)
        => marker.ValueKind == JsonValueKind.String
           && string.Equals(marker.GetString(), MetadataMarkerValue, StringComparison.Ordinal);

    private static string? ReadPresentationLimit(JsonObject value)
    {
        if (!TryGetProperty(value, TimeoutPropertyName, out _, out var timeout) || timeout is null)
        {
            return null;
        }
        if (timeout.GetValueKind() == JsonValueKind.Null)
        {
            return null;
        }
        return timeout is JsonValue jsonValue
               && jsonValue.TryGetValue<int>(out var seconds)
               && seconds is >= MinimumSeconds and <= MaximumSeconds
            ? $"{seconds} {(seconds == 1 ? "second" : "seconds")}"
            : $"Invalid; expected {MinimumSeconds}-{MaximumSeconds} whole seconds";
    }

    private static bool TryGetProperty(JsonElement value, string propertyName, out JsonElement propertyValue)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    propertyValue = property.Value;
                    return true;
                }
            }
        }

        propertyValue = default;
        return false;
    }

    private static bool TryGetProperty(
        JsonObject value,
        string propertyName,
        out string actualName,
        out JsonNode? propertyValue)
    {
        foreach (var property in value)
        {
            if (string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                actualName = property.Key;
                propertyValue = property.Value;
                return true;
            }
        }

        actualName = string.Empty;
        propertyValue = null;
        return false;
    }

    private static void RemoveProperty(JsonObject value, string propertyName)
    {
        if (TryGetProperty(value, propertyName, out var actualName, out _))
        {
            value.Remove(actualName);
        }
    }

    private static IEnumerable<string> ReadRequiredNames(JsonArray? required)
        => required?.Select(static item => item!.GetValue<string>()) ?? [];

    private static bool ContainsName(IEnumerable<string> names, string candidate)
        => names.Any(name => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase));
}

internal enum McpToolArgumentSchemaMode
{
    Flat,
    Envelope,
}

internal sealed record AugmentedMcpToolSchema(McpToolArgumentSchemaMode Mode, JsonElement Schema);

internal sealed record McpToolArgumentPresentation(string ForwardedArgumentsJson, string? ExpectedLimitText);
