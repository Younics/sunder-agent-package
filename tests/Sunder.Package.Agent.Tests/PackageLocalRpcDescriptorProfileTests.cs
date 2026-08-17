using System.Text.Json;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class PackageLocalRpcDescriptorProfileTests
{
    public static TheoryData<string> DescriptorPaths => new()
    {
        "src/Sunder.Package.Agent.Builder/Protocol/Contracts/builder-runtime.rpc.json",
        "src/Sunder.Package.Agent.Mcp/Shared/Contracts/mcp-management.rpc.json",
        "src/Sunder.Package.Agent.Memory.Semantic/Protocol/Contracts/memory-inspector.rpc.json",
        "src/Sunder.Package.Agent.Execution.Local/Protocol/Contracts/local-execution-presentation.rpc.json",
        "src/Sunder.Package.Agent.Execution.Docker/Protocol/Contracts/docker-execution-presentation.rpc.json",
        "src/Sunder.Package.Agent.Provider.OpenAI/Shared/Contracts/openai-management.rpc.json",
        "src/Sunder.Package.Agent.Provider.Gemini/Protocol/Contracts/credential-settings.rpc.json",
        "src/Sunder.Package.Agent.Provider.Anthropic/Protocol/Contracts/credential-settings.rpc.json",
        "src/Sunder.Package.Agent.Provider.LMStudio/Protocol/Contracts/credential-settings.rpc.json",
        "src/Sunder.Package.Agent.Skills/Protocol/Contracts/skills-owner.rpc.json",
        "src/Sunder.Package.Agent.Subagents/Protocol/Contracts/subagents-owner.rpc.json",
    };

    [Theory]
    [MemberData(nameof(DescriptorPaths))]
    public void Descriptor_UsesStrictSemanticValueProfile(string relativePath)
    {
        var path = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, relativePath);
        var bytes = File.ReadAllBytes(path);
        var descriptor = SunderRpcContractDescriptor.Parse(bytes);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;

        Assert.Equal(SunderRpcProtocol.ValueSchemaProfile, descriptor.SchemaProfile);
        Assert.Equal("2.0.0", descriptor.Version);
        Assert.Equal(SunderRpcProtocol.ValueSchemaProfile, root.GetProperty("schemaProfile").GetString());

        var definitions = root.GetProperty("$defs");
        foreach (var definition in definitions.EnumerateObject())
        {
            Assert.DoesNotMatch("^(String|Boolean|Int32|Int64|SunderRpc.*|Nullable.*|.*List)$", definition.Name);
            InspectSchema(definition.Value, relativePath);
        }

        Assert.Equal(
            definitions.EnumerateObject().Select(static definition => definition.Name).Order().ToArray(),
            ReachableDefinitions(root).Order().ToArray());

        if (relativePath.Contains("Mcp", StringComparison.Ordinal))
        {
            Assert.DoesNotContain("\"chunks\"", File.ReadAllText(path), StringComparison.Ordinal);
        }
    }

    private static void InspectSchema(JsonElement value, string relativePath)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("logicalType", out var logicalType))
            {
                Assert.Equal("content-reference", logicalType.GetString());
            }
            if (value.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "number")
            {
                Assert.True(
                    value.TryGetProperty("format", out var format)
                    && format.GetString() is "float32" or "float64",
                    $"Bare number schema in {relativePath}: {value.GetRawText()}");
            }
            if (value.TryGetProperty("enum", out var enumValues))
            {
                Assert.All(enumValues.EnumerateArray(), static item =>
                    Assert.Equal(JsonValueKind.String, item.ValueKind));
            }
            foreach (var limitName in new[] { "maxLength", "maxItems", "maxProperties" })
            {
                if (value.TryGetProperty(limitName, out var limit))
                {
                    Assert.NotEqual(1_000_000, limit.GetInt32());
                }
            }
            foreach (var property in value.EnumerateObject())
            {
                InspectSchema(property.Value, relativePath);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                InspectSchema(item, relativePath);
            }
        }
    }

    private static HashSet<string> ReachableDefinitions(JsonElement root)
    {
        var definitions = root.GetProperty("$defs");
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        foreach (var service in root.GetProperty("services").EnumerateObject())
        {
            foreach (var method in service.Value.GetProperty("methods").EnumerateObject())
            {
                AddReferences(method.Value, pending);
            }
        }

        while (pending.TryDequeue(out var name))
        {
            Assert.True(definitions.TryGetProperty(name, out var definition), $"Missing definition '{name}'.");
            if (reachable.Add(name))
            {
                AddReferences(definition, pending);
            }
        }
        return reachable;
    }

    private static void AddReferences(JsonElement value, Queue<string> pending)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("$ref", out var reference))
            {
                const string prefix = "#/$defs/";
                var text = reference.GetString();
                Assert.StartsWith(prefix, text, StringComparison.Ordinal);
                pending.Enqueue(text![prefix.Length..]);
            }
            foreach (var property in value.EnumerateObject())
            {
                AddReferences(property.Value, pending);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                AddReferences(item, pending);
            }
        }
    }
}
