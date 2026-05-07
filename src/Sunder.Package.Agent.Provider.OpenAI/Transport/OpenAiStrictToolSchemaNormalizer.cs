using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sunder.Package.Agent.Provider.OpenAI.Transport;

internal static class OpenAiStrictToolSchemaNormalizer
{
    internal static JsonElement NormalizeFunctionParameters(string? schemaJson)
    {
        JsonNode? schemaNode = string.IsNullOrWhiteSpace(schemaJson)
            ? CreateEmptyObjectSchemaNode()
            : JsonNode.Parse(schemaJson);

        if (schemaNode is not JsonObject schemaObject)
        {
            return SerializeEmptyObjectSchema();
        }

        var resolvedRoot = ResolveLocalRefs(schemaObject, schemaObject, new HashSet<string>(StringComparer.Ordinal));
        if (resolvedRoot is not JsonObject resolvedSchemaObject)
        {
            return SerializeEmptyObjectSchema();
        }

        NormalizeSchemaNode(resolvedSchemaObject);
        return JsonSerializer.SerializeToElement(resolvedSchemaObject);
    }

    private static JsonNode ResolveLocalRefs(JsonNode node, JsonObject documentRoot, HashSet<string> activeRefs)
    {
        return node switch
        {
            JsonObject obj => ResolveLocalRefsInObject(obj, documentRoot, activeRefs),
            JsonArray array => ResolveLocalRefsInArray(array, documentRoot, activeRefs),
            _ => node.DeepClone(),
        };
    }

    private static JsonNode ResolveLocalRefsInObject(JsonObject schema, JsonObject documentRoot, HashSet<string> activeRefs)
    {
        if (schema.TryGetPropertyValue("$ref", out var refNode)
            && refNode is JsonValue refValue
            && refValue.TryGetValue<string>(out var refPath)
            && IsLocalRef(refPath))
        {
            if (!activeRefs.Add(refPath))
            {
                throw new InvalidOperationException($"Cyclic local schema reference '{refPath}' is not supported.");
            }

            try
            {
                var resolvedTarget = ResolveLocalRefTarget(documentRoot, refPath)
                    ?? throw new InvalidOperationException($"Local schema reference '{refPath}' could not be resolved.");
                var resolvedNode = ResolveLocalRefs(resolvedTarget, documentRoot, activeRefs);

                if (resolvedNode is not JsonObject resolvedObject)
                {
                    return resolvedNode;
                }

                var merged = (JsonObject)resolvedObject.DeepClone();
                foreach (var property in schema)
                {
                    if (string.Equals(property.Key, "$ref", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    merged[property.Key] = property.Value is null
                        ? null
                        : ResolveLocalRefs(property.Value, documentRoot, activeRefs);
                }

                return merged;
            }
            finally
            {
                activeRefs.Remove(refPath);
            }
        }

        var clone = new JsonObject();
        foreach (var property in schema)
        {
            clone[property.Key] = property.Value is null
                ? null
                : ResolveLocalRefs(property.Value, documentRoot, activeRefs);
        }

        return clone;
    }

    private static JsonArray ResolveLocalRefsInArray(JsonArray schemaArray, JsonObject documentRoot, HashSet<string> activeRefs)
    {
        var clone = new JsonArray();
        foreach (var item in schemaArray)
        {
            clone.Add(item is null ? null : ResolveLocalRefs(item, documentRoot, activeRefs));
        }

        return clone;
    }

    private static void NormalizeSchemaNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                NormalizeSchemaObject(obj);
                break;

            case JsonArray array:
                foreach (var child in array)
                {
                    NormalizeSchemaNode(child);
                }
                break;
        }
    }

