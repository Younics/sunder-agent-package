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
    public void SharedRegressionHarnesses_RemainSplitAndResponsibilityCapped()
    {
        var testRoot = Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "tests",
            "Sunder.Package.Agent.Tests");
        var lineCaps = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["RegressionTestInfrastructure.cs"] = 1080,
            ["RegressionTestSourceProviders.cs"] = 230,
            ["RegressionTestRpcState.FailureBoundary.cs"] = 80,
            ["RegressionTestPackageContext.cs"] = 60,
            ["RegressionTestPackageScope.cs"] = 40,
        };
        foreach (var item in lineCaps)
        {
            var path = Path.Combine(testRoot, item.Key);
            Assert.True(
                File.ReadLines(path).Count() <= item.Value,
                $"{item.Key} exceeds its {item.Value}-line test-harness responsibility cap.");
        }

        var rpcInfrastructure = File.ReadAllText(Path.Combine(testRoot, "RegressionTestInfrastructure.cs"));
        Assert.DoesNotContain("IPackageContext", rpcInfrastructure, StringComparison.Ordinal);
        Assert.DoesNotContain("IPackageStorageContext", rpcInfrastructure, StringComparison.Ordinal);
        Assert.Equal(
            5,
            Regex.Matches(
                rpcInfrastructure,
                "^internal (?:(?:sealed|partial) )*class ",
                RegexOptions.Multiline | RegexOptions.CultureInvariant).Count);
    }

    [Fact]
    public void AgentRpcContracts_AreExactCanonicalOutputsAndEnvelopeFree()
    {
        var repository = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        var entries = CanonicalDescriptorInventory.PublicAgent();
        var checkedIn = entries.Select(entry => RegressionTestRpcDescriptors.Get(entry.Descriptor.ContractId))
            .OrderBy(static descriptor => descriptor.ContractId, StringComparer.Ordinal)
            .ToArray();
        var source = Path.Combine(repository, "src/Sunder.Package.Agent");
        var contractsRoot = Path.Combine(source, "bin", ResolveConfiguration(), ResolveTargetFramework(), "sunder-dev/payload/shared/contracts");
        var embedded = entries.Select(entry => SunderRpcContractDescriptor.Parse(
                File.ReadAllBytes(Path.Combine(contractsRoot, entry.Slot + ".rpc.json"))))
            .OrderBy(static descriptor => descriptor.ContractId, StringComparer.Ordinal)
            .ToArray();
        var authoredGeneratedRoot = Path.Combine(source, "Rpc", "Generated");
        var generatedRoot = Path.Combine(
            source,
            "obj",
            ResolveConfiguration(),
            ResolveTargetFramework(),
            "sunder-analysis", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            "sunder-rpc", "bindings");
        using var owned = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(source, "sunder-contracts.owned.json")));
        var slots = owned.RootElement.GetProperty("contracts").EnumerateObject().ToDictionary(
            slot => slot.Value.GetProperty("contractId").GetString()!, slot => slot.Name, StringComparer.Ordinal);

        Assert.Equal(19, checkedIn.Length);
        Assert.False(
            Directory.Exists(authoredGeneratedRoot)
            && Directory.EnumerateFiles(authoredGeneratedRoot, "*.g.cs", SearchOption.TopDirectoryOnly).Any(),
            "Generated RPC bindings must remain build outputs rather than checked-in sources.");
        Assert.Equal(19, checkedIn.Count(descriptor => File.Exists(Path.Combine(generatedRoot, slots[descriptor.ContractId] + ".g.cs"))));
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
                "Sunder.Generated.Rpc." + serviceName);
            Assert.DoesNotContain("AgentRpcPayload", generated, StringComparison.Ordinal);
            Assert.Contains("Provider", generated, StringComparison.Ordinal);
            Assert.Contains("Client", generated, StringComparison.Ordinal);
            var generatedPath = Path.Combine(generatedRoot, slots[descriptor.ContractId] + ".g.cs");
            Assert.Equal(generated, File.ReadAllText(generatedPath));
        }
    }

    [Fact]
    public void AllRpcBindings_AreGeneratedFromTheirExactDescriptors()
    {
        var bindings = CanonicalDescriptorInventory.Read();
        Assert.Equal(31, bindings.Count);
        Assert.Equal(19, bindings.Count(static binding => binding.Exposure == "public"));
        Assert.Equal(12, bindings.Count(static binding => binding.Exposure == "package"));
        foreach (var binding in bindings)
        {
            var generatedPath = Path.Combine(CanonicalPackageRoles.Analysis(binding.ProjectDirectory), "bindings", binding.Slot + ".g.cs");
            var generatedNamespace = "Sunder.Generated.Rpc." + string.Concat(binding.Slot.Split('-').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
            Assert.True(File.Exists(generatedPath), $"Generated RPC binding was not found: {generatedPath}");
            Assert.Equal(
                SunderRpcCSharpGenerator.Generate(binding.Descriptor, generatedNamespace,
                    binding.Exposure == "package" ? SunderRpcCSharpAccessibility.Internal : SunderRpcCSharpAccessibility.Public),
                File.ReadAllText(generatedPath));
        }
    }

    [Fact]
    public void MaintainedCSharpSources_DoNotClaimGeneratedStatus()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var offenders = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Where(static path => !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            .Where(static path => string.Equals(
                File.ReadLines(path).FirstOrDefault()?.Trim(),
                "// <auto-generated />",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(sourceRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ExecutionTargetContract_ExposesOnlyStructuredFileSearch()
    {
        var descriptor = RegressionTestRpcDescriptors.Get(AgentRpcContractIds.ExecutionTarget);
        var methods = Assert.Single(descriptor.Services).Methods
            .Select(static method => method.MethodId)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("structured-file-search", methods);
        Assert.DoesNotContain("legacy-file-search", methods);
        Assert.DoesNotContain("AgentFileSearchProcessRequest", descriptor.Definitions.Keys);
        Assert.DoesNotContain("AgentFileSearchProcessResult", descriptor.Definitions.Keys);
        Assert.DoesNotContain("AgentFileSearchPathStyle", descriptor.Definitions.Keys);
    }

    [Fact]
    public void AgentRpcDomainErrors_AreDeclaredAssignedAndExactlyImplemented()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var descriptor in CanonicalDescriptorInventory.PublicAgent())
        {
            using var document = JsonDocument.Parse(descriptor.Descriptor.GetCanonicalUtf8());
            declared.UnionWith(document.RootElement.GetProperty("errors")
                .EnumerateObject()
                .Select(static error => error.Name));
            assigned.UnionWith(document.RootElement.GetProperty("services")
                .EnumerateObject()
                .SelectMany(static service => service.Value.GetProperty("methods").EnumerateObject())
                .SelectMany(static method => method.Value.GetProperty("errors").EnumerateArray())
                .Select(static error => error.GetString()!));
        }

        var emitted = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories).Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Where(path => !path.EndsWith(".Legacy.cs", StringComparison.Ordinal))
            .SelectMany(path => DomainErrorPattern().Matches(File.ReadAllText(path)).Select(static match =>
                match.Groups["code"].Value))
            .Where(code => code.StartsWith("agent.execution.", StringComparison.Ordinal) || code.StartsWith("agent.run-control.", StringComparison.Ordinal))
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
    public void AgentProtocolIdentityDescriptorsAndCapabilityInventory_AreCanonicalV1()
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        var protocolRoot = CanonicalPackageRoles.Source("Agent");
        var project = XDocument.Load(Path.Combine(protocolRoot, "Sunder.Package.Agent.csproj"));
        Assert.Equal("sunder.package.agent", GetSingleProperty(project, "SunderPackageFamilyId"));
        Assert.Empty(project.Descendants("SunderPackageRole"));
        var descriptors = CanonicalDescriptorInventory.PublicAgent().Select(entry => entry.Descriptor)
            .OrderBy(static descriptor => descriptor.ContractId, StringComparer.Ordinal)
            .ToArray();
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(protocolRoot, "bin", ResolveConfiguration(), ResolveTargetFramework(), "sunder-dev/manifest/sunder-package.json")));
        var buildAssets = manifest.RootElement.GetProperty("contractBundles").EnumerateArray()
            .Where(item => item.GetProperty("source").GetString() == "owned" && item.GetProperty("exposure").GetString() == "public")
            .OrderBy(static item => item.GetProperty("contractId").GetString(), StringComparer.Ordinal)
            .ToArray();
        var expectedIdentities = new Dictionary<string, (string DescriptorSha256, string AbiSha256)>(StringComparer.Ordinal)
        {
            ["sunder.agent.behavior.loop"] = ("4dc81c63ef4a45a64e2480229d4690c2210b646b381638da5bb187e47aea66f7", "c28bdf697a3664559718d3f558869096adc04db9ece92a71d5942053cebe1763"),
            ["sunder.agent.chat.provider"] = ("c6a1a870a284725ce59536aaa1d3ec3cbf12b50b9909cf1bb8dcc586b5314c72", "43872bd38b25104499ff1cba52767190088e9015dd83d69e625b76e2ce9509db"),
            ["sunder.agent.child.run.executor"] = ("d5e153b17714eabf425aa3fa5728d8810eb3f774730445bd285246d6bc9ebeac", "1885862bedcb08cec9a8d65c1205c8dbdf78107a5edb2865d0e3d8cf4154557d"),
            ["sunder.agent.durable.lifecycle.observer"] = ("68a9c908358b8ff607f528c3779d58b9040e64818ac2f45621b36177e7611b21", "bd385af872f26796ecd33d3ce66268d3c642c8e924f6c90e4ee717eb5b727f24"),
            ["sunder.agent.embedding.provider"] = ("cd0992243e8eb038124965c0b51d559d555b984459bd7246ca23e2d61cec6cab", "c8e646ba1457b3b0cce307aa465ff1ce53724d083c65af6bde5393c39dcbbb40"),
            ["sunder.agent.execution.target"] = ("e9aac73111d41b661fd95a461cc71cc131884fed2dafd44a752237920fcd8390", "bbec3990bf9b88ffbe4707166bc58d9112e9df6ee9c9ffa11d0cee80f7673438"),
            ["sunder.agent.permission.surface"] = ("a4fdf925ca509da5e4be8a28a3e790c973a2d3f7a7da1fdd79b21bf91864f7f6", "305a6656e50d62e52ea327753bde3151970b0923fc819332e6dd87dc4e8b9d47"),
            ["sunder.agent.profile.capability.consumer"] = ("9aa38121fe4fa67ceb9cb1332d1f3a4829d1db62242597eb54a6ade8c9fe10ed", "db8148cfdbe1f3e50e134f6e900dd360ca9af8fe1eb263df2f11bf907b3fe5e4"),
            ["sunder.agent.prompt.context.contributor"] = ("95f76fbab4aba10a06eab8e3410743be5e98b8f9d91eeb81f7c7be104729a7b3", "e32be8205c41e1e4227e7a4f9007ceb379d100a2c8be511024a36fbdcac661bd"),
            ["sunder.agent.run.control"] = ("2ed3085b36e7c825ef937cdd83fbe1ce0ed8a9baf00289c91787503b6467aa1e", "95daf0091c5ecdeba030a07221370040d988ebaa7c167e42d1a9c35057cb60b2"),
            ["sunder.agent.runtime.catalog"] = ("dd6245bee39a521c1c3b288ad28bf2c23a118cd360e8e617b3e2d4ec4432703c", "e6da2ed724d23c5d0d309f3ebe1a36b57808a1b3537fc70fe3bbd3eee5ace1b4"),
            ["sunder.agent.selectable.capability.provider"] = ("8d51b029826f5f9340de91b60b32404a62345074cc37f1dcf2901716d5e7881a", "b52793dac4c103576164d66420274481a34f0f06aebe263b9d3b330f33d06bdf"),
            ["sunder.agent.session.cleaner"] = ("2df009ae5253633c434bc1dde894978f6b83bd0503aaea1889307287b090d2e2", "74e6274596c87d937531d2bcdbf30572a0ab614a9812d28497f43c59525ee5f8"),
            ["sunder.agent.system.prompt.contributor"] = ("954dabe043ff5b591bdec78b1a6ea4a55c2a154c47e49016879fde2a27f3a03a", "83622fbc8c16bc257dfb7b39a4712811d58dc00b6ed9d4cf2b03f1b1dcd7a16d"),
            ["sunder.agent.tool.source"] = ("d3b481e2f4b9d8a0ae9362f9770dd557a1513f87b33f96925081b7653a6fd4b3", "21b8195d6ede07c53513184fed6feaadf2546e0314045e4819ab0c6d29a2148a"),
            ["sunder.agent.tool.execution.observed"] = ("1e80a60bbba1d0bbfdcd20992b3ad6e6b6d9166fc987056d6945316a611bbee2", "1a34bc8a004d854ae180dd9ac5a9fb4c346de4bd3360c0ec53821d74e361657d"),
            ["sunder.agent.workspace.editor"] = ("2f3bfe7f9c2a4cb32bf629797616c6061d78d14c2027912f32eac3cbb7c1e157", "f5c581cd4a5fd005ea3fd001dd4966f854d97bbeaeb3993e89b032201d9ae200"),
            ["sunder.agent.workspace.execution.resolver"] = ("110f0cc5ee549de495af7211cb2146c57ce59b26fea0f22c9330c6d6981d0714", "90144d8d520d4373d4abd46bd69727e3fd63b847246d604a75d81c92c6572ff6"),
            ["sunder.agent.workspace.path.migrator"] = ("671e522803ab5b2932787dee390b4c75420771951d8c5645c42b92ea6fb049c2", "1f07223e762b7240715ef8f02826094c75ebb42d8cb778721b08c044f637735c"),
        };
        Assert.Equal(19, descriptors.Length);
        Assert.Equal(descriptors.Length, expectedIdentities.Count);
        Assert.Equal(
            descriptors.Select(static descriptor => descriptor.ContractId),
            buildAssets.Select(static item => item.GetProperty("contractId").GetString()));
        Assert.All(descriptors, descriptor =>
        {
            var expectedIdentity = expectedIdentities[descriptor.ContractId];
            Assert.Equal(SunderRpcProtocol.DescriptorVersion, descriptor.DescriptorVersion);
            Assert.Equal("1.0.0", descriptor.Version);
            Assert.Equal(expectedIdentity.DescriptorSha256, descriptor.Sha256);
            Assert.Equal(expectedIdentity.AbiSha256, descriptor.AbiSha256);
        });
        Assert.All(buildAssets, item =>
        {
            Assert.Equal("1.0.0", item.GetProperty("version").GetString());
            Assert.Equal("public", item.GetProperty("exposure").GetString());
        });

        var inventoryPath = Path.Combine(root, "packages.json");
        using var inventory = JsonDocument.Parse(File.ReadAllBytes(inventoryPath));
        Assert.Equal("2.0.0", inventory.RootElement.GetProperty("releaseVersion").GetString());
        var packages = inventory.RootElement.GetProperty("packages").EnumerateArray().ToArray();
        Assert.Equal(15, packages.Length);
        Assert.All(packages, package => Assert.Equal("sunderpkg", package.GetProperty("artifactType").GetString()));
        Assert.DoesNotContain(packages, package => package.GetProperty("key").GetString() == "agent-protocol");

        foreach (var package in inventory.RootElement.GetProperty("capabilities").EnumerateObject())
        {
            var capabilities = package.Value.EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray();
            Assert.Contains(SunderSdkCapabilities.RpcV1, capabilities, StringComparer.Ordinal);
            Assert.DoesNotContain("rpc.v2", capabilities, StringComparer.Ordinal);
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
    public void AgentRpcValueSchemas_UseExplicitV1Semantics()
    {
        var numberDefinitions = new List<(string Name, string Format)>();
        foreach (var entry in CanonicalDescriptorInventory.PublicAgent())
        {
            using var document = JsonDocument.Parse(entry.Descriptor.GetCanonicalUtf8());
            var contractName = entry.Slot;
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

        using (var chatDescriptor = CanonicalDescriptorInventory.Document("sunder.agent.chat.provider"))
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

        using (var ownerDescriptor = CanonicalDescriptorInventory.Document("sunder.agent.owner.runtime"))
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

        using (var runControlDescriptor = CanonicalDescriptorInventory.Document("sunder.agent.run.control"))
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
            assembly.GetType("Sunder.Generated.Rpc.EmbeddingProvider.AgentEmbeddingGenerationResult", true)!
                .GetProperty("Values")!.PropertyType);
        Assert.Equal(
            typeof(float?),
            assembly.GetType("Sunder.Generated.Rpc.ChatProvider.AgentChatOptions", true)!
                .GetProperty("Temperature")!.PropertyType);
        Assert.Equal(
            typeof(byte[]),
            assembly.GetType("Sunder.Generated.Rpc.ChatProvider.AgentChatOptions", true)!
                .GetProperty("ContinuationHandle")!.PropertyType);
        Assert.Equal(
            typeof(SunderRpcOffsetDateTime),
            assembly.GetType("Sunder.Generated.Rpc.RunControl.AgentTurnRecord", true)!
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

        foreach (var projectPath in Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories).Where(AgentPackageRepositoryInventory.IsSourceFile))
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
        using var toolSource = CanonicalDescriptorInventory.Document("sunder.agent.tool.source");
        using var observedExecution = CanonicalDescriptorInventory.Document("sunder.agent.tool.execution.observed");
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
    public void SemanticSources_AreAgentOwnedAndRoleLocal()
    {
        var relocations = AgentSourceMigrationProjectChanges.CentralSemanticRelocations;
        Assert.Equal(142, relocations.Count);
        Assert.Equal(38, AgentSourceMigrationProjectChanges.CentralInterfaceRelocations.Count);
        foreach (var relocation in AgentSourceMigrationProjectChanges.ReviewedCentralRelocations)
        {
            Assert.StartsWith("src/Sunder.Package.Agent/", relocation.Value, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, relocation.Value)), relocation.Value);
        }

        var publicNamespaces = typeof(Sunder.Package.Agent.Contracts.Contracts.IAgentChatProvider).Assembly.ExportedTypes
            .Select(static type => type.Namespace)
            .Where(static value => value?.StartsWith("Sunder.Package.Agent.Contracts", StringComparison.Ordinal) == true)
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

        var buildTargets = XDocument.Load(Path.Combine(repositoryRoot, "Directory.Build.targets"));
        var storageKeyItem = Assert.Single(buildTargets.Descendants("Compile"), static item =>
            item.Attribute("Include")?.Value.EndsWith(
                "src/Sunder.Package.Agent.Shared/Storage/CurrentPackageStorageKey.cs",
                StringComparison.Ordinal) == true);
        Assert.Equal(
            "'$(SunderAgentCurrentPackageStorageKey)' == 'true'",
            storageKeyItem.Parent?.Attribute("Condition")?.Value);

        var sharedProps = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "Sunder.Package.Agent.Shared",
            "Sunder.Package.Agent.Shared.props"));
        Assert.DoesNotContain(sharedProps.Descendants("Compile"), static item =>
            item.Attribute("Include")?.Value.Contains("Storage\\", StringComparison.Ordinal) == true);

        var sourceRoot = Path.Combine(repositoryRoot, "src");
        var storageKeyProjects = Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Where(static path => XDocument.Load(path).Descendants("SunderAgentCurrentPackageStorageKey")
                .Any(static property => string.Equals(property.Value, "true", StringComparison.OrdinalIgnoreCase)))
            .Select(path => Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "src/Sunder.Package.Agent.Execution.Docker/Sunder.Package.Agent.Execution.Docker.csproj",
                "src/Sunder.Package.Agent.Execution.Local/Sunder.Package.Agent.Execution.Local.csproj",
                "src/Sunder.Package.Agent.Mcp/Sunder.Package.Agent.Mcp.csproj",
                "src/Sunder.Package.Agent/Sunder.Package.Agent.csproj",
            }.Order(StringComparer.Ordinal),
            storageKeyProjects);

        var storageKeySource = Path.Combine(
            sourceRoot,
            "Sunder.Package.Agent.Shared",
            "Storage",
            "CurrentPackageStorageKey.cs");
        var storageKeyConsumers = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Where(path => !string.Equals(path, storageKeySource, StringComparison.OrdinalIgnoreCase))
            .Where(static path => File.ReadAllText(path).Contains("CurrentPackageStorageKey.", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(sourceRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "Sunder.Package.Agent.Execution.Docker/DockerExecutionTarget.Configuration.cs",
                "Sunder.Package.Agent.Execution.Docker/DockerExecutionWorkspaceConfigService.cs",
                "Sunder.Package.Agent.Execution.Local/LocalExecutionWorkspaceConfigService.cs",
                "Sunder.Package.Agent.Mcp/Services/McpOAuthSecretKeys.cs",
                "Sunder.Package.Agent.Mcp/Services/McpServerCatalogService.cs",
                "Sunder.Package.Agent/Services/AgentChatSelectionStateService.cs",
            },
            storageKeyConsumers);

        var openAiProjectPath = Path.Combine(
            sourceRoot,
            "Sunder.Package.Agent.Provider.OpenAI",
            "Sunder.Package.Agent.Provider.OpenAI.csproj");
        var openAiProject = XDocument.Load(openAiProjectPath);
        var providerSharedItem = Assert.Single(openAiProject.Descendants("Compile"), static item =>
            string.Equals(
                item.Attribute("Include")?.Value,
                "..\\Sunder.Package.Agent.Provider.Shared\\*.cs",
                StringComparison.Ordinal));
        var providerSharedExclusions = providerSharedItem.Attribute("Exclude")?.Value
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            ?? [];
        Assert.Equal(new[] { "ApiKeySettingsState.cs", "ApiKeyUtilitySettingsState.cs", "ProviderConfigurationSections.cs", "UtilityModelSettingsState.cs" },
            providerSharedExclusions.Select(path => Path.GetFileName(NormalizePath(path))).Order(StringComparer.Ordinal));
        Assert.All(providerSharedExclusions, exclusion => Assert.True(
            File.Exists(Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(openAiProjectPath)!,
                NormalizePath(exclusion)))),
            $"OpenAI shared-source exclusion does not exist: {exclusion}"));

        foreach (var package in AgentPackageRepositoryInventory.GetRuntimePackageProjects()
                     .Where(static package => !string.Equals(package.Name, "Sunder.Package.Agent", StringComparison.Ordinal)))
        {
            Assert.Matches(BaseAgentV2DependencyPattern(), AgentSourceMigrationProjectChanges.ReadPackageMetadataBeforeMigration(package.MetadataPath));
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
        var entry = File.ReadAllText(Path.Combine(CanonicalPackageRoles.Analysis(sourceRoot), "roles/worker/worker-entry.g.cs"));
        Assert.Contains("AuthHandler =", entry, StringComparison.Ordinal);
        Assert.Contains("GetRequiredService<global::Sunder.Package.Agent.Provider.OpenAI.OpenAiPackageAuthHandler>()", entry, StringComparison.Ordinal);
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
        Assert.DoesNotContain("ListWorkspacesAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetWorkspaceAsync(", source, StringComparison.Ordinal);
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
