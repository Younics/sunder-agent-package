using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sunder.Package.Agent.Provider.OpenAI.Transport;

internal static class OpenAiStrictToolSchemaNormalizer
{
    private const string SunderMcpArgumentsSchemaId = "urn:sunder:mcp:arguments:1";
    private const string SunderMcpArgumentsSchemaMarker = "sunder:mcp-arguments:1";

    internal static JsonElement NormalizeFunctionParameters(string? schemaJson)
    {
        JsonNode? schemaNode = string.IsNullOrWhiteSpace(schemaJson)
            ? CreateEmptyObjectSchemaNode()
            : JsonNode.Parse(schemaJson);

        if (schemaNode is not JsonObject schemaObject)
        {
            return SerializeEmptyObjectSchema();
        }

        var resolvedRoot = ResolveLocalRefs(
            schemaObject,
            schemaObject,
            new HashSet<(JsonObject ResourceRoot, string RefPath)>());
        if (resolvedRoot is not JsonObject resolvedSchemaObject)
        {
            return SerializeEmptyObjectSchema();
        }

        NormalizeSchemaNode(resolvedSchemaObject);
        return JsonSerializer.SerializeToElement(resolvedSchemaObject);
    }

    private static JsonNode ResolveLocalRefs(
        JsonNode node,
        JsonObject resourceRoot,
        HashSet<(JsonObject ResourceRoot, string RefPath)> activeRefs)
    {
        return node switch
        {
            JsonObject obj => ResolveLocalRefsInObject(obj, resourceRoot, activeRefs),
            JsonArray array => ResolveLocalRefsInArray(array, resourceRoot, activeRefs),
            _ => node.DeepClone(),
        };
    }

