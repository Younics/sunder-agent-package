using System.Text.Json;
using Sunder.Package.Agent.Provider.OpenAI.Transport;
using Xunit;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

public sealed class OpenAiStrictToolSchemaNormalizerTests
{
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
