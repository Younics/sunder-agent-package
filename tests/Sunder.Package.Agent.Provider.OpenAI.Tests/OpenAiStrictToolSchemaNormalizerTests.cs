using System.Text.Json;
using Sunder.Package.Agent.Provider.OpenAI.Transport;
using Xunit;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

public sealed class OpenAiStrictToolSchemaNormalizerTests
{
    [Fact]
    public void NonStrictToolsKeepOptionalFiltersOptionalWithoutNullBoilerplate()
    {
        const string schema = """{"type":"object","properties":{"package":{"type":"string"},"source":{"type":"string","enum":["runtime","app"]},"limit":{"type":"integer","default":10}},"required":["package"],"additionalProperties":false}""";
        var normalized = OpenAiStrictToolSchemaNormalizer.NormalizeFunctionParameters(schema, strict: false);
        Assert.Equal(new[] { "package" }, normalized.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("string", normalized.GetProperty("properties").GetProperty("source").GetProperty("type").GetString());
        Assert.False(normalized.GetProperty("properties").GetProperty("source").TryGetProperty("anyOf", out _));
    }
    [Theory]
    [InlineData("{\"enum\":[\"runtime\",\"app\"]}")]
    [InlineData("{\"type\":\"string\",\"enum\":[\"runtime\",\"app\"]}")]
    [InlineData("{\"type\":\"string\",\"const\":\"runtime\"}")]
    public void OptionalConstrainedValuesUseANullBranchWithoutWeakeningTheOriginal(string property)
    {
        var normalized = OpenAiStrictToolSchemaNormalizer.NormalizeFunctionParameters("{\"type\":\"object\",\"properties\":{\"source\":" + property + "}}");
        var branches = normalized.GetProperty("properties").GetProperty("source").GetProperty("anyOf");
        Assert.Equal(2, branches.GetArrayLength());
        Assert.Equal("null", branches[1].GetProperty("type").GetString());
        using var original = JsonDocument.Parse(property);
        Assert.Equal(original.RootElement.GetRawText(), branches[0].GetRawText());
    }
    [Fact]
    public void NormalizeFunctionParameters_EmptySchema_ReturnsStrictEmptyObject()
    {
        var schema = OpenAiStrictToolSchemaNormalizer.NormalizeFunctionParameters(null);

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Object, schema.GetProperty("properties").ValueKind);
        Assert.Empty(schema.GetProperty("properties").EnumerateObject());
        Assert.Equal(JsonValueKind.Array, schema.GetProperty("required").ValueKind);
        Assert.Empty(schema.GetProperty("required").EnumerateArray());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void NormalizeFunctionParameters_ResolvesRootLocalRef_AndMakesRootStrict()
    {
        const string schemaJson = """
            {
              "$ref": "#/$defs/designSystem",
              "$defs": {
                "designSystem": {
                  "type": "object",
                  "properties": {
                    "displayName": { "type": "string" },
                    "theme": {
                      "type": "object",
                      "properties": {
                        "customColor": { "type": "string" }
                      }
                    }
                  }
                }
              }
            }
            """;

        var schema = OpenAiStrictToolSchemaNormalizer.NormalizeFunctionParameters(schemaJson);

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.DoesNotContain(schema.EnumerateObject(), property => property.Name is "$defs" or "definitions" or "$ref");

        var required = schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Equal(["displayName", "theme"], required);

        var properties = schema.GetProperty("properties");
        var displayNameTypes = ReadTypeValues(properties.GetProperty("displayName"));
        Assert.Contains("string", displayNameTypes);
        Assert.Contains("null", displayNameTypes);

        var theme = properties.GetProperty("theme");
        Assert.False(theme.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["customColor"], theme.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).ToArray());
    }

