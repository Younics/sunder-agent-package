using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class RepositoryArchitectureTests
{
    [Fact]
    public void AgentRpcContracts_AreExactEmbeddedAndEnvelopeFree()
    {
        var contractsRoot = Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "src",
            "Sunder.Package.Agent.Contracts",
            "Contracts");
        var checkedIn = Directory.EnumerateFiles(contractsRoot, "*.rpc.json", SearchOption.TopDirectoryOnly)
            .Select(File.ReadAllBytes)
            .Select(static bytes => SunderRpcContractDescriptor.Parse(bytes))
            .OrderBy(static descriptor => descriptor.ContractId, StringComparer.Ordinal)
            .ToArray();
        var embedded = AgentRpcContractDescriptors.All
            .OrderBy(static descriptor => descriptor.ContractId, StringComparer.Ordinal)
            .ToArray();
        var authoredGeneratedRoot = Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "src",
            "Sunder.Package.Agent.Contracts",
            "Rpc",
            "Generated");
        var generatedRoot = Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "src",
            "Sunder.Package.Agent.Contracts",
            "obj",
            ResolveConfiguration(),
            ResolveTargetFramework(),
            "sunder-rpc");

        Assert.Equal(19, checkedIn.Length);
        Assert.False(
            Directory.Exists(authoredGeneratedRoot)
            && Directory.EnumerateFiles(authoredGeneratedRoot, "*.g.cs", SearchOption.TopDirectoryOnly).Any(),
            "Generated RPC bindings must remain build outputs rather than checked-in sources.");
        Assert.Equal(19, Directory.EnumerateFiles(generatedRoot, "*.g.cs", SearchOption.TopDirectoryOnly).Count());
        Assert.Equal(checkedIn.Select(static descriptor => descriptor.ContractId), embedded.Select(static descriptor => descriptor.ContractId));
        Assert.Equal(checkedIn.Select(static descriptor => descriptor.Sha256), embedded.Select(static descriptor => descriptor.Sha256));
        foreach (var descriptor in checkedIn)
        {
            Assert.DoesNotContain("Payload", descriptor.Definitions.Keys, StringComparer.Ordinal);
            foreach (var method in descriptor.Services.SelectMany(static service => service.Methods))
            {
                Assert.NotEqual("#/$defs/Payload", method.RequestSchemaReference);
                Assert.NotEqual("#/$defs/Payload", method.OutputSchemaReference);
            }
            var serviceName = string.Concat(Assert.Single(descriptor.Services).ServiceId
                .Split('-')
                .Select(static part => char.ToUpperInvariant(part[0]) + part[1..]));
            var generated = SunderRpcCSharpGenerator.Generate(
                descriptor,
                "Sunder.Package.Agent.Protocol.Generated." + serviceName);
            Assert.DoesNotContain("AgentRpcPayload", generated, StringComparison.Ordinal);
            Assert.Contains("Provider", generated, StringComparison.Ordinal);
            Assert.Contains("Client", generated, StringComparison.Ordinal);
            var generatedPath = Path.Combine(generatedRoot, serviceName + ".g.cs");
            Assert.Equal(generated, File.ReadAllText(generatedPath));
        }
    }

    [Fact]
    public void AllRpcBindings_AreGeneratedFromTheirExactDescriptors()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var bindings = Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .SelectMany(projectPath => XDocument.Load(projectPath)
                .Descendants("SunderRpcBinding")
                .Select(binding => new
                {
                    ProjectPath = projectPath,
                    Descriptor = binding.Attribute("Include")?.Value,
                    Namespace = binding.Attribute("Namespace")?.Value,
                    OutputFile = binding.Attribute("OutputFile")?.Value,
                }))
            .ToArray();

        Assert.Equal(31, bindings.Length);
        foreach (var binding in bindings)
        {
            Assert.False(string.IsNullOrWhiteSpace(binding.Descriptor));
            Assert.False(string.IsNullOrWhiteSpace(binding.Namespace));
            Assert.False(string.IsNullOrWhiteSpace(binding.OutputFile));
            var projectDirectory = Path.GetDirectoryName(binding.ProjectPath)!;
            var descriptor = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(Path.Combine(
                projectDirectory,
                NormalizePath(binding.Descriptor!))));
            var generatedPath = Path.Combine(
                projectDirectory,
                "obj",
                ResolveConfiguration(),
                ResolveTargetFramework(),
                "sunder-rpc",
                binding.OutputFile!);

            Assert.True(File.Exists(generatedPath), $"Generated RPC binding was not found: {generatedPath}");
            Assert.Equal(
                SunderRpcCSharpGenerator.Generate(descriptor, binding.Namespace!),
                File.ReadAllText(generatedPath));
        }
    }

    [Fact]
    public void AgentRpcDomainErrors_AreDeclaredAssignedAndExactlyImplemented()
    {
        var protocolRoot = Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "src",
            "Sunder.Package.Agent.Contracts");
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var descriptorPath in Directory.EnumerateFiles(
                     Path.Combine(protocolRoot, "Contracts"),
                     "*.rpc.json",
                     SearchOption.TopDirectoryOnly))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(descriptorPath));
            declared.UnionWith(document.RootElement.GetProperty("errors")
                .EnumerateObject()
                .Select(static error => error.Name));
            assigned.UnionWith(document.RootElement.GetProperty("services")
                .EnumerateObject()
                .SelectMany(static service => service.Value.GetProperty("methods").EnumerateObject())
                .SelectMany(static method => method.Value.GetProperty("errors").EnumerateArray())
                .Select(static error => error.GetString()!));
        }

        var emitted = Directory.EnumerateFiles(Path.Combine(protocolRoot, "Rpc"), "*.cs", SearchOption.TopDirectoryOnly)
            .SelectMany(path => DomainErrorPattern().Matches(File.ReadAllText(path)).Select(static match =>
                match.Groups["code"].Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            [
                "agent.execution.facet-unavailable",
                "agent.run-control.capability-unavailable",
                "agent.run-control.default-loop-unavailable",
                "agent.run-control.prompt-context-acknowledgment-unavailable",
            ],
            declared.Order(StringComparer.Ordinal));
        Assert.True(declared.SetEquals(assigned), "Every declared domain error must be assigned to at least one RPC method.");
        Assert.True(declared.SetEquals(emitted), "Handwritten Agent RPC domain failures must exactly match descriptor declarations.");
    }

    [Fact]
    public void AgentProtocolIdentityDescriptorsAndCapabilityInventory_AreCanonicalV2()
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        var protocolRoot = Path.Combine(root, "src", "Sunder.Package.Agent.Contracts");
        var project = XDocument.Load(Path.Combine(protocolRoot, "Sunder.Package.Agent.Contracts.csproj"));
        Assert.Equal("Sunder.Package.Agent.Protocol", GetSingleProperty(project, "AssemblyName"));
        Assert.Equal("Sunder.Package.Agent.Protocol", GetSingleProperty(project, "PackageId"));

        var descriptors = Directory.EnumerateFiles(
                Path.Combine(protocolRoot, "Contracts"),
                "*.rpc.json",
                SearchOption.TopDirectoryOnly)
            .Select(File.ReadAllBytes)
            .Select(static bytes => SunderRpcContractDescriptor.Parse(bytes))
            .OrderBy(static descriptor => descriptor.ContractId, StringComparer.Ordinal)
            .ToArray();
        var buildAssets = XDocument.Load(Path.Combine(
                protocolRoot,
                "buildTransitive",
                "Sunder.Package.Agent.Protocol.props"))
            .Descendants("SunderContractBundle")
            .OrderBy(static item => item.Attribute("ContractId")?.Value, StringComparer.Ordinal)
            .ToArray();
        var expectedIdentities = new Dictionary<string, (string DescriptorSha256, string AbiSha256)>(StringComparer.Ordinal)
        {
            ["sunder.agent.behavior.loop"] = ("bcb61bb6de029af4b856d5550a951602abd11538a433e5affd3f66feebc507ab", "4f71677e7a8b088b447410b92ce0449d04f0646cca384ed889de5802647fb49b"),
            ["sunder.agent.chat.provider"] = ("bdd8f5eff8e065f980a4f9e9f5503962f70781128728eb3e6a103fc486865624", "341538e233661630d93fb9cf64db6b36a08873aa6be9d218082481477a8ba34e"),
            ["sunder.agent.child.run.executor"] = ("bef53d0fc20949838c2984ec47511d8bffbc8816c2b0ee6076bbbe757ddae9ab", "59c0f448dca374853c04076fab85eb1ea03422871ce2cb736a378212342f89f5"),
            ["sunder.agent.durable.lifecycle.observer"] = ("e9286f28b1ec32733bb070a9578afa9dba6c859f56136b2059a88673da962e19", "5216f8788958576ad7ba2f33f64c47b27600c963d04cef3d45f3ebf371426dd1"),
            ["sunder.agent.embedding.provider"] = ("63abafb9bc36a2953338faa43d7858bfd59eda4a3f5b6f18bdb08898e7f9d7b2", "d0077e48364626c18f14257db1a5b55156c04f96d32071242aaa360b5b31b219"),
            ["sunder.agent.execution.target"] = ("a295c9e5569cd543e60057bb3233cd0bcd89b884f3dba3fb78775dc6956ecba1", "82b37639f7e8f20991365d731902a181c027498843e8d7dc23ed927201072751"),
            ["sunder.agent.permission.surface"] = ("72a4c7157104973dfb519277df976dfc26f7ec8d65d3d721f2fe8c925d30dbbb", "09a75027b87d575cc5e0a4c0e8edfd7bbc774f1e5f4604e9792d7cd5f5fa4124"),
            ["sunder.agent.profile.capability.consumer"] = ("780b517c4d076be1ce99ec7c9cae1b0ad94c231f420d5f6fbd1756a924123951", "43c3030f02509aff1839f6beb74c6fbe878d0c33952e5f5b01f749f644ea4f42"),
            ["sunder.agent.prompt.context.contributor"] = ("a650e09f417087d50deae61a47a8f4fcf5d196387845998b25ca8935d5fe9d1d", "b15d15a739c10b90ca84aef9a17d2024deed79caed784d53c4c76f65aa3f30e3"),
            ["sunder.agent.run.control"] = ("43162c5e13f6a787e314fe58db219709782a8078b3de45315a721026f401e5b2", "83ac422ed68e585446e91bffd609f9d21e1bf08b14c984e3067752c1686f336f"),
            ["sunder.agent.runtime.catalog"] = ("2547cdf3471c37aa18425d72bc3b6283e90f340d759d7984b8521290a5114d18", "612f5fdc7a614b0428df76c9757f90c1f0bbae63805454868c1b4bd31303deae"),
            ["sunder.agent.selectable.capability.provider"] = ("4ec0d6d4826e37c29ca1f9bbef7a1387abca3138f2c94f46bd3258c4b474974e", "e7b5e55e4d2d9b43575a908d2beb2bbacd30165e6fecbeb4d33ddfdecde28728"),
            ["sunder.agent.session.cleaner"] = ("0bbe9bfbbfcb60c2778351e778882212663fe66c0c10f8d177b5e62c54ad4acf", "57b3e279fdce9501364e025312a5afb0057658c1770ea728e85aabb3532b0b86"),
            ["sunder.agent.system.prompt.contributor"] = ("3fb579f2cb7afb984d04e7b356cc399a15514a7d5a4b6896b456f2ff5854f2ae", "247f4447ea25d72573b95cdbb07b499501a88af19331287f159f73520b996a02"),
            ["sunder.agent.tool.source"] = ("3a4f9aabca0731c35e443493ca04ccb0ee35fc8caf71489e182fadcae18e50c9", "a9963324b18e0d018b92165d3107e7d481508cd079a7a6624bef5a66c0aeea77"),
            ["sunder.agent.tool.execution.observed"] = ("46ffee9ab3295a1ab3e7180418836df84c35e15638f45fb3277b2df522a28ca1", "f2dcd6c794d6eb7bf81e745d3c43830ad6fd8ffbd314a16aac3e198e1707f2c6"),
            ["sunder.agent.workspace.editor"] = ("e14f547a65c1fbe61c17eabcefce0af0568113769862f1e02c6d6f6fe6b481db", "29829aba4c7dc593e8e7bea181731e6e46d25a967d5f9e73466c700fcb5930f5"),
            ["sunder.agent.workspace.execution.resolver"] = ("e961cbdf4d6213f20fa97e709401f778c2e4e614bab61c06ebc02ee55486eecd", "d51e3e05eb2898865064c07fa358a4ae59dbfc03a9f145a9ee4c20d85dd639ea"),
            ["sunder.agent.workspace.path.migrator"] = ("448c58b625f7f2408e6b9dcdb167dca8c9cdd6dbcaeff0482ffb0067189bf417", "f9fbc6cec25514330b65a749012f06f7f6bc624d745d1824650e20d122ddc39d"),
        };
        Assert.Equal(19, descriptors.Length);
        Assert.Equal(descriptors.Length, expectedIdentities.Count);
        Assert.Equal(
            descriptors.Select(static descriptor => descriptor.ContractId),
            buildAssets.Select(static item => item.Attribute("ContractId")?.Value));
        Assert.All(descriptors, descriptor =>
        {
            var expectedIdentity = expectedIdentities[descriptor.ContractId];
            Assert.Equal(2, descriptor.DescriptorVersion);
            Assert.Equal("1.0.0", descriptor.Version);
            Assert.Equal(expectedIdentity.DescriptorSha256, descriptor.Sha256);
            Assert.Equal(expectedIdentity.AbiSha256, descriptor.AbiSha256);
        });
        Assert.All(buildAssets, item =>
        {
            Assert.Equal("1.0.0", item.Attribute("Version")?.Value);
            Assert.Equal("public", item.Attribute("Exposure")?.Value);
        });

        var inventoryPath = Path.Combine(root, "packages.json");
        using var inventory = JsonDocument.Parse(File.ReadAllBytes(inventoryPath));
        Assert.Equal("2.0.0", inventory.RootElement.GetProperty("releaseVersion").GetString());
        var packages = inventory.RootElement.GetProperty("packages").EnumerateArray().ToArray();
        Assert.Equal(16, packages.Length);
        var protocol = Assert.Single(packages, static package =>
            package.GetProperty("key").GetString() == "agent-protocol");
        Assert.Equal("Sunder.Package.Agent.Protocol", protocol.GetProperty("packageId").GetString());
        Assert.Equal("nuget", protocol.GetProperty("artifactType").GetString());

        foreach (var package in inventory.RootElement.GetProperty("capabilities").EnumerateObject())
        {
            var capabilities = package.Value.EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray();
            Assert.DoesNotContain(SunderSdkCapabilities.RpcV1, capabilities, StringComparer.Ordinal);
            Assert.Contains(SunderSdkCapabilities.RpcV2, capabilities, StringComparer.Ordinal);
            Assert.DoesNotContain("runtime-operations.v1", capabilities, StringComparer.Ordinal);
            Assert.DoesNotContain("runtime-invocation-errors.v1", capabilities, StringComparer.Ordinal);
            Assert.DoesNotContain("stacks.contributions.v1", capabilities, StringComparer.Ordinal);
            if (capabilities.Contains("stacks.v1", StringComparer.Ordinal))
            {
                Assert.Contains("stacks.rpc.v1", capabilities, StringComparer.Ordinal);
            }
        }

        var ratchetedText = File.ReadAllText(inventoryPath)
            + string.Join('\n', Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md")
                .Select(File.ReadAllText))
            + File.ReadAllText(Path.Combine(root, "README.md"));
        Assert.DoesNotContain("StackContributionsV1", ratchetedText, StringComparison.Ordinal);
        Assert.DoesNotContain("stacks.contributions.v1", ratchetedText, StringComparison.Ordinal);
        Assert.DoesNotContain("IPackageExtensionCatalog", ratchetedText, StringComparison.Ordinal);
        Assert.DoesNotContain("IPackageExtensionInvocationCatalog", ratchetedText, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentRpcValueSchemas_UseExplicitV2Semantics()
    {
        var contractsRoot = Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "src",
            "Sunder.Package.Agent.Contracts",
            "Contracts");
        var numberDefinitions = new List<(string Name, string Format)>();
        foreach (var path in Directory.EnumerateFiles(contractsRoot, "*.rpc.json", SearchOption.TopDirectoryOnly))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var contractName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
            Assert.Equal("sunder.rpc.values.v1", document.RootElement.GetProperty("schemaProfile").GetString());
            Assert.DoesNotContain("\"hasValue\"", document.RootElement.GetRawText(), StringComparison.Ordinal);
            foreach (var definition in document.RootElement.GetProperty("$defs").EnumerateObject())
            {
                Assert.DoesNotMatch("^(String|Guid|DateTimeOffset|Int32|Int64|Single|Nullable[0-9]*|ListOf.*)$", definition.Name);
                if (definition.Value.TryGetProperty("logicalType", out var logicalType))
                {
                    Assert.Equal("content-reference", logicalType.GetString());
                }
                if (definition.Value.TryGetProperty("type", out var typeElement)
                    && typeElement.GetString() == "number")
                {
                    numberDefinitions.Add((
                        $"{contractName}:{definition.Name}",
                        definition.Value.GetProperty("format").GetString()!));
                }
            }
        }

        Assert.Equal(
            new[]
            {
                ("chat-provider:ChatOptionValue", "float32"),
                ("embedding-provider:EmbeddingValue", "float64"),
                ("run-control:ChatOptionValue", "float32"),
            },
            numberDefinitions.OrderBy(static item => item.Name, StringComparer.Ordinal));

        using (var chatDescriptor = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
                   contractsRoot,
                   "chat-provider.rpc.json"))))
        {
            var chatDefinitions = chatDescriptor.RootElement.GetProperty("$defs");
            Assert.Equal("float32", chatDefinitions.GetProperty("ChatOptionValue").GetProperty("format").GetString());
            Assert.Equal("base64", chatDefinitions.GetProperty("InlineContinuationBytes").GetProperty("contentEncoding").GetString());
            Assert.Equal("uri", chatDefinitions.GetProperty("AbsoluteUri").GetProperty("format").GetString());
            Assert.Empty(chatDefinitions.GetProperty("JSON").EnumerateObject());
            Assert.Equal(
                ["null", "$ref"],
                chatDefinitions.GetProperty("UtilityModelResolution")
                    .GetProperty("oneOf")
                    .EnumerateArray()
                    .Select(static branch => branch.TryGetProperty("type", out var type) ? type.GetString() : "$ref"));
            Assert.Equal(
                "#/$defs/CalendarDate",
                chatDefinitions.GetProperty("AgentModelDescriptor")
                    .GetProperty("properties")
                    .GetProperty("releaseDate")
                    .GetProperty("$ref")
                    .GetString());
            Assert.Equal(
                ["#/$defs/AgentChatUpdateEvent", "#/$defs/AgentChatErrorEvent"],
                chatDefinitions.GetProperty("AgentChatStreamEvent")
                    .GetProperty("oneOf")
                    .EnumerateArray()
                    .Select(static branch => branch.GetProperty("$ref").GetString()));
            Assert.Equal(
                "#/$defs/Timestamp",
                chatDefinitions.GetProperty("AgentChatUpdateEvent")
                    .GetProperty("properties")
                    .GetProperty("createdAt")
                    .GetProperty("$ref")
                    .GetString());
            Assert.Equal(
                "#/$defs/AgentChatProviderFailureKind",
                chatDefinitions.GetProperty("AgentChatErrorEvent")
                    .GetProperty("properties")
                    .GetProperty("errorFailureKind")
                    .GetProperty("$ref")
                    .GetString());
        }

        using (var ownerDescriptor = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
                   AgentPackageRepositoryInventory.RepositoryRoot.FullName,
                   "src",
                   "Sunder.Package.Agent",
                   "OwnerProtocol",
                   "Contracts",
                   "agent-owner-runtime.rpc.json"))))
        {
            var ownerRoot = ownerDescriptor.RootElement;
            Assert.Equal("sunder.rpc.values.v1", ownerRoot.GetProperty("schemaProfile").GetString());
            Assert.Equal("1.0.0", ownerRoot.GetProperty("version").GetString());
            var ownerDefinitions = ownerRoot.GetProperty("$defs");
            var date = ownerDefinitions.GetProperty("ModelReleaseDate");
            Assert.Equal("string", date.GetProperty("type").GetString());
            Assert.Equal("date", date.GetProperty("format").GetString());
            Assert.Equal(
                "#/$defs/ModelReleaseDate",
                ownerDefinitions.GetProperty("AgentModelDescriptor")
                    .GetProperty("properties")
                    .GetProperty("releaseDate")
                    .GetProperty("$ref")
                    .GetString());
            Assert.Equal(
                "content-reference",
                ownerDefinitions.GetProperty("ContentReference").GetProperty("logicalType").GetString());
            Assert.True(ownerDefinitions.TryGetProperty("JsonText", out _));
            Assert.False(ownerDefinitions.TryGetProperty("AgentAttachmentTransferRequest", out _));
            Assert.False(ownerDefinitions.TryGetProperty("DateOnly", out _));
            Assert.False(ownerDefinitions.TryGetProperty("DayOfWeek", out _));
        }

        using (var runControlDescriptor = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
                   contractsRoot,
                   "run-control.rpc.json"))))
        {
            var definitions = runControlDescriptor.RootElement.GetProperty("$defs");
            Assert.Equal("float32", definitions.GetProperty("ChatOptionValue").GetProperty("format").GetString());
            Assert.Equal(2, definitions.GetProperty("AgentChatStreamEvent").GetProperty("oneOf").GetArrayLength());
            Assert.Equal(
                "#/$defs/AgentReasoningEffort",
                definitions.GetProperty("AgentModelVariantDescriptor")
                    .GetProperty("properties")
                    .GetProperty("reasoningEffort")
                    .GetProperty("$ref")
                    .GetString());
        }

        var assembly = typeof(Sunder.Package.Agent.Contracts.Models.AgentEmbeddingGenerationResult).Assembly;
        Assert.Equal(
            typeof(IReadOnlyList<double>),
            typeof(Sunder.Package.Agent.Contracts.Models.AgentEmbeddingGenerationResult)
                .GetProperty("Values")!.PropertyType);
        Assert.Equal(
            typeof(IReadOnlyList<double>),
            assembly.GetType("Sunder.Package.Agent.Protocol.Generated.EmbeddingProvider.AgentEmbeddingGenerationResult")!
                .GetProperty("Values")!.PropertyType);
        Assert.Equal(
            typeof(float?),
            assembly.GetType("Sunder.Package.Agent.Protocol.Generated.ChatProvider.AgentChatOptions")!
                .GetProperty("Temperature")!.PropertyType);
        Assert.Equal(
            typeof(byte[]),
            assembly.GetType("Sunder.Package.Agent.Protocol.Generated.ChatProvider.AgentChatOptions")!
                .GetProperty("ContinuationHandle")!.PropertyType);
        Assert.Equal(
            typeof(SunderRpcOffsetDateTime),
            assembly.GetType("Sunder.Package.Agent.Protocol.Generated.RunControl.AgentTurnRecord")!
                .GetProperty("UpdatedAtUtc")!.PropertyType);
        Assert.Equal(
            typeof(SunderRpcOffsetDateTime?),
            typeof(AgentChatUpdateEvent).GetProperty(nameof(AgentChatUpdateEvent.CreatedAt))!.PropertyType);
        Assert.Equal(
            typeof(SunderRpcOptional<JsonElement>),
            typeof(AgentChatContent).GetProperty(nameof(AgentChatContent.Json))!.PropertyType);
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Protocol.AgentRpcOptional`1"));
    }

    [Fact]
    public void AgentRpcBindings_DoNotUseOpaquePayloadOrLegacyExtensionTransport()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var source = string.Join('\n', Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Select(File.ReadAllText));
        var prohibited = new[]
        {
            "AgentRpcPayload",
            "AddPayloadUnary",
            "AddPayloadStream",
            "InvokePayloadAsync",
            "SubscribePayloadAsync",
            "PackageExtensionPoints",
            "IPackageExtensionCatalog",
            "IPackageExtensionInvocationCatalog",
            "GetExtensionReferences",
            "GetExtensionContributions",
            "AgentChatContentTransferStore",
            "AgentChatContentChunk",
            "AgentChatContentDiscard",
            "RunControlChatContentChunk",
            "RunControlChatContentDiscard",
            "\"stage-content\"",
            "\"discard-content\"",
            "stage-chat-content",
            "discard-chat-content",
        };

        Assert.All(prohibited, token => Assert.DoesNotContain(token, source, StringComparison.Ordinal));
    }

    [Fact]
    public void ProductionProjectReferences_FollowSharedAssemblyDirection()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var allowedSharedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Sunder.Agent.Execution.Common",
            "Sunder.Package.Agent.Contracts",
            "Sunder.Package.Agent.Mcp.Shared",
            "Sunder.Package.Agent.Provider.OpenAI.Shared",
        };
        allowedSharedProjects.UnionWith(Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Where(path => XDocument.Load(path).Descendants("SunderPackageRole")
                .Any(static role => string.Equals(role.Value, "protocol", StringComparison.OrdinalIgnoreCase)))
            .Select(static path => Path.GetFileNameWithoutExtension(path)!));
        var executionCommonConsumers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Sunder.Package.Agent.Execution.Docker",
            "Sunder.Package.Agent.Execution.Local",
            "Sunder.Package.Agent.Tools.Files",
            "Sunder.Package.Agent.Tools.Shell",
            "Sunder.Package.Agent.Tools.Web",
        };

        foreach (var projectPath in Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var project = XDocument.Load(projectPath);
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var referencedProductionProjects = project
                .Descendants("ProjectReference")
                .Select(static reference => reference.Attribute("Include")?.Value)
                .Where(static include => !string.IsNullOrWhiteSpace(include))
                .Select(include => Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(projectPath)!,
                    NormalizePath(include!))))
                .Where(path => path.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .Select(static path => Path.GetFileNameWithoutExtension(path)!)
                .ToArray();

            Assert.DoesNotContain(referencedProductionProjects, referenced => !allowedSharedProjects.Contains(referenced));
            if (!executionCommonConsumers.Contains(projectName))
            {
                Assert.DoesNotContain("Sunder.Agent.Execution.Common", referencedProductionProjects, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void ObservedToolExecution_UsesTheToolSourceWireSchemasWithoutChangingTheOriginalContract()
    {
        var contractsRoot = Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "src",
            "Sunder.Package.Agent.Contracts",
            "Contracts");
        using var toolSource = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(contractsRoot, "tool-source.rpc.json")));
        using var observedExecution = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            contractsRoot,
            "tool-execution-observed.rpc.json")));
        var toolSourceDefinitions = toolSource.RootElement.GetProperty("$defs");
        var observedDefinitions = observedExecution.RootElement.GetProperty("$defs");

        foreach (var definitionName in new[]
                 {
                     "TextValue",
                     "Timestamp",
                     "Flag",
                     "SignedInteger",
                     "AgentWorkspaceBindingRecord",
                     "EntityId",
                     "AgentWorkspaceDocumentRecord",
                     "AgentWorkspaceDocumentRecordCollection",
                     "AgentWorkspacePathRecord",
                     "AgentWorkspacePathRecordCollection",
                     "AgentWorkspaceRecord",
                     "AgentRpcProviderHandle",
                     "TextValues",
                     "AgentResourceClaim",
                     "AgentResourceClaimCollection",
                     "AgentToolExecutionContext",
                     "AgentResourceOperationContextWire",
                     "AgentToolExecutionContextWire",
                     "AgentToolRequest",
                     "ToolInvocationRequest",
                     "AgentToolSourceItem",
                     "AgentToolSourceItemCollection",
                     "AgentToolResult",
                     "Identifier",
                     "IdentifierOrEmpty",
                     "VersionText",
                     "PathText",
                     "DisplayText",
                     "ContentText",
                     "JsonText",
                     "Sha256Text",
                     "SortOrder",
                     "ResourceOperationIndex",
                     "Revision",
                     "Count",
                 })
        {
            Assert.Equal(
                JsonSerializer.Serialize(toolSourceDefinitions.GetProperty(definitionName)),
                JsonSerializer.Serialize(observedDefinitions.GetProperty(definitionName)));
        }
    }

    [Fact]
    public void ContractSources_AreGroupedByOwnedDomainWithoutChangingShippedNamespaces()
    {
        var contractsRoot = Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "src",
            "Sunder.Package.Agent.Contracts");
        var allowedFolders = new HashSet<string>(StringComparer.Ordinal)
        {
            "Execution",
            "Extensions",
            "Models",
            "Providers",
            "Rpc",
            "Runtime",
            "Tools",
        };
        var sourceFiles = Directory.EnumerateFiles(contractsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .ToArray();

        Assert.NotEmpty(sourceFiles);
        foreach (var sourceFile in sourceFiles)
        {
            var relativePath = Path.GetRelativePath(contractsRoot, sourceFile);
            var topLevelFolder = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            Assert.Contains(topLevelFolder, allowedFolders);
        }

        foreach (var folder in allowedFolders)
        {
            Assert.Contains(sourceFiles, path => string.Equals(
                Path.GetRelativePath(contractsRoot, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0],
                folder,
                StringComparison.Ordinal));
        }

        var publicNamespaces = typeof(Sunder.Package.Agent.Contracts.Contracts.IAgentChatProvider).Assembly.ExportedTypes
            .Select(static type => type.Namespace)
            .Where(static value => value is not null)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(publicNamespaces, namespaceName => namespaceName is not
                 ("Sunder.Package.Agent.Contracts"
                 or "Sunder.Package.Agent.Contracts.Contracts"
                 or "Sunder.Package.Agent.Contracts.Models"
                 or "Sunder.Package.Agent.Contracts.Services"
                 or "Sunder.Package.Agent.Protocol")
             && !namespaceName!.StartsWith("Sunder.Package.Agent.Protocol.Generated.", StringComparison.Ordinal));
    }

    [Fact]
    public void PackageModuleTypes_AreAbsentFromWorkersAndAppOnlyElsewhere()
    {
        foreach (var package in AgentPackageRepositoryInventory.GetRuntimePackageProjects())
        {
            var moduleTypes = Assembly.Load(package.Name).GetTypes()
                .Where(static type => !type.IsAbstract)
                .Where(static type => typeof(ISunderAppPackageModule).IsAssignableFrom(type))
                .ToArray();

            if (package.IsWorker)
            {
                Assert.Empty(moduleTypes);
                Assert.NotNull(Assembly.Load(package.Name).EntryPoint);
                continue;
            }

            Assert.Single(moduleTypes);
            foreach (var moduleType in moduleTypes)
            {
                var declaredMethodNames = moduleType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                    .Select(static method => method.Name)
                    .ToArray();
                Assert.DoesNotContain("ConfigureRuntimeServices", declaredMethodNames);
                Assert.DoesNotContain("RegisterRuntimeContributions", declaredMethodNames);
            }
        }
    }

    [Fact]
    public void RepositoryBuildAndPackagePolicy_IsCentralAndConsistent()
    {
        var repositoryRoot = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        var buildProperties = XDocument.Load(Path.Combine(repositoryRoot, "Directory.Build.props"));
        Assert.Equal("enable", GetSingleProperty(buildProperties, "Nullable"));
        Assert.Equal("true", GetSingleProperty(buildProperties, "TreatWarningsAsErrors"));
        Assert.Equal("true", GetSingleProperty(buildProperties, "Deterministic"));
        Assert.Equal("true", GetSingleProperty(buildProperties, "EnableNETAnalyzers"));
        Assert.Equal("2.0.0", GetSingleProperty(buildProperties, "VersionPrefix"));
        Assert.Equal("$(VersionPrefix)", GetSingleProperty(buildProperties, "SunderAgentVersion"));
        Assert.Equal("$(SunderAgentVersion)", GetSingleProperty(buildProperties, "Version"));
        Assert.Equal("$(SunderAgentVersion)", GetSingleProperty(buildProperties, "PackageVersion"));
        Assert.Equal("$(SunderAgentVersion)", GetSingleProperty(buildProperties, "InformationalVersion"));
        Assert.Equal("GPL-3.0-only", GetSingleProperty(buildProperties, "PackageLicenseExpression"));

        var packageProperties = XDocument.Load(Path.Combine(repositoryRoot, "Directory.Packages.props"));
        Assert.Equal("true", GetSingleProperty(packageProperties, "ManagePackageVersionsCentrally"));
        Assert.Equal("false", GetSingleProperty(packageProperties, "CentralPackageVersionOverrideEnabled"));
        var sunderPackageVersions = packageProperties.Descendants("PackageVersion")
            .Where(reference => reference.Attribute("Include")?.Value.StartsWith("Sunder.", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Equal(5, sunderPackageVersions.Length);
        Assert.Equal(
            "[1.1.0]",
            Assert.Single(sunderPackageVersions, static reference =>
                reference.Attribute("Include")?.Value == "Sunder.Sdk.Worker").Attribute("Version")?.Value);
        Assert.All(
            sunderPackageVersions.Where(static reference =>
                reference.Attribute("Include")?.Value != "Sunder.Sdk.Worker"),
            reference => Assert.Equal("[1.1.0,1.2.0)", reference.Attribute("Version")?.Value));
        var avaloniaVersions = packageProperties.Descendants("PackageVersion")
            .Where(static reference => reference.Attribute("Include")?.Value == "Avalonia")
            .ToArray();
        Assert.Equal(2, avaloniaVersions.Length);
        Assert.All(avaloniaVersions, reference =>
        {
            var condition = Assert.IsType<string>(reference.Attribute("Condition")?.Value);
            Assert.Contains("$(SunderPackageFamilyId)", condition, StringComparison.Ordinal);
            Assert.Contains("$(SunderPackageRole)", condition, StringComparison.Ordinal);
            Assert.DoesNotContain("$(MSBuildProjectName)", condition, StringComparison.Ordinal);
        });
        var presentationTestProject = XDocument.Load(Path.Combine(
            repositoryRoot,
            "tests",
            "Sunder.Package.Agent.Presentation.Tests",
            "Sunder.Package.Agent.Presentation.Tests.csproj"));
        Assert.Equal("sunder.package.agent", GetSingleProperty(presentationTestProject, "SunderPackageFamilyId"));
        Assert.Equal("test", GetSingleProperty(presentationTestProject, "SunderPackageRole"));

        foreach (var package in AgentPackageRepositoryInventory.GetRuntimePackageProjects()
                     .Where(static package => !string.Equals(package.Name, "Sunder.Package.Agent", StringComparison.Ordinal)))
        {
            Assert.Matches(BaseAgentV2DependencyPattern(), File.ReadAllText(package.MetadataPath));
        }

        var editorConfig = File.ReadAllText(Path.Combine(repositoryRoot, ".editorconfig"));
        Assert.Contains("[**/obj/**/*.cs]", editorConfig, StringComparison.Ordinal);
        Assert.Contains("[**/*.g.cs]", editorConfig, StringComparison.Ordinal);
        Assert.Contains("generated_code = true", editorConfig, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflows_DoNotPropagateAgentVersionThroughCoreVersionProperties()
    {
        var repositoryRoot = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        foreach (var workflow in new[] { "sunder-package-smoke.yml", "sunder-package-release.yml" })
        {
            var source = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", workflow));
            Assert.Contains("-p:SunderAgentVersion=", source, StringComparison.Ordinal);
            Assert.DoesNotContain("-p:Version=", source, StringComparison.Ordinal);
            Assert.DoesNotContain("-p:PackageVersion=", source, StringComparison.Ordinal);
            Assert.DoesNotContain("-p:InformationalVersion=", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProductionPackages_DoNotOwnNetworkCallbackListeners()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var sourceFiles = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .ToArray();

        foreach (var sourceFile in sourceFiles)
        {
            var source = File.ReadAllText(sourceFile);
            Assert.DoesNotContain("HttpListener", source, StringComparison.Ordinal);
            Assert.DoesNotContain("TcpListener", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WebApplication.CreateBuilder", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Socket.Listen", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OpenAiInteractiveAuth_UsesHostAuthAndCallbackFlows()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src", "Sunder.Package.Agent.Provider.OpenAI");
        var source = string.Join('\n', Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Select(File.ReadAllText));

        Assert.Contains("IPackageAuthHandler", source, StringComparison.Ordinal);
        Assert.Contains("PackageCallbackFlowRunner", source, StringComparison.Ordinal);
        Assert.Contains("AuthHandler =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IPackageCallbackHandler", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SignInWithBrowserAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CodexAuthCallbackServer", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PresentationCode_DoesNotUseUntrackedDispatcherPostsOrNonEventAsyncVoid()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var asyncVoidPattern = new Regex(
            @"async\s+void\s+(?<name>\w+)\s*\(",
            RegexOptions.CultureInvariant);

        foreach (var sourceFile in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(AgentPackageRepositoryInventory.IsSourceFile))
        {
            var source = File.ReadAllText(sourceFile);
            Assert.DoesNotContain("Dispatcher.UIThread.Post", source, StringComparison.Ordinal);
            Assert.DoesNotContain("_uiDispatcher.Post", source, StringComparison.Ordinal);
            foreach (Match match in asyncVoidPattern.Matches(source))
            {
                Assert.StartsWith("On", match.Groups["name"].Value, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void AgentHotspots_ArePartitionedByExplicitResponsibility()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var requiredFiles = new[]
        {
            "Sunder.Package.Agent/Services/AgentSessionService.Projections.cs",
            "Sunder.Package.Agent/Services/AgentSessionService.Mutations.cs",
            "Sunder.Package.Agent/Services/BehaviorLoops/AgentBehaviorLoopHost.ToolBatch.cs",
            "Sunder.Package.Agent/Services/BehaviorLoops/AgentBehaviorLoopHost.ToolExecution.cs",
            "Sunder.Package.Agent/Services/BehaviorLoops/AgentPermissionSuspensionCoordinator.cs",
            "Sunder.Package.Agent/Storage/AgentLocalStore.Permissions.Queries.cs",
            "Sunder.Package.Agent/Storage/AgentLocalStore.Permissions.Writes.cs",
            "Sunder.Package.Agent/Storage/AgentLocalStore.PermissionRecovery.cs",
            "Sunder.Package.Agent.Shared/PackageViews/TranscriptScrollCoordinator.Anchors.cs",
            "Sunder.Package.Agent.Shared/PackageViews/TranscriptScrollCoordinator.Paging.cs",
            "Sunder.Package.Agent.Shared/PackageViews/TranscriptScrollCoordinator.Lifetime.cs",
            "Sunder.Package.Agent.Subagents/PackageViews/SubagentsViewModel.Editor.cs",
            "Sunder.Package.Agent.Subagents/PackageViews/SubsessionsViewModel.Transcript.cs",
            "Sunder.Package.Agent.Mcp/AgentMcpSettingsViewModel.DocumentLoading.cs",
        };

        Assert.All(requiredFiles, relativePath => Assert.True(
            File.Exists(Path.Combine(sourceRoot, NormalizePath(relativePath))),
            $"Missing responsibility boundary '{relativePath}'."));

        var hostSource = File.ReadAllText(Path.Combine(
            sourceRoot,
            "Sunder.Package.Agent",
            "Services",
            "BehaviorLoops",
            "AgentBehaviorLoopHost.cs"));
        Assert.Contains("AgentToolBatchCoordinator", hostSource, StringComparison.Ordinal);
        Assert.Contains("AgentPermissionSuspensionCoordinator", hostSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteToolBatchAsync(", hostSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SubagentAppGateway_UsesOnlyNarrowAsyncSubsessionPorts()
    {
        var source = File.ReadAllText(Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "src",
            "Sunder.Package.Agent.Subagents",
            "Runtime",
            "SubagentRuntimeBridge.cs"));

        Assert.Contains("ISubsessionSessionReader", source, StringComparison.Ordinal);
        Assert.Contains("ISubsessionCheckpointReader", source, StringComparison.Ordinal);
        Assert.Contains("ISubsessionTranscriptPageReader", source, StringComparison.Ordinal);
        Assert.Contains("ISubsessionChangeNotifications", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SubagentAppRuntimeGateway : ISubagentManagementGateway, IAgentRuntimeCatalog",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(".GetAwaiter().GetResult()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => [];", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public AgentWorkspaceRecord? GetWorkspace(string workspaceId) => null;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AppRuntimeBridges_RemainAsyncAndExecutionPackagesDoNotDuplicateWorkspaceTransport()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var bridgePaths = new[]
        {
            "Sunder.Package.Agent/Runtime/AgentAppRuntimeGateway.cs",
            "Sunder.Package.Agent/Runtime/AgentAppRuntimeGateway.Commands.cs",
            "Sunder.Package.Agent.Memory.Semantic/Runtime/MemoryRuntimeBridge.cs",
            "Sunder.Package.Agent.Subagents/Runtime/SubagentRuntimeBridge.cs",
        };
        foreach (var bridgePath in bridgePaths)
        {
            var source = File.ReadAllText(Path.Combine(sourceRoot, NormalizePath(bridgePath)));
            Assert.DoesNotContain(".GetAwaiter().GetResult()", source, StringComparison.Ordinal);
            Assert.DoesNotContain(".Result", source, StringComparison.Ordinal);
            Assert.DoesNotContain(".Wait(", source, StringComparison.Ordinal);
        }

        var executionRoots = new[]
        {
            Path.Combine(sourceRoot, "Sunder.Package.Agent.Execution.Local"),
            Path.Combine(sourceRoot, "Sunder.Package.Agent.Execution.Docker"),
        };
        Assert.False(File.Exists(Path.Combine(executionRoots[0], "LocalExecutionAppRuntimeClient.cs")));
        Assert.False(File.Exists(Path.Combine(executionRoots[1], "DockerExecutionAppRuntimeClient.cs")));
        var executionSource = string.Join('\n', executionRoots
            .SelectMany(static root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Select(File.ReadAllText));
        Assert.DoesNotContain("GetWorkspaceEditor", executionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveWorkspaceEditor", executionSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AppAsyncEntryPoints_UseReviewedPresentationBoundaries()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var sourceFiles = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .ToArray();
        var asyncVoidMethods = sourceFiles
            .SelectMany(path => AsyncVoidMethodPattern().Matches(File.ReadAllText(path))
                .Select(match => $"{NormalizePath(Path.GetRelativePath(sourceRoot, path))}::{match.Groups["method"].Value}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Empty(asyncVoidMethods);

        var unreviewedCommands = new List<string>();
        foreach (var path in sourceFiles)
        {
            var source = File.ReadAllText(path);
            var matches = AsyncRelayCommandPattern().Matches(source).ToArray();
            for (var index = 0; index < matches.Length; index++)
            {
                var match = matches[index];
                var end = index + 1 < matches.Length ? matches[index + 1].Index : source.Length;
                var implementation = source[match.Index..end];
                var id = $"{Path.GetRelativePath(sourceRoot, path).Replace('\\', '/')}::{match.Groups["method"].Value}";
                if (implementation.Contains("_operations.RunAsync", StringComparison.Ordinal)
                    || implementation.Contains("_operations.SubmitAsync", StringComparison.Ordinal)
                    || implementation.Contains("RunOperationAsync", StringComparison.Ordinal)
                    || implementation.Contains("RunPresentationOperationAsync", StringComparison.Ordinal)
                    || implementation.Contains("_presentationOperations.RunAsync", StringComparison.Ordinal)
                    || ExplicitLocalAsyncRelayCommandBoundaries.Contains(id))
                {
                    continue;
                }

                unreviewedCommands.Add(id);
            }
        }

        Assert.True(
            unreviewedCommands.Count == 0,
            "Async RelayCommand operations must use PresentationOperationObserver or a reviewed local boundary:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, unreviewedCommands.Order(StringComparer.Ordinal)));
    }

    private static readonly IReadOnlySet<string> ExplicitLocalAsyncRelayCommandBoundaries =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Sunder.Package.Agent.Execution.Docker/App/DockerExecutionSettingsViewModel.Commands.cs::RetryInitializationAsync",
            "Sunder.Package.Agent.Execution.Local/App/LocalExecutionSettingsViewModel.cs::DeleteSelectedShellAsync",
            "Sunder.Package.Agent.Execution.Local/App/LocalExecutionSettingsViewModel.cs::RetryInitializationAsync",
            "Sunder.Package.Agent.Execution.Local/App/LocalExecutionSettingsViewModel.cs::SaveExecutionSettingsAsync",
            "Sunder.Package.Agent.Execution.Local/App/LocalExecutionSettingsViewModel.cs::SaveShellsAsync",
            "Sunder.Package.Agent.Mcp/McpSettingsOperationsViewModel.cs::AuthorizeMcpServerAsync",
            "Sunder.Package.Agent.Mcp/McpSettingsOperationsViewModel.cs::DeleteAsync",
            "Sunder.Package.Agent.Mcp/McpSettingsOperationsViewModel.cs::DisconnectMcpServerAsync",
            "Sunder.Package.Agent.Mcp/McpSettingsOperationsViewModel.cs::DisconnectMcpServerOAuthAsync",
            "Sunder.Package.Agent.Mcp/McpSettingsOperationsViewModel.cs::DiscoverToolsAsync",
            "Sunder.Package.Agent.Mcp/McpSettingsOperationsViewModel.cs::ImportCommonConfigurationsAsync",
            "Sunder.Package.Agent.Mcp/McpSettingsOperationsViewModel.cs::ReconnectMcpServerAsync",
            "Sunder.Package.Agent.Mcp/McpSettingsOperationsViewModel.cs::SaveAsync",
            "Sunder.Package.Agent.Provider.Anthropic/App/AnthropicSettingsViewModel.cs::SaveSettingsAsync",
            "Sunder.Package.Agent.Provider.Gemini/App/GeminiSettingsViewModel.cs::SaveSettingsAsync",
            "Sunder.Package.Agent.Provider.LMStudio/App/LMStudioSettingsViewModel.cs::RetryInitializationAsync",
            "Sunder.Package.Agent.Provider.LMStudio/App/LMStudioSettingsViewModel.cs::SaveConnectionAsync",
            "Sunder.Package.Agent.Provider.LMStudio/App/LMStudioSettingsViewModel.cs::SaveSettingsAsync",
            "Sunder.Package.Agent.Provider.LMStudio/App/LMStudioSettingsViewModel.cs::SaveUtilityModelAsync",
            "Sunder.Package.Agent.Provider.Shared/ApiKeySettingsState.cs::ClearCredentialAsync",
            "Sunder.Package.Agent.Provider.Shared/ApiKeySettingsState.cs::SaveCredentialAsync",
            "Sunder.Package.Agent.Provider.Shared/UtilityModelSettingsState.cs::SaveUtilityModelAsync",
            "Sunder.Package.Agent.Skills/PackageViews/SkillSettingsViewModel.cs::DeleteSelectedSkillAsync",
            "Sunder.Package.Agent.Skills/PackageViews/SkillSettingsViewModel.cs::ImportCommonSkillFoldersAsync",
            "Sunder.Package.Agent.Skills/PackageViews/SkillSettingsViewModel.cs::ImportGitHubAsync",
            "Sunder.Package.Agent.Subagents/PackageViews/SubagentsViewModel.Operations.cs::CreateSubagentAsync",
            "Sunder.Package.Agent.Subagents/PackageViews/SubagentsViewModel.Operations.cs::DeleteSubagentAsync",
            "Sunder.Package.Agent.Subagents/PackageViews/SubagentsViewModel.Operations.cs::OpenSelectedChatProviderSettingsAsync",
            "Sunder.Package.Agent.Subagents/PackageViews/SubagentsViewModel.Operations.cs::ReloadSubagentChatProvidersAsync",
            "Sunder.Package.Agent.Subagents/PackageViews/SubagentsViewModel.Operations.cs::SaveSubagentAsync",
            "Sunder.Package.Agent/PackageViews/AgentHistorySearchViewModel.Search.cs::ClearQueryAsync",
            "Sunder.Package.Agent/PackageViews/AgentHistorySearchViewModel.Search.cs::EscapeAsync",
            "Sunder.Package.Agent/PackageViews/AgentHistorySearchViewModel.Search.cs::LoadMoreAsync",
            "Sunder.Package.Agent/PackageViews/AgentHistorySearchViewModel.Search.cs::OpenResultAsync",
            "Sunder.Package.Agent/PackageViews/AgentHistorySearchViewModel.Search.cs::SearchNowAsync",
            "Sunder.Package.Agent/PackageViews/AgentProfilesViewModel.Operations.cs::CreateProfileAsync",
            "Sunder.Package.Agent/PackageViews/AgentProfilesViewModel.Operations.cs::DeleteProfileAsync",
            "Sunder.Package.Agent/PackageViews/AgentProfilesViewModel.Operations.cs::OpenSelectedChatProviderSettingsAsync",
            "Sunder.Package.Agent/PackageViews/AgentProfilesViewModel.Operations.cs::OpenSelectedEmbeddingProviderSettingsAsync",
            "Sunder.Package.Agent/PackageViews/AgentProfilesViewModel.Operations.cs::ReloadProfileProvidersAsync",
            "Sunder.Package.Agent/PackageViews/AgentProfilesViewModel.Operations.cs::SaveProfileAsync",
            "Sunder.Package.Agent/PackageViews/AgentWorkspacesViewModel.Save.cs::SaveWorkspace",
            "Sunder.Package.Agent/PackageViews/AgentWorkspacesViewModel.cs::CreateWorkspaceAsync",
            "Sunder.Package.Agent/PackageViews/AgentWorkspacesViewModel.cs::DeleteWorkspaceAsync",
        };

    private static string GetSingleProperty(XDocument document, string propertyName)
        => Assert.Single(document.Descendants(propertyName)).Value;

    private static string NormalizePath(string path)
        => path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    private static string ResolveTargetFramework()
        => new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;

    private static string ResolveConfiguration()
        => new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent?.Name
           ?? "Debug";

    [GeneratedRegex("SunderPackageDependency\\s*\\(\\s*PackageId\\s*=\\s*\"sunder\\.package\\.agent\"\\s*,\\s*VersionRange\\s*=\\s*\">=2\\.0\\.0 <3\\.0\\.0\"", RegexOptions.CultureInvariant)]
    private static partial Regex BaseAgentV2DependencyPattern();

    [GeneratedRegex("SunderRpcErrorKind\\.Domain,\\s*\"(?<code>[a-z0-9.-]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex DomainErrorPattern();

    [GeneratedRegex(@"\basync\s+void\s+(?<method>[A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex AsyncVoidMethodPattern();

    [GeneratedRegex(@"\[RelayCommand(?:\([^\]]*\))?\]\s*(?:private|public|internal)\s+(?:static\s+)?(?:async\s+)?(?:Task|ValueTask)(?:<[^;{]+>)?\s+(?<method>[A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex AsyncRelayCommandPattern();
}
