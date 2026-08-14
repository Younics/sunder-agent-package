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

        Assert.Equal(18, checkedIn.Length);
        Assert.False(
            Directory.Exists(authoredGeneratedRoot)
            && Directory.EnumerateFiles(authoredGeneratedRoot, "*.g.cs", SearchOption.TopDirectoryOnly).Any(),
            "Generated RPC bindings must remain build outputs rather than checked-in sources.");
        Assert.Equal(18, Directory.EnumerateFiles(generatedRoot, "*.g.cs", SearchOption.TopDirectoryOnly).Count());
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

        Assert.Equal(30, bindings.Length);
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
                "agent.chat.option-not-representable",
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
            ["sunder.agent.behavior.loop"] = ("a59b9f5c69b610ac74387f779e7624f768d5ad769fed21b6318bb5c4d355c26d", "ae2778dcd1f15aba946296b257cda0088a7a78b0e33ac02bb59a29c9d5d85a3e"),
            ["sunder.agent.chat.provider"] = ("60baacad95b6e1fdaa98471bd922a5ae096f76e9f4cc521c6e90da99f9e92c38", "c676ca29e51b87c50b88f5bc332f0248500a5deae04ec56007e1d997f3e56b0d"),
            ["sunder.agent.child.run.executor"] = ("2a1f2be2c637a944cec2c2a51d74d6329027fd2618ef1152dce6eac11f33e0be", "aa97c4ace5c518c179b04491ed60cb8a19f5aa42298dd83dea6b062ad813c93a"),
            ["sunder.agent.durable.lifecycle.observer"] = ("e4e95dd636d52fa95cfad072a6538e642ef0fea016ca38b2d9112ceb7edec4ea", "2e6349363ea9be84742391736ce183e5d82b252d5fe84f603e4553971510091c"),
            ["sunder.agent.embedding.provider"] = ("a7c209b05f30e5948c5ab468a8c48e77596da746ec8d4884224a96523544f2db", "ccd09867b80d9930f77fa218a3f5795d6f526e8cf68fa0319c4e0c9d8ad462ed"),
            ["sunder.agent.execution.target"] = ("bf02924316c7c1821698b4e704727024a699465a18ca44791197ff914d382a10", "790498cadd6c8cd72627000ec8dcc53a99075e73fe0bfcd81cc564089c0a5d1e"),
            ["sunder.agent.permission.surface"] = ("45c4159e1c5583255c6514a70c0fd2d93ee0998355d25e7d5f368b215c65adb3", "c99a79188268b47a264825b189a099f21e33218e5128854050e98dbf9e858a01"),
            ["sunder.agent.profile.capability.consumer"] = ("7df99ab4e60717958cb93df05c6969bb38eadba210c70d8c05f2ea3467d86d31", "3eb7a4a5f3b1ba3cda247e2ff6f0c412372775922cffd8bcb11da2b47df3fb01"),
            ["sunder.agent.prompt.context.contributor"] = ("82bbdcf62f0f41abe6f9198780d9079ebf2378ffa4babea1bb79f236bbe1c93d", "2250afb3fe2790dadd1729dcbefea22236f2dd39efc2643b50958e67b5f56fbb"),
            ["sunder.agent.run.control"] = ("7328d27e38017eb4c93c47a83439c80252ac248a27241a9d7d69cd530f7e66b2", "dfe0e0393f2679f1ad1764e02990f7fa791e923a525e7c0d44b276a1334b0678"),
            ["sunder.agent.runtime.catalog"] = ("35eb402780fc906436a8bd42e3ceb280b1a1b1e15d0d72e838e6149cb908d785", "7d87d46307a134dcf73198714e872441cd6de8e244ec0db7f91c5b9dbeb6f12d"),
            ["sunder.agent.selectable.capability.provider"] = ("209b51f15f3bded33f2cfcbd0c66d5e6f34730189237cb01b95c6225177dbe9c", "af2e673333f63f21df8cd3021038415768311e51abb2db802be6dc21c0b10acb"),
            ["sunder.agent.session.cleaner"] = ("d594e3e9c421359b83ab376f6ce0c4a1179425a47cf58d9ad1ca542713e3928f", "cff0db58986b3289fed6a5064b9137c8a4cb6fc6c98cf8f19451f4f8fe0c9902"),
            ["sunder.agent.system.prompt.contributor"] = ("2c5a6803d11571525fbe3d857d854c50a0b0ddd054a21e197daa90ecadda12f6", "66ea76798e76d7fa42e11790b392ce388386ed2a7cafd81f29ca5f51ba4ce7ea"),
            ["sunder.agent.tool.source"] = ("64461366daa9a30248363bac6e839d9a4cd4189a6174543e5dc53c6576eabafe", "43340a8ffecaf48a158eb6fe51a1d0da4c14dbdd3ee54b11e2b6aaa0e19e355b"),
            ["sunder.agent.workspace.editor"] = ("6930eb83a6d273525427d55159d247385703cc0c781b52c59c6df83a0126c3f9", "1ba5e3ba7a87644edac5f7c32825d147abd1894201dd6c11659dc7ccc1d2a487"),
            ["sunder.agent.workspace.execution.resolver"] = ("e2f10f19528d5e401e299eb4b7669f2026c8d3356b5b0f114024d9999e64cc1f", "3da28f1ff95f633f41ece72c2f161f305c059893322aae4e7c8cc1b0cce056b0"),
            ["sunder.agent.workspace.path.migrator"] = ("ea153d15e7bef4700efcd0c7fe64741195d2bc216cc7a3b2cc895c61e18289d0", "cc89ecad6fb96beeac5809d5b1202ce94979dc71b2d155374f6eb9635b28d648"),
        };
        Assert.Equal(18, descriptors.Length);
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
    public void AgentRpcLogicalTypesAndBinary64Schemas_AreExplicit()
    {
        var contractsRoot = Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "src",
            "Sunder.Package.Agent.Contracts",
            "Contracts");
        var logicalDefinitions = new List<(string Name, string Type, string LogicalType)>();
        var numberDefinitions = new List<string>();
        foreach (var path in Directory.EnumerateFiles(contractsRoot, "*.rpc.json", SearchOption.TopDirectoryOnly))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var contractName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
            foreach (var definition in document.RootElement.GetProperty("$defs").EnumerateObject())
            {
                if (!definition.Value.TryGetProperty("type", out var typeElement))
                {
                    continue;
                }
                var type = typeElement.GetString()!;
                if (definition.Value.TryGetProperty("logicalType", out var logicalType))
                {
                    logicalDefinitions.Add(($"{contractName}:{definition.Name}", type, logicalType.GetString()!));
                }
                if (type == "number")
                {
                    numberDefinitions.Add($"{contractName}:{definition.Name}");
                }
            }
        }

        string[] expectedUtcDateTimes =
        [
            "behavior-loop:DateTimeOffset",
            "chat-provider:Timestamp",
            "child-run-executor:DateTimeOffset",
            "durable-lifecycle-observer:DateTimeOffset",
            "execution-target:DateTimeOffset",
            "prompt-context-contributor:DateTimeOffset",
            "run-control:DateTimeOffset",
            "run-control:Nullable8",
            "runtime-catalog:DateTimeOffset",
            "selectable-capability-provider:DateTimeOffset",
            "system-prompt-contributor:DateTimeOffset",
            "tool-source:DateTimeOffset",
            "workspace-editor:DateTimeOffset",
            "workspace-execution-resolver:DateTimeOffset",
            "workspace-path-migrator:DateTimeOffset",
        ];
        string[] expectedUuids =
        [
            "behavior-loop:Guid",
            "behavior-loop:Nullable3",
            "child-run-executor:Guid",
            "durable-lifecycle-observer:Guid",
            "durable-lifecycle-observer:Nullable2",
            "execution-target:Guid",
            "execution-target:Nullable",
            "prompt-context-contributor:Guid",
            "prompt-context-contributor:Nullable",
            "run-control:Guid",
            "run-control:Nullable",
            "runtime-catalog:Guid",
            "runtime-catalog:Nullable",
            "session-cleaner:Guid",
            "system-prompt-contributor:Guid",
            "system-prompt-contributor:Nullable2",
            "tool-source:Guid",
            "tool-source:Nullable",
            "workspace-execution-resolver:Guid",
        ];
        Assert.Equal(
            expectedUtcDateTimes,
            logicalDefinitions.Where(static item => item.LogicalType == "utc-date-time")
                .Select(static item => item.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            expectedUuids,
            logicalDefinitions.Where(static item => item.LogicalType == "uuid")
                .Select(static item => item.Name)
                .Order(StringComparer.Ordinal));
        Assert.All(
            logicalDefinitions.Where(static item => item.LogicalType is "utc-date-time" or "uuid"),
            static item => Assert.Equal("string", item.Type));
        Assert.Equal(
            [
                "chat-provider:OptionNumber",
                "embedding-provider:Single",
                "run-control:Nullable7",
                "run-control:Single",
            ],
            numberDefinitions.Order(StringComparer.Ordinal));

        using (var chatDescriptor = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
                   contractsRoot,
                   "chat-provider.rpc.json"))))
        {
            const string optionError = "agent.chat.option-not-representable";
            var chatRoot = chatDescriptor.RootElement;
            var chatDefinitions = chatRoot.GetProperty("$defs");
            var date = chatDefinitions.GetProperty("Date");
            Assert.Equal(10, date.GetProperty("minLength").GetInt32());
            Assert.Equal(10, date.GetProperty("maxLength").GetInt32());
            Assert.Contains(optionError, chatDefinitions.GetProperty("OptionNumber").GetProperty("description").GetString(), StringComparison.Ordinal);
            Assert.True(chatRoot.GetProperty("errors").TryGetProperty(optionError, out _));
            Assert.Equal(
                [optionError],
                chatRoot.GetProperty("services")
                    .GetProperty("chat-provider")
                    .GetProperty("methods")
                    .GetProperty("stream-chat")
                    .GetProperty("errors")
                    .EnumerateArray()
                    .Select(static error => error.GetString()));
            Assert.Equal(
                "#/$defs/Date",
                chatDefinitions.GetProperty("AgentModelDescriptor")
                    .GetProperty("properties")
                    .GetProperty("releaseDate")
                    .GetProperty("$ref")
                    .GetString());
            Assert.Equal(
                "#/$defs/Timestamp",
                chatDefinitions.GetProperty("AgentChatStreamEvent")
                    .GetProperty("properties")
                    .GetProperty("createdAt")
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
            var ownerDefinitions = ownerDescriptor.RootElement.GetProperty("$defs");
            var date = ownerDefinitions.GetProperty("Date");
            Assert.Equal("string", date.GetProperty("type").GetString());
            Assert.Equal(10, date.GetProperty("minLength").GetInt32());
            Assert.Equal(10, date.GetProperty("maxLength").GetInt32());
            Assert.Equal(
                "#/$defs/Date",
                ownerDefinitions.GetProperty("AgentModelDescriptor")
                    .GetProperty("properties")
                    .GetProperty("releaseDate")
                    .GetProperty("$ref")
                    .GetString());
            Assert.False(ownerDefinitions.TryGetProperty("DateOnly", out _));
            Assert.False(ownerDefinitions.TryGetProperty("DayOfWeek", out _));
        }

        using (var runControlDescriptor = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
                   contractsRoot,
                   "run-control.rpc.json"))))
        {
            const string optionError = "agent.chat.option-not-representable";
            var runControlRoot = runControlDescriptor.RootElement;
            Assert.Contains(optionError, runControlRoot.GetProperty("$defs").GetProperty("Nullable7").GetProperty("description").GetString(), StringComparison.Ordinal);
            Assert.True(runControlRoot.GetProperty("errors").TryGetProperty(optionError, out _));
            Assert.Equal(
                [optionError],
                runControlRoot.GetProperty("services")
                    .GetProperty("run-control")
                    .GetProperty("methods")
                    .GetProperty("stream-chat")
                    .GetProperty("errors")
                    .EnumerateArray()
                    .Select(static error => error.GetString()));
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
            typeof(double?),
            assembly.GetType("Sunder.Package.Agent.Protocol.Generated.ChatProvider.AgentChatOptions")!
                .GetProperty("Temperature")!.PropertyType);
        Assert.Equal(
            typeof(Guid?),
            assembly.GetType("Sunder.Package.Agent.Protocol.Generated.RunControl.AgentSessionRecord")!
                .GetProperty("ParentRunId")!.PropertyType);
        Assert.Equal(
            typeof(string),
            assembly.GetType("Sunder.Package.Agent.Protocol.Generated.RunControl.AgentTurnRecord")!
                .GetProperty("UpdatedAtUtc")!.PropertyType);
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
            if (project.Descendants("SunderPackageAggregateProject")
                .Any(static property => string.Equals(property.Value, "true", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
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
}