    [Fact]
    public void NormalizeFunctionParameters_ObjectWithoutProperties_BecomesStrictEmptyObject()
    {
        const string schemaJson = """
            {
              "type": "object"
            }
            """;

        var schema = OpenAiStrictToolSchemaNormalizer.NormalizeFunctionParameters(schemaJson);

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Object, schema.GetProperty("properties").ValueKind);
        Assert.Empty(schema.GetProperty("properties").EnumerateObject());
        Assert.Empty(schema.GetProperty("required").EnumerateArray());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void NormalizeFunctionParameters_CyclicLocalRef_ThrowsDeterministicError()
    {
        const string schemaJson = """
            {
              "$ref": "#/$defs/root",
              "$defs": {
                "root": {
                  "$ref": "#/$defs/root"
                }
              }
            }
            """;

        var exception = Assert.Throws<InvalidOperationException>(() => OpenAiStrictToolSchemaNormalizer.NormalizeFunctionParameters(schemaJson));
        Assert.Contains("Cyclic local schema reference", exception.Message);
    }

    [Fact]
    public void NormalizeFunctionParameters_McpEnvelopeResolvesNestedRootRefAndKeepsNullableTimeout()
    {
        const string schemaJson = """
            {
              "type": "object",
              "properties": {
                "__younics_mcp_call": {
                  "type": "object",
                  "properties": {
                    "__younics_mcp_metadata_version": { "type": "string", "const": "younics.ai.agent.mcp.call/1" },
                    "arguments": {
                      "$id": "urn:younics:ai:agent:mcp:arguments:1",
                      "$ref": "#/$defs/args",
                      "$defs": {
                        "args": {
                          "type": "object",
                          "properties": { "prompt": { "type": "string" } },
                          "additionalProperties": false
                        }
                      }
                    },
                    "__younics_expected_timeout_seconds": {
                      "type": ["integer", "null"],
                      "minimum": 1,
                      "maximum": 1800
                    }
                  },
                  "required": ["__younics_mcp_metadata_version", "arguments"],
                  "additionalProperties": false
                }
              },
              "required": ["__younics_mcp_call"],
              "additionalProperties": false
            }
            """;

        var schema = OpenAiStrictToolSchemaNormalizer.NormalizeFunctionParameters(schemaJson);

        var envelope = schema.GetProperty("properties").GetProperty("__younics_mcp_call");
        Assert.Equal(
            ["__younics_mcp_metadata_version", "arguments", "__younics_expected_timeout_seconds"],
            envelope.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            ["integer", "null"],
            envelope.GetProperty("properties")
                .GetProperty("__younics_expected_timeout_seconds")
                .GetProperty("type")
                .EnumerateArray()
                .Select(item => item.GetString()));
        var arguments = envelope.GetProperty("properties").GetProperty("arguments");
        Assert.True(arguments.GetProperty("properties").TryGetProperty("prompt", out _));
        Assert.False(arguments.GetProperty("additionalProperties").GetBoolean());
        Assert.False(arguments.TryGetProperty("$ref", out _));
        Assert.False(arguments.TryGetProperty("$defs", out _));
        Assert.False(arguments.TryGetProperty("$id", out _));
    }

    [Fact]
    public void NormalizeFunctionParameters_McpEnvelopePreservesClosedCombinatorsAndPatternArguments()
    {
        const string schemaJson = """
            {
              "type": "object",
              "properties": {
                "__younics_mcp_call": {
                  "type": "object",
                  "properties": {
                    "__younics_mcp_metadata_version": { "type": "string", "const": "younics.ai.agent.mcp.call/1" },
                    "arguments": {
                      "$id": "urn:younics:ai:agent:mcp:arguments:1",
                      "oneOf": [
                        { "type": "object", "properties": { "a": { "type": "string" } }, "additionalProperties": false },
                        { "allOf": [
                          { "type": "object", "properties": { "b": { "type": "integer" } } },
                          { "type": "object", "patternProperties": { "^x-": { "type": "string" } } }
                        ], "unevaluatedProperties": false }
                      ]
                    }
                  },
                  "required": ["__younics_mcp_metadata_version", "arguments"],
                  "additionalProperties": false
                }
              },
              "required": ["__younics_mcp_call"],
              "additionalProperties": false
            }
            """;

        var schema = OpenAiStrictToolSchemaNormalizer.NormalizeFunctionParameters(schemaJson);
        var arguments = schema.GetProperty("properties")
            .GetProperty("__younics_mcp_call")
            .GetProperty("properties")
            .GetProperty("arguments");

        Assert.Equal(2, arguments.GetProperty("oneOf").GetArrayLength());
        var secondBranch = arguments.GetProperty("oneOf")[1];
        Assert.Equal(2, secondBranch.GetProperty("allOf").GetArrayLength());
        Assert.True(secondBranch.GetProperty("allOf")[1].TryGetProperty("patternProperties", out _));
        Assert.False(secondBranch.GetProperty("unevaluatedProperties").GetBoolean());
    }