    private static JsonNode ResolveLocalRefsInObject(
        JsonObject schema,
        JsonObject resourceRoot,
        HashSet<(JsonObject ResourceRoot, string RefPath)> activeRefs)
    {
        if (schema.ContainsKey("$id"))
        {
            resourceRoot = schema;
        }
        ValidateReference(schema, "$ref");
        ValidateReference(schema, "$dynamicRef");
        ValidateReference(schema, "$recursiveRef");
        if (schema.TryGetPropertyValue("$ref", out var refNode)
            && refNode is JsonValue refValue
            && refValue.TryGetValue<string>(out var refPath)
            && IsLocalPointerRef(refPath))
        {
            var refKey = (resourceRoot, refPath);
            if (!activeRefs.Add(refKey))
            {
                throw new InvalidOperationException($"Cyclic local schema reference '{refPath}' is not supported.");
            }

            try
            {
                var resolvedTarget = ResolveLocalRefTarget(resourceRoot, refPath)
                    ?? throw new InvalidOperationException($"Local schema reference '{refPath}' could not be resolved.");
                var resolvedNode = ResolveLocalRefs(resolvedTarget, resourceRoot, activeRefs);

                var assertionSiblings = new JsonObject();
                var resourceSiblings = new JsonObject();
                foreach (var property in schema)
                {
                    if (string.Equals(property.Key, "$ref", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var resolvedSibling = property.Value is null
                        ? null
                        : ResolveLocalRefs(property.Value, resourceRoot, activeRefs);
                    (IsResourceSibling(property.Key) ? resourceSiblings : assertionSiblings)[property.Key] = resolvedSibling;
                }
                return ComposeResolvedReference(resolvedNode, assertionSiblings, resourceSiblings);
            }
            finally
            {
                activeRefs.Remove(refKey);
            }
        }

        var clone = new JsonObject();
        foreach (var property in schema)
        {
            clone[property.Key] = property.Value is null
                ? null
                : ResolveLocalRefs(property.Value, resourceRoot, activeRefs);
        }

        return clone;
    }

    private static JsonArray ResolveLocalRefsInArray(
        JsonArray schemaArray,
        JsonObject resourceRoot,
        HashSet<(JsonObject ResourceRoot, string RefPath)> activeRefs)
    {
        var clone = new JsonArray();
        foreach (var item in schemaArray)
        {
            clone.Add(item is null ? null : ResolveLocalRefs(item, resourceRoot, activeRefs));
        }

        return clone;
    }

    private static void NormalizeSchemaNode(JsonNode? node, bool preserveObjectShape = false)
    {
        switch (node)
        {
            case JsonObject obj:
                NormalizeSchemaObject(obj, preserveObjectShape);
                break;

            case JsonArray array:
                foreach (var child in array)
                {
                    NormalizeSchemaNode(child, preserveObjectShape);
                }
                break;
        }
    }

    private static void NormalizeSchemaObject(JsonObject schema, bool preserveObjectShape)
    {
        preserveObjectShape |= IsSunderMcpArgumentsSchema(schema);
        var hasUnresolvedReference = ContainsUnresolvedReference(schema);
        if (!hasUnresolvedReference)
        {
            schema.Remove("$defs");
            schema.Remove("definitions");
            schema.Remove("$schema");
            schema.Remove("$id");
        }
        if (preserveObjectShape
            && schema["$comment"] is JsonValue comment
            && comment.TryGetValue<string>(out var commentValue)
            && string.Equals(commentValue, SunderMcpArgumentsSchemaMarker, StringComparison.Ordinal))
        {
            schema.Remove("$comment");
        }

        NormalizeSchemaNode(schema["items"], preserveObjectShape);
        NormalizeSchemaNode(schema["contains"], preserveObjectShape);
        NormalizeSchemaNode(schema["propertyNames"], preserveObjectShape);
        NormalizeSchemaNode(schema["not"], preserveObjectShape);
        NormalizeSchemaNode(schema["if"], preserveObjectShape);
        NormalizeSchemaNode(schema["then"], preserveObjectShape);
        NormalizeSchemaNode(schema["else"], preserveObjectShape);
        NormalizeSchemaArray(schema["prefixItems"] as JsonArray, preserveObjectShape);
        NormalizeSchemaArray(schema["anyOf"] as JsonArray, preserveObjectShape);
        NormalizeSchemaArray(schema["oneOf"] as JsonArray, preserveObjectShape);
        NormalizeSchemaArray(schema["allOf"] as JsonArray, preserveObjectShape);
        if (preserveObjectShape)
        {
            NormalizeSchemaMap(schema["properties"] as JsonObject, preserveObjectShape);
            NormalizeSchemaMap(schema["patternProperties"] as JsonObject, preserveObjectShape);
            NormalizeSchemaMap(schema["dependentSchemas"] as JsonObject, preserveObjectShape);
            NormalizeSchemaNode(schema["additionalProperties"], preserveObjectShape);
            NormalizeSchemaNode(schema["unevaluatedProperties"], preserveObjectShape);
            return;
        }

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

    private static void NormalizeSchemaArray(JsonArray? schemaArray, bool preserveObjectShape = false)
    {
        if (schemaArray is null)
        {
            return;
        }

        foreach (var child in schemaArray)
        {
            NormalizeSchemaNode(child, preserveObjectShape);
        }
    }

    private static void NormalizeSchemaMap(JsonObject? schemas, bool preserveObjectShape)
    {
        if (schemas is null)
        {
            return;
        }
        foreach (var schema in schemas)
        {
            NormalizeSchemaNode(schema.Value, preserveObjectShape);
        }
    }

    private static bool IsSunderMcpArgumentsSchema(JsonObject schema)
        => schema["$id"] is JsonValue id
               && id.TryGetValue<string>(out var idValue)
               && string.Equals(idValue, SunderMcpArgumentsSchemaId, StringComparison.Ordinal)
           || schema["$comment"] is JsonValue comment
               && comment.TryGetValue<string>(out var commentValue)
               && string.Equals(commentValue, SunderMcpArgumentsSchemaMarker, StringComparison.Ordinal);

    private static JsonNode ComposeResolvedReference(
        JsonNode resolvedTarget,
        JsonObject assertionSiblings,
        JsonObject resourceSiblings)
    {
        if (assertionSiblings.Count == 0
            && resolvedTarget is JsonObject resolvedObject
            && resourceSiblings.All(sibling => !resolvedObject.ContainsKey(sibling.Key)))
        {
            var merged = (JsonObject)resolvedObject.DeepClone();
            foreach (var sibling in resourceSiblings)
            {
                merged[sibling.Key] = sibling.Value?.DeepClone();
            }
            return merged;
        }

        var composition = new JsonObject();
        foreach (var sibling in resourceSiblings)
        {
            composition[sibling.Key] = sibling.Value?.DeepClone();
        }
        var branches = new JsonArray(resolvedTarget.DeepClone());
        if (assertionSiblings.Count > 0)
        {
            branches.Add(assertionSiblings.DeepClone());
        }
        composition["allOf"] = branches;
        return composition;
    }

    private static bool IsResourceSibling(string propertyName)
        => propertyName is "$schema" or "$id" or "$anchor" or "$dynamicAnchor" or "$recursiveAnchor"
            or "$vocabulary" or "$defs" or "definitions" or "$comment";

    private static void ValidateReference(JsonObject schema, string keyword)
    {
        if (schema[keyword] is not JsonValue reference
            || !reference.TryGetValue<string>(out var referenceValue))
        {
            return;
        }
        if (!referenceValue.StartsWith('#'))
        {
            throw new InvalidOperationException(
                $"External JSON Schema reference '{referenceValue}' in '{keyword}' is not supported for OpenAI tool normalization.");
        }
        if (string.Equals(keyword, "$ref", StringComparison.Ordinal)
            && !IsLocalPointerRef(referenceValue))
        {
            throw new InvalidOperationException(
                $"Local JSON Schema reference '{referenceValue}' must use a JSON Pointer for OpenAI tool normalization.");
        }
    }

    private static bool ContainsUnresolvedReference(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (property.Key is "$ref" or "$dynamicRef" or "$recursiveRef"
                    || ContainsUnresolvedReference(property.Value))
                {
                    return true;
                }
            }
        }
        else if (node is JsonArray array)
        {
            return array.Any(ContainsUnresolvedReference);
        }
        return false;
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

    private static bool IsLocalPointerRef(string refPath) => refPath.StartsWith("#/", StringComparison.Ordinal);

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