    private static void NormalizeSchemaObject(JsonObject schema)
    {
        schema.Remove("$defs");
        schema.Remove("definitions");
        schema.Remove("$schema");

        NormalizeSchemaNode(schema["items"]);
        NormalizeSchemaNode(schema["contains"]);
        NormalizeSchemaNode(schema["propertyNames"]);
        NormalizeSchemaNode(schema["not"]);
        NormalizeSchemaNode(schema["if"]);
        NormalizeSchemaNode(schema["then"]);
        NormalizeSchemaNode(schema["else"]);
        NormalizeSchemaArray(schema["prefixItems"] as JsonArray);
        NormalizeSchemaArray(schema["anyOf"] as JsonArray);
        NormalizeSchemaArray(schema["oneOf"] as JsonArray);
        NormalizeSchemaArray(schema["allOf"] as JsonArray);

        if (!IsObjectLikeSchema(schema))
        {
            return;
        }

        var properties = schema["properties"] as JsonObject ?? new JsonObject();
        schema["properties"] = properties;

        var originalRequired = (schema["required"] as JsonArray)
            ?.Select(node => node?.GetValue<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var propertyNames = properties.Select(property => property.Key).ToArray();
        foreach (var propertyName in propertyNames)
        {
            NormalizeSchemaNode(properties[propertyName]);

            if (!originalRequired.Contains(propertyName) && properties[propertyName] is JsonObject propertySchema)
            {
                MakePropertyNullable(propertySchema);
            }
        }

        schema["type"] = EnsureTypeContains(schema["type"], "object", includeNull: false);
        schema["required"] = new JsonArray(propertyNames.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
        schema["additionalProperties"] = JsonValue.Create(false);
    }

    private static void NormalizeSchemaArray(JsonArray? schemaArray)
    {
        if (schemaArray is null)
        {
            return;
        }

        foreach (var child in schemaArray)
        {
            NormalizeSchemaNode(child);
        }
    }

    private static void MakePropertyNullable(JsonObject propertySchema)
    {
        propertySchema["type"] = EnsureTypeContains(propertySchema["type"], "null", includeNull: true);
    }

    private static JsonNode EnsureTypeContains(JsonNode? existingType, string requiredType, bool includeNull)
    {
        var types = new List<string>();
        switch (existingType)
        {
            case JsonValue value when value.TryGetValue<string>(out var singleType) && !string.IsNullOrWhiteSpace(singleType):
                types.Add(singleType);
                break;

            case JsonArray array:
                foreach (var node in array)
                {
                    if (node is JsonValue item && item.TryGetValue<string>(out var arrayType) && !string.IsNullOrWhiteSpace(arrayType))
                    {
                        types.Add(arrayType);
                    }
                }
                break;
        }

        if (!types.Contains(requiredType, StringComparer.OrdinalIgnoreCase))
        {
            types.Insert(0, requiredType);
        }

        if (includeNull && !types.Contains("null", StringComparer.OrdinalIgnoreCase))
        {
            types.Add("null");
        }

        return types.Count switch
        {
            0 => JsonValue.Create(requiredType),
            1 => JsonValue.Create(types[0]),
            _ => new JsonArray(types.Select(type => (JsonNode?)JsonValue.Create(type)).ToArray()),
        };
    }

    private static bool IsObjectLikeSchema(JsonObject schema)
    {
        return schema["properties"] is JsonObject
            || schema.ContainsKey("required")
            || schema.ContainsKey("additionalProperties")
            || schema.ContainsKey("patternProperties")
            || TypeContains(schema["type"], "object");
    }

    private static bool TypeContains(JsonNode? typeNode, string expectedType)
    {
        return typeNode switch
        {
            JsonValue value when value.TryGetValue<string>(out var singleType)
                => string.Equals(singleType, expectedType, StringComparison.OrdinalIgnoreCase),
            JsonArray array => array.Any(node => node is JsonValue item
                                                 && item.TryGetValue<string>(out var itemType)
                                                 && string.Equals(itemType, expectedType, StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    private static JsonNode? ResolveLocalRefTarget(JsonObject documentRoot, string refPath)
    {
        if (!refPath.StartsWith("#/", StringComparison.Ordinal))
        {
            return null;
        }

        JsonNode? current = documentRoot;
        foreach (var rawSegment in refPath[2..].Split('/', StringSplitOptions.None))
        {
            var segment = rawSegment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            current = current switch
            {
                JsonObject obj when obj.TryGetPropertyValue(segment, out var propertyValue) => propertyValue,
                JsonArray array when int.TryParse(segment, out var index) && index >= 0 && index < array.Count => array[index],
                _ => null,
            };

            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static bool IsLocalRef(string refPath) => refPath.StartsWith("#/", StringComparison.Ordinal);

    private static JsonObject CreateEmptyObjectSchemaNode()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
            ["required"] = new JsonArray(),
            ["additionalProperties"] = false,
        };

    private static JsonElement SerializeEmptyObjectSchema() => JsonSerializer.SerializeToElement(CreateEmptyObjectSchemaNode());
}