    [Fact]
    public void NormalizeFunctionParameters_McpEnvelopeResolvesNestedExistingSchemaResource()
    {
        const string schemaJson = """
            {
              "type": "object",
              "properties": {
                "__younics_mcp_call": {
                  "type": "object",
                  "properties": {
                    "__younics_mcp_metadata_version": { "type": "string", "const": "younics.ai.agent.mcp.call/1" },
                    "arguments": {
                      "$comment": "younics:ai:agent:mcp-arguments:1",
                      "allOf": [{
                        "$id": "https://example.test/arguments",
                        "$ref": "#/$defs/args",
                        "$defs": {
                          "args": { "type": "object", "properties": { "path": { "type": "string" } } }
                        }
                      }]
                    }
                  },
                  "required": ["__younics_mcp_metadata_version", "arguments"],
                  "additionalProperties": false
                }
              },
              "required": ["__younics_mcp_call"],
              "additionalProperties": false
            }
            """;

        var schema = OpenAiStrictToolSchemaNormalizer.NormalizeFunctionParameters(schemaJson);
        var originalResource = schema.GetProperty("properties")
            .GetProperty("__younics_mcp_call")
            .GetProperty("properties")
            .GetProperty("arguments")
            .GetProperty("allOf")[0];

        Assert.True(originalResource.GetProperty("properties").TryGetProperty("path", out _));
        Assert.False(originalResource.TryGetProperty("$ref", out _));
        Assert.False(originalResource.TryGetProperty("$defs", out _));
        Assert.False(originalResource.TryGetProperty("$id", out _));
        Assert.False(schema.GetProperty("properties")
            .GetProperty("__younics_mcp_call")
            .GetProperty("properties")
            .GetProperty("arguments")
            .TryGetProperty("$comment", out _));
    }

