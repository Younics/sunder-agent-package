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
        var descriptor = Assert.Single(CanonicalDescriptorInventory.Read(), value => value.HistoricalPath == relativePath).Descriptor;
        var bytes = descriptor.GetCanonicalUtf8();
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;

        Assert.Equal(SunderRpcProtocol.ValueSchemaProfile, descriptor.SchemaProfile);
        Assert.Equal("2.0.0", descriptor.Version);
        Assert.Equal(SunderRpcProtocol.ValueSchemaProfile, root.GetProperty("schemaProfile").GetString());

        var definitions = root.GetProperty("$defs");
        foreach (var definition in definitions.EnumerateObject())
        {
            Assert.DoesNotMatch("^(String|Boolean|Int32|Int64|SunderRpc.*|Nullable.*|.*List)$", definition.Name);
            InspectSchema(definition.Value, descriptor.ContractId, "#/$defs/" + definition.Name, relativePath);
        }

        Assert.Equal(
            definitions.EnumerateObject().Select(static definition => definition.Name).Order().ToArray(),
            ReachableDefinitions(root).Order().ToArray());

        if (relativePath.Contains("Mcp", StringComparison.Ordinal))
        {
            Assert.DoesNotContain("\"chunks\"", document.RootElement.GetRawText(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DockerPresentationDescriptor_PreservesApprovedNumericStatusAndImageReferenceBounds()
    {
        var descriptor = CurrentPrivateRpcDescriptors.Get("sunder.package.agent.execution.docker.presentation");
        const string statusReference = "#/$defs/DockerImageStatus";
        const string imageDefinitionReference = "#/$defs/DockerImageDefinition";

        Assert.True(descriptor.IsValid(statusReference, JsonSerializer.SerializeToElement(0), out var firstStatusError), firstStatusError);
        Assert.True(descriptor.IsValid(statusReference, JsonSerializer.SerializeToElement(4), out var lastStatusError), lastStatusError);
        Assert.False(descriptor.IsValid(statusReference, JsonSerializer.SerializeToElement(-1), out _));
        Assert.False(descriptor.IsValid(statusReference, JsonSerializer.SerializeToElement(5), out _));
        Assert.False(descriptor.IsValid(statusReference, JsonSerializer.SerializeToElement("ready"), out _));

        foreach (var length in new[] { 1, 128, 129, 256, 511, 512 })
            Assert.True(descriptor.IsValid(imageDefinitionReference, DockerImage(new string('i', length)), out var maximumError), maximumError);
        Assert.False(descriptor.IsValid(imageDefinitionReference, DockerImage(string.Empty), out _));
        Assert.False(descriptor.IsValid(imageDefinitionReference, DockerImage(new string('i', 513)), out _));
    }

    [Fact]
    public void DescriptorInventory_UsesUnreleasedVersionsAndOnlySemanticFiniteBounds()
    {
        var descriptors = CanonicalDescriptorInventory.Read();
        var retainedMaximums = new List<string>();

        Assert.Equal(32, descriptors.Count);
        foreach (var entry in descriptors)
        {
            var descriptor = entry.Descriptor;
            var bytes = descriptor.GetCanonicalUtf8();
            using var document = JsonDocument.Parse(bytes);

            if (entry.Exposure == "public") CurrentPublicRpcDescriptors.AssertCurrent(descriptor);
            else CurrentPrivateRpcDescriptors.AssertCurrent(descriptor);
            InspectFiniteConstraints(
                document.RootElement,
                descriptor.ContractId,
                "#",
                retainedMaximums);
        }

        Assert.Equal(
            new[]
            {
                "sunder.agent.behavior.loop:#/$defs/CSunder_Agent_Run_Control_RunControl_ServiceReference/properties/activationEpoch=9007199254740991",
                "sunder.agent.behavior.loop:#/$defs/CSunder_Agent_Run_Control_RunControl_ServiceReference/properties/sessionGeneration=9007199254740991",
                "sunder.agent.execution.target:#/$defs/AgentFileReadRequest/properties/limit/oneOf/1=2000",
                "sunder.agent.owner.runtime:#/$defs/AgentChatSnapshotRequest/properties/initialTranscriptLimit=500",
                "sunder.agent.owner.runtime:#/$defs/AgentTranscriptAroundTurnRequest/properties/afterLimit=30",
                "sunder.agent.owner.runtime:#/$defs/AgentTranscriptAroundTurnRequest/properties/beforeLimit=30",
                "sunder.agent.owner.runtime:#/$defs/AgentTranscriptPageRequest/properties/limit=500",
                "sunder.agent.owner.runtime:#/$defs/HistorySearchRequest/properties/limit=100",
                "sunder.agent.owner.runtime:#/$defs/HistorySearchStateRequest/properties/limit=200",
                "sunder.agent.prompt.context.contributor:#/$defs/CSunder_Agent_Execution_Target_ExecutionTarget_ServiceReference/properties/activationEpoch=9007199254740991",
                "sunder.agent.prompt.context.contributor:#/$defs/CSunder_Agent_Execution_Target_ExecutionTarget_ServiceReference/properties/sessionGeneration=9007199254740991",
                "sunder.agent.run.control:#/$defs/RunControlListRecentTurnsRequest/properties/limit=100",
                "sunder.agent.runtime.catalog:#/$defs/RuntimeCatalogListRecentTranscriptHeadersRequest/properties/limit=1000",
                "sunder.agent.runtime.catalog:#/$defs/RuntimeCatalogListRecentTurnsRequest/properties/limit=1000",
                "sunder.agent.runtime.catalog:#/$defs/RuntimeCatalogListTranscriptHeadersAfterRequest/properties/limit=1000",
                "sunder.agent.runtime.catalog:#/$defs/RuntimeCatalogListTranscriptHeadersBeforeRequest/properties/limit=1000",
                "sunder.agent.runtime.catalog:#/$defs/RuntimeCatalogListTurnsAfterRequest/properties/limit=1000",
                "sunder.agent.runtime.catalog:#/$defs/RuntimeCatalogListTurnsBeforeRequest/properties/limit=1000",
                "sunder.agent.tool.execution.observed:#/$defs/CSunder_Agent_Execution_Target_ExecutionTarget_ServiceReference/properties/activationEpoch=9007199254740991",
                "sunder.agent.tool.execution.observed:#/$defs/CSunder_Agent_Execution_Target_ExecutionTarget_ServiceReference/properties/sessionGeneration=9007199254740991",
                "sunder.agent.tool.source:#/$defs/CSunder_Agent_Execution_Target_ExecutionTarget_ServiceReference/properties/activationEpoch=9007199254740991",
                "sunder.agent.tool.source:#/$defs/CSunder_Agent_Execution_Target_ExecutionTarget_ServiceReference/properties/sessionGeneration=9007199254740991",
                "sunder.agent.workspace.execution.resolver:#/$defs/CSunder_Agent_Execution_Target_ExecutionTarget_ServiceReference/properties/activationEpoch=9007199254740991",
                "sunder.agent.workspace.execution.resolver:#/$defs/CSunder_Agent_Execution_Target_ExecutionTarget_ServiceReference/properties/sessionGeneration=9007199254740991",
                "sunder.package.agent.subagents.owner:#/$defs/ManagementAroundTurnRequest/properties/afterLimit=30",
                "sunder.package.agent.subagents.owner:#/$defs/ManagementAroundTurnRequest/properties/beforeLimit=30",
                "sunder.package.agent.subagents.owner:#/$defs/ManagementRecentTurnsRequest/properties/limit=500",
                "sunder.package.agent.subagents.owner:#/$defs/ManagementTurnsAfterRequest/properties/limit=500",
                "sunder.package.agent.subagents.owner:#/$defs/ManagementTurnsBeforeRequest/properties/limit=500",
            },
            retainedMaximums.Order(StringComparer.Ordinal));
    }

    private static void InspectSchema(JsonElement value, string contractId, string schemaPath, string relativePath)
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
                AssertExactEnum(value, enumValues, contractId, schemaPath);
            }
            foreach (var limitName in new[] { "maxItems", "maxProperties", "maxDecodedBytes" })
            {
                Assert.False(
                    value.TryGetProperty(limitName, out _),
                    $"Transport limit '{limitName}' in {relativePath}: {value.GetRawText()}");
            }
            if (value.TryGetProperty("maxLength", out var maximumLength))
            {
                AssertExactTextBounds(value, contractId, schemaPath, maximumLength);
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
                InspectSchema(property.Value, contractId, $"{schemaPath}/{property.Name}", relativePath);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                InspectSchema(item, contractId, schemaPath, relativePath);
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
                AssertExactTextBounds(value, contractId, schemaPath, maximumLength);
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
                // Natural CLR integers retain representation bounds; additional
                // finite paging bounds come from the reviewed owning source models.
                if (value.TryGetProperty("enum", out var enumeration))
                {
                    Assert.Equal(enumeration.EnumerateArray().Max(item => item.GetInt64()), maximum.GetInt64());
                    Assert.Equal(enumeration.EnumerateArray().Min(item => item.GetInt64()), value.GetProperty("minimum").GetInt64());
                }
                else if (!(maximum.TryGetInt64(out var bound) && bound == int.MaxValue
                    && value.TryGetProperty("minimum", out var minimum) && minimum.GetInt64() == int.MinValue))
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

    private static void AssertExactEnum(
        JsonElement value,
        JsonElement enumValues,
        string contractId,
        string schemaPath)
    {
        if (contractId == "sunder.package.agent.execution.docker.presentation"
            && schemaPath == "#/$defs/DockerImageStatus")
        {
            Assert.Equal("integer", value.GetProperty("type").GetString());
            Assert.Equal([0, 1, 2, 3, 4], enumValues.EnumerateArray().Select(static item => item.GetInt32()));
            return;
        }

        Assert.Equal("string", value.GetProperty("type").GetString());
        Assert.All(enumValues.EnumerateArray(), static item =>
            Assert.Equal(JsonValueKind.String, item.ValueKind));
    }

    private static void AssertExactTextBounds(
        JsonElement value,
        string contractId,
        string schemaPath,
        JsonElement maximumLength)
    {
        var expected = (Minimum: 64, Maximum: 64);
        if (contractId == "sunder.package.agent.execution.docker.presentation"
            && schemaPath == "#/$defs/DockerImageDefinition/properties/imageReference")
            expected = (1, 512);
        if (schemaPath.Contains("_ServiceReference/properties/", StringComparison.Ordinal))
        {
            Assert.Contains(contractId, new[] { "sunder.agent.behavior.loop", "sunder.agent.prompt.context.contributor",
                "sunder.agent.tool.execution.observed", "sunder.agent.tool.source", "sunder.agent.workspace.execution.resolver" });
            var reference = contractId == "sunder.agent.behavior.loop"
                ? "CSunder_Agent_Run_Control_RunControl_ServiceReference"
                : "CSunder_Agent_Execution_Target_ExecutionTarget_ServiceReference";
            Assert.StartsWith("#/$defs/" + reference + "/properties/", schemaPath, StringComparison.Ordinal);
            expected = schemaPath.Split('/').Last() switch
            {
                "abiSha256" or "descriptorSha256" => (64, 64),
                "contractId" or "packageId" or "providerId" => (1, 128),
                "contractVersion" or "packageVersion" => (5, 256),
                "endpoint" => (1, 256),
                _ => throw new InvalidOperationException("Unreviewed service-reference text bound: " + schemaPath),
            };
        }
        Assert.Equal(expected.Maximum, maximumLength.GetInt32());
        Assert.Equal(expected.Minimum, value.GetProperty("minLength").GetInt32());
    }

    private static JsonElement DockerImage(string imageReference)
        => JsonSerializer.SerializeToElement(new
        {
            imageReference,
            status = 0,
            lastPulledAtUtc = (string?)null,
            lastMessage = (string?)null,
        });

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
