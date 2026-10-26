using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Rpc;
using Sunder.Package.Build.Rpc.Generation;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class RepositoryArchitectureTests
{
    [Fact]
    public void MainAgent_UsesGeneratedServicesWithoutAnAuthoredTransportTier()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src/Sunder.Package.Agent");
        var consumers = Path.Combine(sourceRoot, "Rpc/Consumers");
        Assert.False(Directory.Exists(consumers) && Directory.EnumerateFiles(consumers, "*.cs", SearchOption.AllDirectories).Any());
        var forbidden = new Regex(
            @"\b(?:AgentRpcCatalog|AgentRpcInvocation|AgentRpcReference|AgentRpcLease|AgentRpcProviderHandle|GeneratedProviderCallLifetime|DeferredAgentRpcServiceHandler)\b|Sunder\.Generated\.Rpc|\b(?:CreateCallScopeAsync|RegisterContentAsync|RegisterContentFileAsync|OpenContentAsync)\s*\(",
            RegexOptions.CultureInvariant);
        var offenders = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Where(path => forbidden.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(sourceRoot, path)).ToArray();
        Assert.Empty(offenders);
    }

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
            ["RegressionTestRpcInfrastructure.cs"] = 1080,
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

        var rpcInfrastructure = string.Join('\n',
            File.ReadAllText(Path.Combine(testRoot, "RegressionTestInfrastructure.cs")),
            File.ReadAllText(Path.Combine(testRoot, "RegressionTestRpcInfrastructure.cs")));
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
        var checkedIn = entries.Select(entry => CurrentPublicRpcDescriptors.Get(entry.Descriptor.ContractId))
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

        Assert.Equal(18, checkedIn.Length);
        Assert.False(
            Directory.Exists(authoredGeneratedRoot)
            && Directory.EnumerateFiles(authoredGeneratedRoot, "*.g.cs", SearchOption.TopDirectoryOnly).Any(),
            "Generated RPC bindings must remain build outputs rather than checked-in sources.");
        Assert.Equal(18, checkedIn.Count(descriptor => File.Exists(Path.Combine(generatedRoot, slots[descriptor.ContractId] + ".g.cs"))));
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
            var generatedPath = Path.Combine(generatedRoot, slots[descriptor.ContractId] + ".g.cs");
            var generated = File.ReadAllText(generatedPath);
            Assert.Contains("Sunder.Generated.Packages.", generated, StringComparison.Ordinal);
            Assert.DoesNotContain("Sunder.Generated.Rpc.", generated, StringComparison.Ordinal);
            Assert.DoesNotContain("AgentRpcPayload", generated, StringComparison.Ordinal);
            Assert.Contains($"ContractId = \"{descriptor.ContractId}\"", generated, StringComparison.Ordinal);
            Assert.Contains($"ContractVersion = \"{descriptor.Version}\"", generated, StringComparison.Ordinal);
            Assert.Contains($"DescriptorSha256 = \"{descriptor.Sha256}\"", generated, StringComparison.Ordinal);
            Assert.Contains($"AbiSha256 = \"{descriptor.AbiSha256}\"", generated, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AllRpcBindings_AreGeneratedFromTheirExactDescriptors()
    {
        var bindings = CanonicalDescriptorInventory.Read();
        Assert.Equal(31, bindings.Count);
        Assert.Equal(18, bindings.Count(static binding => binding.Exposure == "public"));
        Assert.Equal(13, bindings.Count(static binding => binding.Exposure == "package"));
        foreach (var binding in bindings)
        {
            var generatedPath = Path.Combine(CanonicalPackageRoles.Analysis(binding.ProjectDirectory), "bindings", binding.Slot + ".g.cs");
            var generatedNamespace = GeneratedBindingNamespace(binding.ProjectDirectory, binding.Descriptor.ContractId);
            Assert.True(File.Exists(generatedPath), $"Generated RPC binding was not found: {generatedPath}");
            Assert.Equal(
                SunderRpcCSharpGenerator.Generate(binding.Descriptor, generatedNamespace,
                    SunderRpcCSharpAccessibility.Internal),
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
        var descriptor = CurrentPublicRpcDescriptors.Get("younics.ai.agent.execution.target");
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
            .Concat(Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories).Where(AgentPackageRepositoryInventory.IsSourceFile)
                .SelectMany(path => DomainErrorDeclarationPattern().Matches(File.ReadAllText(path)).Select(static match =>
                    match.Groups["code"].Value)))
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
        Assert.Equal("younics.ai.agent", GetSingleProperty(project, "SunderPackageFamilyId"));
        Assert.Empty(project.Descendants("SunderPackageRole"));
        var descriptors = CanonicalDescriptorInventory.PublicAgent().Select(entry => entry.Descriptor)
            .OrderBy(static descriptor => descriptor.ContractId, StringComparer.Ordinal)
            .ToArray();
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(protocolRoot, "bin", ResolveConfiguration(), ResolveTargetFramework(), "sunder-dev/manifest/sunder-package.json")));
        var buildAssets = manifest.RootElement.GetProperty("contractBundles").EnumerateArray()
            .Where(item => item.GetProperty("source").GetString() == "owned" && item.GetProperty("exposure").GetString() == "public")
            .OrderBy(static item => item.GetProperty("contractId").GetString(), StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(18, descriptors.Length);
        Assert.Equal(
            descriptors.Select(static descriptor => descriptor.ContractId),
            buildAssets.Select(static item => item.GetProperty("contractId").GetString()));
        Assert.All(descriptors, descriptor =>
        {
            Assert.Equal(SunderRpcProtocol.DescriptorVersion, descriptor.DescriptorVersion);
            CurrentPublicRpcDescriptors.AssertCurrent(descriptor);
        });
        Assert.All(buildAssets, item =>
        {
            var descriptor = CurrentPublicRpcDescriptors.Get(item.GetProperty("contractId").GetString()!);
            Assert.Equal(descriptor.Version, item.GetProperty("version").GetString());
            Assert.Equal(descriptor.Sha256, item.GetProperty("descriptorSha256").GetString());
            Assert.Equal(descriptor.AbiSha256, item.GetProperty("abiSha256").GetString());
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
                CollectNumberDefinitions(definition.Value, $"{contractName}:{definition.Name}", numberDefinitions);
            }
        }

        Assert.Equal(
            [
                ("chat-provider:AgentChatOptions.properties.frequencyPenalty.oneOf.1", "float32"),
                ("chat-provider:AgentChatOptions.properties.presencePenalty.oneOf.1", "float32"),
                ("chat-provider:AgentChatOptions.properties.temperature.oneOf.1", "float32"),
                ("chat-provider:AgentChatOptions.properties.topP.oneOf.1", "float32"),
                ("embedding-provider:AgentEmbeddingGenerationResult.properties.values.items", "float64"),
                ("run-control:AgentChatOptions.properties.frequencyPenalty.oneOf.1", "float32"),
                ("run-control:AgentChatOptions.properties.presencePenalty.oneOf.1", "float32"),
                ("run-control:AgentChatOptions.properties.temperature.oneOf.1", "float32"),
                ("run-control:AgentChatOptions.properties.topP.oneOf.1", "float32"),
            ],
            numberDefinitions.OrderBy(static item => item.Name, StringComparer.Ordinal));

        using (var chatDescriptor = CanonicalDescriptorInventory.Document("younics.ai.agent.chat.provider"))
        {
            var chatDefinitions = chatDescriptor.RootElement.GetProperty("$defs");
            var chatOptions = chatDefinitions.GetProperty("AgentChatOptions").GetProperty("properties");
            foreach (var option in new[] { "frequencyPenalty", "presencePenalty", "temperature", "topP" })
            {
                Assert.Contains(chatOptions.GetProperty(option).GetProperty("oneOf").EnumerateArray(), branch =>
                    branch.GetProperty("type").GetString() == "number"
                    && branch.GetProperty("format").GetString() == "float32");
            }
            Assert.Contains(chatOptions.GetProperty("continuationHandle").GetProperty("oneOf").EnumerateArray(), branch =>
                branch.GetProperty("type").GetString() == "string"
                && branch.GetProperty("contentEncoding").GetString() == "base64");
            Assert.All(new[] { "ChatOptionValue", "InlineContinuationBytes", "AbsoluteUri", "JSON", "UtilityModelResolution" },
                definitionName => Assert.False(chatDefinitions.TryGetProperty(definitionName, out _), definitionName));
            Assert.Equal(
                "date",
                chatDefinitions.GetProperty("AgentModelDescriptor")
                    .GetProperty("properties")
                    .GetProperty("releaseDate")
                    .GetProperty("oneOf")
                    .EnumerateArray()
                    .Single(static branch => branch.GetProperty("type").GetString() != "null")
                    .GetProperty("format")
                    .GetString());
            Assert.Equal(
                ["#/$defs/AgentChatUpdateEvent", "#/$defs/AgentChatErrorEvent"],
                chatDefinitions.GetProperty("AgentChatStreamEvent")
                    .GetProperty("oneOf")
                    .EnumerateArray()
                    .Select(static branch => branch.GetProperty("$ref").GetString()));
            Assert.Equal(
                "date-time",
                chatDefinitions.GetProperty("AgentChatUpdateEvent")
                    .GetProperty("properties")
                    .GetProperty("createdAt")
                    .GetProperty("oneOf")
                    .EnumerateArray()
                    .Single(static branch => branch.GetProperty("type").GetString() != "null")
                    .GetProperty("format")
                    .GetString());
            Assert.Equal(
                "#/$defs/AgentChatProviderFailureKind",
                chatDefinitions.GetProperty("AgentChatErrorEvent")
                    .GetProperty("properties")
                    .GetProperty("errorFailureKind")
                    .GetProperty("$ref")
                    .GetString());
        }

        using (var ownerDescriptor = CanonicalDescriptorInventory.Document("younics.ai.agent.owner.runtime"))
        {
            var ownerRoot = ownerDescriptor.RootElement;
            Assert.Equal("sunder.rpc.values.v1", ownerRoot.GetProperty("schemaProfile").GetString());
            Assert.Equal("2.0.0", ownerRoot.GetProperty("version").GetString());
            var ownerDefinitions = ownerRoot.GetProperty("$defs");
            var date = Assert.Single(ownerDefinitions.GetProperty("AgentModelDescriptor").GetProperty("properties")
                .GetProperty("releaseDate").GetProperty("oneOf").EnumerateArray(), value => value.GetProperty("type").GetString() != "null");
            Assert.Equal("string", date.GetProperty("type").GetString());
            Assert.Equal("date", date.GetProperty("format").GetString());
            Assert.Equal(
                "content-reference",
                ownerDefinitions.GetProperty("AgentAttachmentContentUploadRequest").GetProperty("properties")
                    .GetProperty("content").GetProperty("logicalType").GetString());
            Assert.Contains(ownerDefinitions.GetProperty("AgentProfileRecord").GetProperty("properties")
                .GetProperty("behaviorLoopSettingsJson").GetProperty("oneOf").EnumerateArray(),
                value => value.GetProperty("type").GetString() == "string");
            Assert.False(ownerDefinitions.TryGetProperty("AgentAttachmentTransferRequest", out _));
            Assert.False(ownerDefinitions.TryGetProperty("DateOnly", out _));
            Assert.False(ownerDefinitions.TryGetProperty("DayOfWeek", out _));
        }

        using (var runControlDescriptor = CanonicalDescriptorInventory.Document("younics.ai.agent.run.control"))
        {
            var definitions = runControlDescriptor.RootElement.GetProperty("$defs");
            using var chatDescriptor = CanonicalDescriptorInventory.Document("younics.ai.agent.chat.provider");
            Assert.Equal(
                JsonSerializer.Serialize(chatDescriptor.RootElement.GetProperty("$defs").GetProperty("AgentChatOptions")),
                JsonSerializer.Serialize(definitions.GetProperty("AgentChatOptions")));
            Assert.Equal(2, definitions.GetProperty("AgentChatStreamEvent").GetProperty("oneOf").GetArrayLength());
            Assert.Equal(
                "#/$defs/AgentReasoningEffort",
                definitions.GetProperty("AgentModelVariantDescriptor")
                    .GetProperty("properties")
                    .GetProperty("reasoningEffort")
                    .GetProperty("oneOf")
                    .EnumerateArray()
                    .Single(static branch => branch.TryGetProperty("$ref", out _))
                    .GetProperty("$ref")
                    .GetString());
        }

        using var roles = new CanonicalPackageRoles();
        var assembly = roles.Load("Memory.Semantic", "worker");
        Assert.Equal(
            typeof(IReadOnlyList<double>),
            typeof(Sunder.Package.Agent.Contracts.Models.AgentEmbeddingGenerationResult)
                .GetProperty("Values")!.PropertyType);
        Assert.Equal(
            typeof(IReadOnlyList<double>),
            assembly.GetType("Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentEmbeddingGenerationResult", true)!
                .GetProperty("Values")!.PropertyType);
        Assert.Equal(
            typeof(float?),
            assembly.GetType("Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentChatOptions", true)!
                .GetProperty("Temperature")!.PropertyType);
        Assert.Equal(
            typeof(byte[]),
            assembly.GetType("Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentChatOptions", true)!
                .GetProperty("ContinuationHandle")!.PropertyType);
        Assert.Equal(
            typeof(SunderRpcOffsetDateTime),
            assembly.GetType("Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTurnRecord", true)!
                .GetProperty("UpdatedAtUtc")!.PropertyType);
        Assert.Equal(
            typeof(SunderRpcOffsetDateTime?),
            typeof(AgentChatUpdateEvent).GetProperty(nameof(AgentChatUpdateEvent.CreatedAt))!.PropertyType);
        Assert.Equal(
            typeof(SunderRpcOptional<JsonElement>),
            typeof(AgentChatContent).GetProperty(nameof(AgentChatContent.Json))!.PropertyType);
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Protocol.AgentRpcOptional`1"));
        Assert.Null(typeof(AgentChatContent).Assembly.GetType("Sunder.Package.Agent.Protocol.AgentRpcOptional`1"));
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
        };
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
        using var toolSource = CanonicalDescriptorInventory.Document("younics.ai.agent.tool.source");
        using var observedExecution = CanonicalDescriptorInventory.Document("younics.ai.agent.tool.execution.observed");
        var toolSourceDefinitions = toolSource.RootElement.GetProperty("$defs");
        var observedDefinitions = observedExecution.RootElement.GetProperty("$defs");

        var sharedDefinitions = new[]
        {
            "AgentResourceClaim",
            "AgentResourceOperationContext",
            "AgentToolExecutionContext",
            "AgentToolRequest",
            "AgentToolResult",
            "AgentToolSourceItem",
            "AgentWorkspaceBindingRecord",
            "AgentWorkspaceDocumentRecord",
            "AgentWorkspacePathRecord",
            "AgentWorkspaceRecord",
            "CYounics_Ai_Agent_Execution_Target_ExecutionTarget_ServiceReference",
            "Unit",
        };
        Assert.Equal(
            sharedDefinitions,
            toolSourceDefinitions.EnumerateObject().Select(static definition => definition.Name)
                .Intersect(observedDefinitions.EnumerateObject().Select(static definition => definition.Name), StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
        foreach (var definitionName in sharedDefinitions)
        {
            Assert.Equal(
                JsonSerializer.Serialize(toolSourceDefinitions.GetProperty(definitionName)),
                JsonSerializer.Serialize(observedDefinitions.GetProperty(definitionName)));
        }
    }

    [Fact]
    public void SemanticSources_AreAgentOwnedAndRoleLocal()
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        foreach (var contract in CurrentContractInventory.Contracts.Where(value => value.Exposure == "public"))
        {
            Assert.StartsWith("src/Sunder.Package.Agent/", contract.Source, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(root, contract.Source)), contract.Source);
        }
        foreach (var name in new[] { "IAgentChatProvider", "IAgentPermissionSurface", "IAgentSystemPromptContributor", "IAgentUtilityModelProvider", "IAgentWorkspaceExecutionResolver" })
            Assert.True(File.Exists(Path.Combine(root, "tests/Sunder.Package.Agent.Tests/LocalProviderFakes", name + ".cs")));

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
        var avaloniaVersion = Assert.Single(avaloniaVersions);
        Assert.Null(avaloniaVersion.Attribute("Condition"));
        // Product and headless tests now share one patched Avalonia release; keep
        // its theme and adapter aligned instead of preserving the old test-only split.
        foreach (var package in new[] { "Avalonia.Themes.Fluent", "Avalonia.Headless.XUnit" })
            Assert.Equal(avaloniaVersion.Attribute("Version")?.Value,
                Assert.Single(packageProperties.Descendants("PackageVersion"), reference =>
                    reference.Attribute("Include")?.Value == package).Attribute("Version")?.Value);
        var presentationTestProject = XDocument.Load(Path.Combine(
            repositoryRoot,
            "tests",
            "Sunder.Package.Agent.Presentation.Tests",
            "Sunder.Package.Agent.Presentation.Tests.csproj"));
        Assert.Equal("younics.ai.agent", GetSingleProperty(presentationTestProject, "SunderPackageFamilyId"));
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
            Assert.DoesNotContain("SunderPackageDependency", File.ReadAllText(package.MetadataPath), StringComparison.Ordinal);
            using var currentLock = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(package.DirectoryPath, "sunder-contracts.lock.json")));
            var currentOwner = currentLock.RootElement.GetProperty("packageArtifacts").GetProperty("younics.ai.agent");
            Assert.Equal(">=2.0.0 <3.0.0", currentOwner.GetProperty("requestedVersionRange").GetString());
            Assert.Equal("2.0.0", currentOwner.GetProperty("selectedPackageVersion").GetString());
            if (package.Name == "Sunder.Package.Agent.Builder")
            {
                var import = currentLock.RootElement.GetProperty("packageImports").GetProperty("younics.ai.agent");
                Assert.Equal("optional", import.GetProperty("presence").GetString());
                Assert.Empty(import.GetProperty("requiredContracts").EnumerateArray());
            }
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
            source += File.ReadAllText(Path.Combine(repositoryRoot, "scripts/ci/agent_ci.py"));
            if (workflow == "sunder-package-release.yml")
            {
                Assert.Contains("scripts/release/build-native-family.ps1", source, StringComparison.Ordinal);
                source += File.ReadAllText(Path.Combine(repositoryRoot, "scripts/release/build-native-family.ps1"));
                source += File.ReadAllText(Path.Combine(repositoryRoot, "scripts/release/native-family.py"));
            }
            Assert.Contains("SunderAgentVersion", source, StringComparison.Ordinal);
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
            "Sunder.Package.Agent.Memory.Semantic/App/MemoryAppRuntimeGateway.cs",
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

    private static string GeneratedBindingNamespace(string projectDirectory, string contractId)
    {
        var owner = CurrentContractInventory.Owner(CurrentContractInventory.Find(contractId).PackageId);
        Assert.Equal(Path.Combine(RegressionTestRpcDescriptors.RepositoryRoot, owner.SourceDirectory), projectDirectory);
        var packageStem = string.Join("_", owner.PackageId
            .Split('.').Select(static segment => char.ToUpperInvariant(segment[0]) + segment[1..]));
        var contractStem = string.Join("_", contractId.Split('.').Select(static segment => char.ToUpperInvariant(segment[0]) + segment[1..]));
        return $"Sunder.Generated.Packages.{packageStem}_Internal.C{contractStem}";
    }

    private static void CollectNumberDefinitions(JsonElement value, string path, ICollection<(string Name, string Format)> numbers)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("type", out var type) && type.GetString() == "number")
                numbers.Add((path, value.GetProperty("format").GetString()!));
            foreach (var property in value.EnumerateObject())
                CollectNumberDefinitions(property.Value, path + "." + property.Name, numbers);
            return;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                CollectNumberDefinitions(item, path + "." + index++, numbers);
        }
    }

    private static string ResolveTargetFramework()
        => new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;

    private static string ResolveConfiguration()
        => new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent?.Name
           ?? "Debug";

    [GeneratedRegex("SunderRpcErrorKind\\.Domain,\\s*\"(?<code>[a-z0-9.-]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex DomainErrorPattern();

    [GeneratedRegex("SunderServiceError\\(\"(?<code>[a-z0-9.-]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex DomainErrorDeclarationPattern();

    [GeneratedRegex(@"\basync\s+void\s+(?<method>[A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex AsyncVoidMethodPattern();

    [GeneratedRegex(@"\[RelayCommand(?:\([^\]]*\))?\]\s*(?:private|public|internal)\s+(?:static\s+)?(?:async\s+)?(?:Task|ValueTask)(?:<[^;{]+>)?\s+(?<method>[A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex AsyncRelayCommandPattern();
}