    [Fact]
    public void NormalizeFunctionParameters_McpEnvelopeComposesRefSiblingsWithoutOverwritingReferencedConstraints()
    {
        const string schemaJson = """
            {
              "type": "object",
              "properties": {
                "__younics_mcp_call": {
                  "type": "object",
                  "properties": {
                    "__younics_mcp_metadata_version": { "type": "string", "const": "younics.ai.agent.mcp.call/1" },
                    "arguments": {
                      "$id": "urn:younics:ai:agent:mcp:arguments:1",
                      "$ref": "#/$defs/base",
                      "$defs": {
                        "base": {
                          "type": "object",
                          "properties": { "real": { "type": "string" } },
                          "required": ["real"],
                          "additionalProperties": false
                        }
                      },
                      "properties": {
                        "real": { "type": "string", "minLength": 3 },
                        "sibling": { "type": "integer", "minimum": 1 }
                      },
                      "required": ["sibling"],
                      "minProperties": 2,
                      "additionalProperties": true
                    },
                    "__younics_expected_timeout_seconds": { "type": ["integer", "null"], "minimum": 1, "maximum": 1800 }
                  },
                  "required": ["__younics_mcp_metadata_version", "arguments"],
                  "additionalProperties": false
                }
              },
              "required": ["__younics_mcp_call"],
              "additionalProperties": false
            }
            """;

        var schema = OpenAiStrictToolSchemaNormalizer.NormalizeFunctionParameters(schemaJson);
        var envelope = schema.GetProperty("properties").GetProperty("__younics_mcp_call");
        var arguments = envelope.GetProperty("properties").GetProperty("arguments");
        var branches = arguments.GetProperty("allOf");
        Assert.Equal(2, branches.GetArrayLength());

        var referenced = branches[0];
        Assert.True(referenced.GetProperty("properties").TryGetProperty("real", out _));
        Assert.Equal(["real"], referenced.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.False(referenced.GetProperty("additionalProperties").GetBoolean());

        var siblings = branches[1];
        Assert.True(siblings.GetProperty("properties").TryGetProperty("real", out var constrainedReal));
        Assert.Equal(3, constrainedReal.GetProperty("minLength").GetInt32());
        Assert.True(siblings.GetProperty("properties").TryGetProperty("sibling", out _));
        Assert.Equal(["sibling"], siblings.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(2, siblings.GetProperty("minProperties").GetInt32());
        Assert.True(siblings.GetProperty("additionalProperties").GetBoolean());

        Assert.Equal(
            ["integer", "null"],
            envelope.GetProperty("properties")
                .GetProperty("__younics_expected_timeout_seconds")
                .GetProperty("type")
                .EnumerateArray()
                .Select(item => item.GetString()));
    }

    [Fact]
    public void NormalizeFunctionParameters_McpEnvelopeRetainsDefinitionsNeededByUnresolvedDynamicRef()
    {
        const string schemaJson = """
            {
              "type": "object",
              "properties": {
                "__younics_mcp_call": {
                  "type": "object",
                  "properties": {
                    "__younics_mcp_metadata_version": { "type": "string", "const": "younics.ai.agent.mcp.call/1" },
                    "arguments": {
                      "$id": "urn:younics:ai:agent:mcp:arguments:1",
                      "$dynamicRef": "#node",
                      "$defs": {
                        "node": {
                          "$dynamicAnchor": "node",
                          "type": "object",
                          "properties": { "value": { "type": "string" } }
                        }
                      }
                    }
                  },
                  "required": ["__younics_mcp_metadata_version", "arguments"],
                  "additionalProperties": false
                }
              },
              "required": ["__younics_mcp_call"],
              "additionalProperties": false
            }
            """;

        var schema = OpenAiStrictToolSchemaNormalizer.NormalizeFunctionParameters(schemaJson);
        var arguments = schema.GetProperty("properties")
            .GetProperty("__younics_mcp_call")
            .GetProperty("properties")
            .GetProperty("arguments");

        Assert.Equal("#node", arguments.GetProperty("$dynamicRef").GetString());
        Assert.Equal("urn:younics:ai:agent:mcp:arguments:1", arguments.GetProperty("$id").GetString());
        Assert.Equal("node", arguments.GetProperty("$defs").GetProperty("node").GetProperty("$dynamicAnchor").GetString());
    }

    [Fact]
    public void NormalizeFunctionParameters_ExternalReferenceFailsClearly()
    {
        const string schemaJson = """
            {
              "type": "object",
              "properties": {
                "arguments": { "$ref": "https://example.test/schemas/arguments.json" }
              }
            }
            """;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            OpenAiStrictToolSchemaNormalizer.NormalizeFunctionParameters(schemaJson));

        Assert.Contains("External JSON Schema reference", exception.Message, StringComparison.Ordinal);
        Assert.Contains("https://example.test/schemas/arguments.json", exception.Message, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> ReadTypeValues(JsonElement schema)
    {
        var typeElement = schema.GetProperty("type");
        return typeElement.ValueKind switch
        {
            JsonValueKind.String => [typeElement.GetString()!],
            JsonValueKind.Array => typeElement.EnumerateArray().Select(item => item.GetString()!).ToArray(),
            _ => []
        };
    }
}
