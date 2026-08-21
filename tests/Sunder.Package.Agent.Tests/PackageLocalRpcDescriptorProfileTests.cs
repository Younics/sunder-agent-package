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
        Assert.Equal("1.0.0", descriptor.Version);
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

    [Fact]
    public void DescriptorInventory_UsesUnreleasedVersionsAndOnlySemanticFiniteBounds()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var paths = Directory.EnumerateFiles(sourceRoot, "*.rpc.json", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var retainedMaximums = new List<string>();

        Assert.Equal(31, paths.Length);
        foreach (var path in paths)
        {
            var bytes = File.ReadAllBytes(path);
            var descriptor = SunderRpcContractDescriptor.Parse(bytes);
            using var document = JsonDocument.Parse(bytes);

            Assert.Equal("1.0.0", descriptor.Version);
            InspectFiniteConstraints(
                document.RootElement,
                descriptor.ContractId,
                "#",
                retainedMaximums);
        }

        Assert.Equal(
            new[]
            {
                "sunder.agent.execution.target:#/$defs/PageSize=2000",
                "sunder.agent.memory.semantic.inspector:#/$defs/MemoryScore=1",
                "sunder.agent.owner.runtime:#/$defs/AgentChatSnapshotRequest/properties/initialTranscriptLimit=500",
                "sunder.agent.owner.runtime:#/$defs/AgentTranscriptAroundTurnRequest/properties/afterLimit=30",
                "sunder.agent.owner.runtime:#/$defs/AgentTranscriptAroundTurnRequest/properties/beforeLimit=30",
                "sunder.agent.owner.runtime:#/$defs/AgentTranscriptPageRequest/properties/limit=500",
                "sunder.agent.owner.runtime:#/$defs/HistorySearchRequest/properties/limit=100",
                "sunder.agent.owner.runtime:#/$defs/HistorySearchStateRequest/properties/limit=200",
                "sunder.agent.run.control:#/$defs/PageSize=1000",
                "sunder.agent.runtime.catalog:#/$defs/PageSize=1000",
            },
            retainedMaximums.Order(StringComparer.Ordinal));
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
            foreach (var limitName in new[] { "maxItems", "maxProperties", "maxDecodedBytes" })
            {
                Assert.False(
                    value.TryGetProperty(limitName, out _),
                    $"Transport limit '{limitName}' in {relativePath}: {value.GetRawText()}");
            }
            if (value.TryGetProperty("maxLength", out var maximumLength))
            {
                Assert.Equal(64, maximumLength.GetInt32());
                Assert.Equal(64, value.GetProperty("minLength").GetInt32());
            }
            Assert.False(
                value.TryGetProperty("minLength", out var minimumLength) && minimumLength.GetInt32() == 0,
                $"Tautological minLength in {relativePath}: {value.GetRawText()}");
            Assert.False(
                value.TryGetProperty("minItems", out var minimumItems) && minimumItems.GetInt32() == 0,
                $"Tautological minItems in {relativePath}: {value.GetRawText()}");
            Assert.False(
                value.TryGetProperty("minProperties", out var minimumProperties) && minimumProperties.GetInt32() == 0,
                $"Tautological minProperties in {relativePath}: {value.GetRawText()}");
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

    private static void InspectFiniteConstraints(
        JsonElement value,
        string contractId,
        string schemaPath,
        List<string> retainedMaximums)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var limitName in new[] { "maxItems", "maxProperties", "maxDecodedBytes" })
            {
                Assert.False(
                    value.TryGetProperty(limitName, out _),
                    $"Transport limit '{limitName}' at {contractId}:{schemaPath}.");
            }
            if (value.TryGetProperty("maxLength", out var maximumLength))
            {
                Assert.Equal(64, maximumLength.GetInt32());
                Assert.Equal(64, value.GetProperty("minLength").GetInt32());
            }
            Assert.False(
                value.TryGetProperty("minLength", out var minimumLength) && minimumLength.GetInt32() == 0,
                $"Tautological minLength at {contractId}:{schemaPath}.");
            Assert.False(
                value.TryGetProperty("minItems", out var minimumItems) && minimumItems.GetInt32() == 0,
                $"Tautological minItems at {contractId}:{schemaPath}.");
            Assert.False(
                value.TryGetProperty("minProperties", out var minimumProperties) && minimumProperties.GetInt32() == 0,
                $"Tautological minProperties at {contractId}:{schemaPath}.");
            if (value.TryGetProperty("maximum", out var maximum))
            {
                retainedMaximums.Add($"{contractId}:{schemaPath}={maximum.GetRawText()}");
            }
            foreach (var property in value.EnumerateObject())
            {
                InspectFiniteConstraints(
                    property.Value,
                    contractId,
                    $"{schemaPath}/{property.Name}",
                    retainedMaximums);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                InspectFiniteConstraints(item, contractId, $"{schemaPath}/{index++}", retainedMaximums);
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
