using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    private static IReadOnlyDictionary<string, NaturalServiceHistoryEntry> ReadNaturalServiceHistories()
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        var text = ReadNormalizedProjectText(Path.Combine(root,
            "tests/Sunder.Package.Agent.Tests/Fixtures/NaturalServices.source-history.json"));
        AssertProjectHash("fa5493a891d2936b9aa0cc5a50ee596babb39c38eb3c80db85d1d1fdeb20d110", text,
            "NaturalServices.source-history.json");
        var history = JsonSerializer.Deserialize<NaturalServiceHistory>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, history.SchemaVersion);
        Assert.Equal(12, history.Sources.Length);
        Assert.All(history.Sources, source =>
        {
            Assert.True(source.Path.StartsWith("src/Sunder.Package.Agent/", StringComparison.Ordinal)
                || source.Path == "src/Sunder.Package.Agent.Builder/Runtime/BuilderRpcContracts.cs"
                || source.Path == "src/Sunder.Package.Agent.Builder/Runtime/BuilderRuntimeOperations.cs"
                || source.Path == "src/Sunder.Package.Agent.Builder/App/Runtime/BuilderRpcExecutionTargetProxy.cs"
                || source.Path == ProviderChatModelsSource,
                $"Unreviewed natural-service history path: {source.Path}");
            Assert.Matches("^[0-9a-f]{40}$", source.HeadBlob);
            Assert.Matches("^[0-9a-f]{64}$", source.SourceSha256);
            Assert.Equal("git-head-blob", source.Provenance);
            Assert.NotEmpty(source.TextChunks);
        });
        var retiredText = ReadNormalizedProjectText(Path.Combine(root,
            "tests/Sunder.Package.Agent.Tests/Fixtures/NaturalServices.retired-candidate-sources.json"));
        AssertProjectHash("c87ee5e24b2ac5edbe2d1c4fab1e4553c0c7ec2e5109c29dfff9c5a721620f49", retiredText,
            "NaturalServices.retired-candidate-sources.json");
        var retired = JsonSerializer.Deserialize<NaturalServiceHistory>(retiredText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, retired.SchemaVersion);
        Assert.Equal(153, retired.Sources.Length);
        AssertProjectHash("3c80ff287c107df2fbd0e4910b2820eb144939b25ed3de7169f2cb69ac536f02",
            string.Join("\n", retired.Sources.Select(source => source.Path).Order(StringComparer.Ordinal)) + "\n",
            "retired natural-service candidate path inventory");
        var adaptersText = ReadNormalizedProjectText(Path.Combine(root,
            "tests/Sunder.Package.Agent.Tests/Fixtures/NaturalServices.retired-adapter-sources.json"));
        AssertProjectHash("4c4af3ebf09109901e7002b921cab426557e2f6586d0987de1d2977ca9c2396e", adaptersText,
            "NaturalServices.retired-adapter-sources.json");
        var adapters = JsonSerializer.Deserialize<NaturalServiceHistory>(adaptersText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, adapters.SchemaVersion);
        Assert.Equal(new[]
        {
            "src/Sunder.Package.Agent.Mcp/Runtime/McpManagementRpcProvider.cs",
            "src/Sunder.Package.Agent.Memory.Semantic/Runtime/MemoryRuntimeBridge.cs",
            "src/Sunder.Package.Agent.Provider.OpenAI/Management/OpenAiAppManagement.cs",
            "src/Sunder.Package.Agent.Subagents/Services/SubagentCatalogRpcInvocation.cs",
            "src/Sunder.Package.Agent/Runtime/DeferredAgentRpcServiceHandler.cs",
        }, adapters.Sources.Select(source => source.Path).Order(StringComparer.Ordinal));
        Assert.All(retired.Sources.Concat(adapters.Sources), source =>
        {
            Assert.StartsWith("src/Sunder.Package.Agent", source.Path, StringComparison.Ordinal);
            Assert.EndsWith(".cs", source.Path, StringComparison.Ordinal);
            Assert.Matches("^[0-9a-f]{64}$", source.SourceSha256);
            Assert.NotEmpty(source.TextChunks);
            if (source.Provenance == "git-index-blob")
            {
                Assert.Matches("^[0-9a-f]{40}$", source.IndexBlob);
                Assert.Null(source.HeadBlob);
            }
            else
            {
                Assert.Equal("git-head-blob", source.Provenance);
                Assert.Matches("^[0-9a-f]{40}$", source.HeadBlob);
                Assert.Null(source.IndexBlob);
            }
        });
        return history.Sources.Concat(retired.Sources).Concat(adapters.Sources).ToDictionary(source => source.Path, StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, NaturalServiceDeltaEntry> ReadNaturalServiceDeltas()
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        var text = ReadNormalizedProjectText(Path.Combine(root,
            "tests/Sunder.Package.Agent.Tests/Fixtures/NaturalServices.current-source-deltas.json"));
        AssertProjectHash("a75a5e8c583fa4186e00476cafb62d96dc6a69d4589244680148ff2c44b6afe6", text,
            "NaturalServices.current-source-deltas.json");
        var history = JsonSerializer.Deserialize<NaturalServiceDeltaHistory>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, history.SchemaVersion);
        Assert.Equal(60, history.Sources.Length);

        // The provider/source-model split changed all canonical Compile inputs.
        // Preserve the independently pinned canonical phase and guard the exact
        // reviewed current projects, rather than replacing its historical hashes.
        var projectsText = ReadNormalizedProjectText(Path.Combine(root,
            "tests/Sunder.Package.Agent.Tests/Fixtures/NaturalServices.canonical-project-deltas.json"));
        AssertProjectHash("8da734f8e96bbb6a9d0f6e827c4015dfe04024bde4942151251ae685fc4c47f7", projectsText,
            "NaturalServices.canonical-project-deltas.json");
        var projects = JsonSerializer.Deserialize<NaturalServiceDeltaHistory>(projectsText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, projects.SchemaVersion);
        Assert.Equal(15, projects.Sources.Length);
        Assert.All(projects.Sources, source =>
        {
            var name = Path.GetFileNameWithoutExtension(source.Path);
            Assert.StartsWith("Sunder.Package.Agent", name, StringComparison.Ordinal);
            Assert.Equal($"src/{name}/{name}.csproj", source.Path);
        });
        var retainedText = ReadNormalizedProjectText(Path.Combine(root,
            "tests/Sunder.Package.Agent.Tests/Fixtures/NaturalServices.retained-project-deltas.json"));
        AssertProjectHash("16244ddcb9c300bb09fac8847f536160964bde83c0f6b55b53959a3f78d444c7", retainedText,
            "NaturalServices.retained-project-deltas.json");
        var retained = JsonSerializer.Deserialize<NaturalServiceDeltaHistory>(retainedText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, retained.SchemaVersion);
        var retainedSource = Assert.Single(retained.Sources);
        Assert.Equal("src/Sunder.Agent.Execution.Common/Sunder.Agent.Execution.Common.csproj", retainedSource.Path);
        var executionText = ReadNormalizedProjectText(Path.Combine(root,
            "tests/Sunder.Package.Agent.Tests/Fixtures/NaturalServices.execution-model-deltas.json"));
        AssertProjectHash("873aacbf2949e23aee214831fc00f7e66d9b4ee6519dcfc0192dbff69c7f97dc", executionText,
            "NaturalServices.execution-model-deltas.json");
        var execution = JsonSerializer.Deserialize<NaturalServiceDeltaHistory>(executionText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, execution.SchemaVersion);
        Assert.Equal(new[]
        {
            "src/Sunder.Package.Agent.Execution.Docker/Models/DockerExecutionPresentationModels.cs",
            "src/Sunder.Package.Agent.Execution.Local/Models/LocalExecutionPresentationModels.cs",
        }, execution.Sources.Select(source => source.Path).Order(StringComparer.Ordinal));
        var memoryText = ReadNormalizedProjectText(Path.Combine(root,
            "tests/Sunder.Package.Agent.Tests/Fixtures/NaturalServices.memory-model-deltas.json"));
        AssertProjectHash("f76602e842691231af122740b3488002ae26a5ad7e4939a4d6b0c29b3e504129", memoryText,
            "NaturalServices.memory-model-deltas.json");
        var memory = JsonSerializer.Deserialize<NaturalServiceDeltaHistory>(memoryText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, memory.SchemaVersion);
        Assert.Equal(new[]
        {
            "src/Sunder.Package.Agent.Memory.Semantic/Models/MemoryInspectorModels.cs",
            "src/Sunder.Package.Agent.Memory.Semantic/Runtime/MemoryInspectorContracts.cs",
        }, memory.Sources.Select(source => source.Path).Order(StringComparer.Ordinal));
        var skillsText = ReadNormalizedProjectText(Path.Combine(root,
            "tests/Sunder.Package.Agent.Tests/Fixtures/NaturalServices.skills-runtime-deltas.json"));
        AssertProjectHash("f619eaf52ef2050733eccaa180618e2a2b8190ab97218a51cf8225cc2f9a997e", skillsText,
            "NaturalServices.skills-runtime-deltas.json");
        var skills = JsonSerializer.Deserialize<NaturalServiceDeltaHistory>(skillsText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, skills.SchemaVersion);
        Assert.Equal(new[]
        {
            "src/Sunder.Package.Agent.Skills/Runtime/SkillRuntimeAbstractions.cs",
            "src/Sunder.Package.Agent.Skills/Runtime/SkillRuntimeContracts.cs",
            "src/Sunder.Package.Agent.Skills/Runtime/SkillRuntimeGateway.cs",
        }, skills.Sources.Select(source => source.Path).Order(StringComparer.Ordinal));
        var subagentsText = ReadNormalizedProjectText(Path.Combine(root,
            "tests/Sunder.Package.Agent.Tests/Fixtures/NaturalServices.subagents-runtime-deltas.json"));
        AssertProjectHash("25495d3c3dbddfd1592125501683af6045ba21db1eb510ec736d4286c365b092", subagentsText,
            "NaturalServices.subagents-runtime-deltas.json");
        var subagents = JsonSerializer.Deserialize<NaturalServiceDeltaHistory>(subagentsText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, subagents.SchemaVersion);
        Assert.Equal(new[]
        {
            "src/Sunder.Package.Agent.Subagents/Runtime/SubagentOwnerRpc.cs",
            "src/Sunder.Package.Agent.Subagents/Runtime/SubagentRuntimeContracts.cs",
        }, subagents.Sources.Select(source => source.Path).Order(StringComparer.Ordinal));
        var mcpText = ReadNormalizedProjectText(Path.Combine(root,
            "tests/Sunder.Package.Agent.Tests/Fixtures/NaturalServices.mcp-runtime-deltas.json"));
        AssertProjectHash("4af414d414c826509ef1b5e32d77bff4238027e17a6d29ec64d8d2e8a125ff30", mcpText,
            "NaturalServices.mcp-runtime-deltas.json");
        var mcp = JsonSerializer.Deserialize<NaturalServiceDeltaHistory>(mcpText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, mcp.SchemaVersion);
        Assert.Equal(new[]
        {
            "src/Sunder.Package.Agent.Mcp/Runtime/McpManagementContent.cs",
            "src/Sunder.Package.Agent.Mcp/Runtime/McpManagementModels.cs",
        }, mcp.Sources.Select(source => source.Path).Order(StringComparer.Ordinal));
        var openAiText = ReadNormalizedProjectText(Path.Combine(root,
            "tests/Sunder.Package.Agent.Tests/Fixtures/NaturalServices.openai-management-deltas.json"));
        AssertProjectHash("a97dc7d7b219a1081bf77906452078fa9b2f3682bdc6ad12d2def0bd69142bcf", openAiText,
            "NaturalServices.openai-management-deltas.json");
        var openAi = JsonSerializer.Deserialize<NaturalServiceDeltaHistory>(openAiText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, openAi.SchemaVersion);
        Assert.Equal("src/Sunder.Package.Agent.Provider.OpenAI/Management/OpenAiManagementModels.cs", Assert.Single(openAi.Sources).Path);
        var builderText = ReadNormalizedProjectText(Path.Combine(root,
            "tests/Sunder.Package.Agent.Tests/Fixtures/NaturalServices.builder-workspace-deltas.json"));
        AssertProjectHash("e5c214cf693b8fba9251e30d252975da22f9002d78ab5c381b1a028b4d07e5af", builderText,
            "NaturalServices.builder-workspace-deltas.json");
        var builder = JsonSerializer.Deserialize<NaturalServiceDeltaHistory>(builderText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, builder.SchemaVersion);
        Assert.Equal("src/Sunder.Package.Agent.Builder/BuilderWorkspaceExecutionService.cs", Assert.Single(builder.Sources).Path);
        var filesText = ReadNormalizedProjectText(Path.Combine(root,
            "tests/Sunder.Package.Agent.Tests/Fixtures/NaturalServices.files-tool-deltas.json"));
        AssertProjectHash("daa5fee0ac1a82acdd84444aa5fdc0291a6b84d2e666b1b14dcfd675701f2489", filesText,
            "NaturalServices.files-tool-deltas.json");
        var files = JsonSerializer.Deserialize<NaturalServiceDeltaHistory>(filesText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, files.SchemaVersion);
        Assert.Equal("src/Sunder.Package.Agent.Tools.Files/FilesToolSource.cs", Assert.Single(files.Sources).Path);
        var shellText = ReadNormalizedProjectText(Path.Combine(root,
            "tests/Sunder.Package.Agent.Tests/Fixtures/NaturalServices.shell-tool-deltas.json"));
        AssertProjectHash("ae13d67cc2168cb2c8115ec99a62443843e6ff2c678d48de7634500efda9c3e4", shellText,
            "NaturalServices.shell-tool-deltas.json");
        var shell = JsonSerializer.Deserialize<NaturalServiceDeltaHistory>(shellText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, shell.SchemaVersion);
        Assert.Equal("src/Sunder.Package.Agent.Tools.Shell/ShellToolSource.cs", Assert.Single(shell.Sources).Path);
        var subagentConsumersText = ReadNormalizedProjectText(Path.Combine(root,
            "tests/Sunder.Package.Agent.Tests/Fixtures/NaturalServices.subagents-consumer-deltas.json"));
        AssertProjectHash("94e4173decb4cd2c5956f81c0cb6eab7fadffcd91acf7147c6e1228cad582304", subagentConsumersText,
            "NaturalServices.subagents-consumer-deltas.json");
        var subagentConsumers = JsonSerializer.Deserialize<NaturalServiceDeltaHistory>(subagentConsumersText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, subagentConsumers.SchemaVersion);
        Assert.Equal(new[]
        {
            "Runtime/SubagentRuntimeBridge.cs",
            "Runtime/SubagentRuntimeChangeStream.cs",
            "Services/SubagentChildRunCoordinator.cs",
            "Services/SubagentDescriptorSchema.cs",
            "Services/SubagentEditorCapabilityCatalog.cs",
            "Services/SubagentFeature.cs",
            "Services/SubagentPermissionStatusAdapter.cs",
            "Services/SubagentStackContributor.cs",
        }.Select(path => "src/Sunder.Package.Agent.Subagents/" + path),
            subagentConsumers.Sources.Select(source => source.Path).Order(StringComparer.Ordinal));
        var familyConsumersText = ReadNormalizedProjectText(Path.Combine(root,
            "tests/Sunder.Package.Agent.Tests/Fixtures/NaturalServices.family-consumer-deltas.json"));
        AssertProjectHash("8f1887b6dad663b851b8bfff2acfdfe7a6a6b55989fedb842eb812e636318c45", familyConsumersText,
            "NaturalServices.family-consumer-deltas.json");
        var familyConsumers = JsonSerializer.Deserialize<NaturalServiceDeltaHistory>(familyConsumersText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, familyConsumers.SchemaVersion);
        Assert.Equal(new[]
        {
            "src/Sunder.Package.Agent.Mcp/Services/McpSunderConfigurationSyncService.cs",
            "src/Sunder.Package.Agent.Memory.Semantic/Services/SemanticModelRuntimeResolver.cs",
            "src/Sunder.Package.Agent.Skills/Services/SkillsFeature.cs",
        }, familyConsumers.Sources.Select(source => source.Path).Order(StringComparer.Ordinal));
        var presentationText = ReadNormalizedProjectText(Path.Combine(root,
            "tests/Sunder.Package.Agent.Tests/Fixtures/NaturalServices.presentation-deltas.json"));
        AssertProjectHash("097ea787d0af273fe67543a91a4291beae80fb8d62a4d92304f62f2b4b3dc3c0", presentationText,
            "NaturalServices.presentation-deltas.json");
        var presentation = JsonSerializer.Deserialize<NaturalServiceDeltaHistory>(presentationText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, presentation.SchemaVersion);
        Assert.Equal(new[]
        {
            "src/Sunder.Package.Agent.Shared/PackageViews/TranscriptToolPresentationService.cs",
            "src/Sunder.Package.Agent.Shared/Presentation/ProviderModelLoader.cs",
            "src/Sunder.Package.Agent/Services/AgentToolPresentationService.cs",
        }, presentation.Sources.Select(source => source.Path).Order(StringComparer.Ordinal));
        var adaptersText = ReadNormalizedProjectText(Path.Combine(root,
            "tests/Sunder.Package.Agent.Tests/Fixtures/NaturalServices.adapter-consumer-deltas.json"));
        AssertProjectHash("f74a71424245265bd215d785d74f46afc3ad702ff7fad3f5c2610224c09b15cb", adaptersText,
            "NaturalServices.adapter-consumer-deltas.json");
        var adapters = JsonSerializer.Deserialize<NaturalServiceDeltaHistory>(adaptersText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, adapters.SchemaVersion);
        Assert.Equal(new[]
        {
            "src/Sunder.Package.Agent.Execution.Docker/App/DockerExecutionSettingsViewModel.Runtime.cs",
            "src/Sunder.Package.Agent.Execution.Local/App/LocalExecutionSettingsViewModel.cs",
            "src/Sunder.Package.Agent.Mcp/Runtime/McpAppRpcGateway.cs",
            "src/Sunder.Package.Agent.Memory.Semantic/App/MemoryAppRuntimeGateway.cs",
            "src/Sunder.Package.Agent.Tools.Files/FilePermissionPlanner.cs",
            "src/Sunder.Package.Agent/PackageViews/AgentWorkspaceEditorInvocation.cs",
            "src/Sunder.Package.Agent/PackageViews/AgentWorkspaceEditorViewModels.cs",
            "src/Sunder.Package.Agent/PackageViews/AgentWorkspacesView.axaml.cs",
            "src/Sunder.Package.Agent/PackageViews/AgentWorkspacesViewModel.Editor.cs",
            "src/Sunder.Package.Agent/PackageViews/AgentWorkspacesViewModel.cs",
            "src/Sunder.Package.Agent/Runtime/AgentAttachmentTransferHandler.cs",
            "src/Sunder.Package.Agent/Runtime/AgentRuntimeContracts.cs",
            "src/Sunder.Package.Agent/Runtime/AgentWorkerLifecycle.cs",
            "src/Sunder.Package.Agent/Services/AgentExecutionTargetService.cs",
            "src/Sunder.Package.Agent/Services/AgentLifecycleDispatcher.cs",
            "src/Sunder.Package.Agent/Services/AgentMemoryCoordinator.cs",
            "src/Sunder.Package.Agent/Services/AgentPermissionService.cs",
            "src/Sunder.Package.Agent/Services/AgentRunProviderResolver.cs",
            "src/Sunder.Package.Agent/Services/AgentSessionCleanupDispatcher.cs",
            "src/Sunder.Package.Agent/Services/AgentSessionService.cs",
            "src/Sunder.Package.Agent/Services/AgentSystemPromptComposer.cs",
            "src/Sunder.Package.Agent/Services/AgentToolService.cs",
            "src/Sunder.Package.Agent/Services/AgentWorkspaceService.cs",
            "src/Sunder.Package.Agent/Services/AgentWorkspaceStackContributor.cs",
            "src/Sunder.Package.Agent/Services/BehaviorLoops/AgentBehaviorLoopResolver.cs",
        }, adapters.Sources.Select(source => source.Path).Order(StringComparer.Ordinal));
        var namedText = ReadNormalizedProjectText(Path.Combine(root,
            "tests/Sunder.Package.Agent.Tests/Fixtures/NaturalServices.named-inverse-deltas.json"));
        AssertProjectHash("21d1bdf53c1e2cb8b0b0f34f38cfd8cf8355deb44886e4b4c0952e669bde42f1", namedText,
            "NaturalServices.named-inverse-deltas.json");
        var named = JsonSerializer.Deserialize<NaturalServiceDeltaHistory>(namedText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, named.SchemaVersion);
        Assert.Equal(new[]
        {
            "HistorySearch/HistorySearchIndexingService.cs",
            "Services/AgentAttachmentService.cs",
            "Services/AgentToolService.Catalog.cs",
            "Services/AgentToolService.Invocation.cs",
            "Services/BehaviorLoops/DefaultAgentBehaviorLoop.cs",
            "Services/WorkspaceDocumentationContextService.cs",
        }.Select(path => "src/Sunder.Package.Agent/" + path), named.Sources.Select(source => source.Path).Order(StringComparer.Ordinal));
        var candidatesText = ReadNormalizedProjectText(Path.Combine(root,
            "tests/Sunder.Package.Agent.Tests/Fixtures/NaturalServices.current-candidate-deltas.json"));
        AssertProjectHash("4efc0ff0d06a645649a481d2e44329e54dd50f8d697a740e76c5429eef6afaac", candidatesText,
            "NaturalServices.current-candidate-deltas.json");
        var candidates = JsonSerializer.Deserialize<NaturalServiceDeltaHistory>(candidatesText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, candidates.SchemaVersion);
        Assert.Equal(new[]
        {
            "src/Sunder.Package.Agent.Memory.Semantic/Rpc/AppComposition.cs",
            "src/Sunder.Package.Agent.Tools.Web/Services/WebToolSource.cs",
        }, candidates.Sources.Select(source => source.Path).Order(StringComparer.Ordinal));
        var closureText = ReadNormalizedProjectText(Path.Combine(root,
            "tests/Sunder.Package.Agent.Tests/Fixtures/NaturalServices.source-closure-deltas.json"));
        AssertProjectHash("66e428ec68dabb4a3b5685a1c92335ee668664f6f3b691f8c42b20bbde8d3a1d", closureText,
            "NaturalServices.source-closure-deltas.json");
        var closure = JsonSerializer.Deserialize<NaturalServiceDeltaHistory>(closureText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, closure.SchemaVersion);
        Assert.Equal(26, closure.Sources.Length);
        AssertProjectHash("0388826c62721e6931cfcc07eba3909930c930cbcf59f6ebcfbb9357756d2fb9",
            string.Join("\n", closure.Sources.Select(source => source.Path).Order(StringComparer.Ordinal)) + "\n",
            "pre-natural non-adapter business source inventory");
        var relocatedText = ReadNormalizedProjectText(Path.Combine(root,
            "tests/Sunder.Package.Agent.Tests/Fixtures/NaturalServices.relocated-domain-deltas.json"));
        AssertProjectHash("beb6ac24725d6e4f2f79864d87d5f8db6d0a8ca33a8caf1d294dec0eef5da721", relocatedText,
            "NaturalServices.relocated-domain-deltas.json");
        var relocated = JsonSerializer.Deserialize<NaturalServiceDeltaHistory>(relocatedText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, relocated.SchemaVersion);
        Assert.Equal(new[]
        {
            "src/Sunder.Package.Agent.Subagents/Runtime/TranscriptPageCursor.cs",
            "src/Sunder.Package.Agent/Services/AgentWorkspaceBindingRoles.cs",
        }, relocated.Sources.Select(source => source.Path).Order(StringComparer.Ordinal));
        var sources = history.Sources.Concat(projects.Sources).Concat(retained.Sources).Concat(execution.Sources)
            .Concat(memory.Sources).Concat(skills.Sources).Concat(subagents.Sources).Concat(mcp.Sources)
            .Concat(openAi.Sources).Concat(builder.Sources).Concat(files.Sources).Concat(shell.Sources)
            .Concat(subagentConsumers.Sources).Concat(familyConsumers.Sources).Concat(presentation.Sources)
            .Concat(adapters.Sources).Concat(named.Sources).Concat(candidates.Sources).Concat(closure.Sources)
            .Concat(relocated.Sources).ToArray();
        Assert.All(sources, source =>
        {
            Assert.Equal("git-index-blob", source.Provenance);
            Assert.Matches("^[0-9a-f]{40}$", source.IndexBlob);
            Assert.Matches("^[0-9a-f]{64}$", source.CurrentSha256);
            Assert.Matches("^[0-9a-f]{64}$", source.BeforeSha256);
            Assert.NotEmpty(source.TextChunks);
        });
        return sources.ToDictionary(source => source.Path, StringComparer.Ordinal);
    }

    internal static bool TryReadNaturalServiceHistory(string root, string path, out string text)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (!NaturalServiceHistoryHolder.Value.TryGetValue(relative, out var source))
        {
            text = string.Empty;
            return false;
        }

        Assert.False(File.Exists(path), $"Retired natural-service source was reintroduced: {source.Path}");
        var compressed = Convert.FromBase64String(string.Concat(source.TextChunks));
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        text = Encoding.UTF8.GetString(output.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        AssertProjectHash(source.SourceSha256, text, source.Path + " (captured natural-service Git before-image)");
        return true;
    }

    internal static bool TryReadNaturalServiceDelta(string root, string path, string current, out string text)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        var previousBoundary = BeforeApprovedSourceSplits(relative, current);
        if (!NaturalServiceDeltaHolder.Value.TryGetValue(relative, out var source))
        {
            text = previousBoundary;
            return !string.Equals(current, previousBoundary, StringComparison.Ordinal);
        }

        Assert.Equal("git-index-blob", source.Provenance);
        AssertProjectHash(source.CurrentSha256, previousBoundary, relative + " (post-natural-service current source)");
        var compressed = Convert.FromBase64String(string.Concat(source.TextChunks));
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        text = Encoding.UTF8.GetString(output.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        AssertProjectHash(source.BeforeSha256, text, relative + " (captured staged pre-natural-service source)");
        return true;
    }

    internal static IEnumerable<string> EnumerateNaturalServiceHistoryFiles(string root, string directory, string pattern)
    {
        var prefix = Path.GetRelativePath(root, directory).Replace('\\', '/').TrimEnd('/') + "/";
        return NaturalServiceHistoryHolder.Value.Keys
            .Where(path => path.StartsWith(prefix, StringComparison.Ordinal)
                && System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(path)))
            .Select(path => Path.Combine(root, path));
    }

    private sealed record NaturalServiceHistory(int SchemaVersion, NaturalServiceHistoryEntry[] Sources);
    private sealed record NaturalServiceHistoryEntry(string Path, string? HeadBlob, string SourceSha256, string Provenance,
        string[] TextChunks, string? IndexBlob = null);
    private sealed record NaturalServiceDeltaHistory(int SchemaVersion, NaturalServiceDeltaEntry[] Sources);
    private sealed record NaturalServiceDeltaEntry(string Path, string CurrentSha256, string BeforeSha256, string IndexBlob,
        string Provenance, string[] TextChunks);

    // Nested holders initialize only when a history reader is first invoked, avoiding
    // partial-class static field ordering during eager relocation-map construction.
    private static class NaturalServiceHistoryHolder
    {
        internal static readonly IReadOnlyDictionary<string, NaturalServiceHistoryEntry> Value = ReadNaturalServiceHistories();
    }

    private static class NaturalServiceDeltaHolder
    {
        internal static readonly IReadOnlyDictionary<string, NaturalServiceDeltaEntry> Value = ReadNaturalServiceDeltas();
    }
}
