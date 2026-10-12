using System.Reflection;
using System.Runtime.Loader;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentCatalogSourceMigrationTests
{
    [Theory]
    [InlineData("Provider.Gemini", "ProviderRoleLocalProbes.cs.txt")]
    [InlineData("Provider.Anthropic", "ProviderRoleLocalProbes.cs.txt")]
    [InlineData("Provider.LMStudio", "ProviderRoleLocalProbes.cs.txt")]
    [InlineData("Provider.OpenAI", "ProviderRoleLocalProbes.cs.txt")]
    [InlineData("Execution.Docker", "DockerArtifactRunner.cs.txt")]
    [InlineData("Tools.Web", "WebArtifactBackend.cs.txt")]
    [InlineData("Mcp", null)]
    [InlineData("Memory.Semantic", null)]
    [InlineData("Subagents", null)]
    public async Task ControlledTransportProbe_CompilesOnlyFrozenGeneratedRoleInputs(string family, string? fixture)
    {
        var root = Directory.CreateTempSubdirectory("sunder-generated-probe-compiler-").FullName;
        try
        {
            var source = CanonicalPackageRoles.Source(family);
            var snapshot = Path.Combine(root, family);
            var output = CanonicalPackageRoles.Output(snapshot, "worker");
            Directory.CreateDirectory(output);
            foreach (var path in Directory.EnumerateFiles(CanonicalPackageRoles.Output(source, "worker")))
            {
                var bytes = File.ReadAllBytes(path);
                File.WriteAllBytes(Path.Combine(output, Path.GetFileName(path)), bytes);
                Assert.Equal(bytes, File.ReadAllBytes(path));
            }
            var entry = Path.Combine(CanonicalPackageRoles.Analysis(source), "roles/worker/worker-entry.g.cs");
            var entryBytes = File.ReadAllBytes(entry);
            var destination = Path.Combine(CanonicalPackageRoles.Analysis(snapshot), "roles/worker/worker-entry.g.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, entryBytes);
            Assert.Equal(entryBytes, File.ReadAllBytes(entry));
            if (family.StartsWith("Provider.", StringComparison.Ordinal))
            {
                var app = CanonicalPackageRoles.Output(snapshot, "app");
                Directory.CreateDirectory(app);
                foreach (var path in Directory.EnumerateFiles(CanonicalPackageRoles.Output(source, "app")))
                    File.Copy(path, Path.Combine(app, Path.GetFileName(path)));
                var owner = CanonicalPackageRoles.Output(Path.Combine(root, "Worker"), "worker");
                Directory.CreateDirectory(owner);
                File.Copy(Path.Combine(CanonicalPackageRoles.Output(CanonicalPackageRoles.Source("Agent"), "worker"), "Sunder.Package.Agent.dll"),
                    Path.Combine(owner, "Sunder.Package.Agent.dll"));
                await (family switch
                {
                    "Provider.OpenAI" => AgentSourceArtifactMigrationTests.VerifyOpenAiRoles(snapshot),
                    "Provider.Gemini" => AgentSourceArtifactMigrationTests.VerifyGeminiRoles(snapshot),
                    "Provider.Anthropic" => AgentSourceArtifactMigrationTests.VerifyAnthropicRoles(snapshot),
                    "Provider.LMStudio" => AgentSourceArtifactMigrationTests.VerifyLMStudioRoles(snapshot),
                    _ => throw new InvalidOperationException("Unrecognized provider role probe: " + family),
                });
            }
            else
            {
                using var roles = new CanonicalPackageRoles();
                var runtime = roles.Load(family, "worker", snapshot);
                Assembly? semanticProbes = null;
                string? semanticProbePath = null;
                byte[]? semanticProbeBytes = null;
                if (family == "Memory.Semantic")
                {
                    semanticProbes = await AgentSourceArtifactMigrationTests.BuildRoleProbes(snapshot, runtime,
                        "MemoryRoleLocalProbes.cs.txt", output: output);
                    semanticProbePath = Path.Combine(root, family + "Probes/bin/Debug/net10.0/Sunder.Package.Agent.Memory.Semantic.SemanticProbes.dll");
                    semanticProbeBytes = File.ReadAllBytes(semanticProbePath);
                }
                var probe = await AgentSourceArtifactMigrationTests.BuildRoleProbes(snapshot, runtime, fixture,
                    Directory.GetFiles(output, "*.dll"), assemblyName: "Sunder.Package.Agent.Tests", output: output, controlledComposition: true);
                Assert.NotNull(probe.GetType("Sunder.Tests.Generated.Package.WorkerEntryPoint"));
                Assert.Contains(probe.GetReferencedAssemblies(), reference => reference.Name == runtime.GetName().Name);
                Assert.DoesNotContain(probe.GetReferencedAssemblies(), reference => reference.Name!.EndsWith(".Protocol", StringComparison.Ordinal));
                if (family == "Memory.Semantic")
                {
                    Assert.Equal(semanticProbeBytes, File.ReadAllBytes(semanticProbePath!));
                    await AgentSourceArtifactMigrationTests.ExerciseMemoryConsumers(runtime);
                    var owner = roles.Load("Agent", "worker");
                    await AgentSourceArtifactMigrationTests.VerifyAgentProfileCapabilities(owner, runtime, semanticProbes!);
                    await AgentSourceArtifactMigrationTests.VerifyAgentCleanupConsumers(owner, runtime, semanticProbes!);
                }
                else if (family == "Subagents")
                {
                    await AgentSourceArtifactMigrationTests.VerifySubagentRuntimeConsumers(runtime, roles.Load("Agent", "worker"));
                }
            }
            Assert.Equal(entryBytes, File.ReadAllBytes(entry));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [AgentPublicSourceMigrationFact]
    public void BusinessSources_CompileIntoIsolatedRolesWithoutProtocol()
    {
        using var roles = new CanonicalPackageRoles();
        var assemblies = new List<Assembly>();
        foreach (var (name, family, role) in new[]
        {
            ("ProviderApp", "Provider.Gemini", "app"), ("ProviderWorker", "Provider.Gemini", "worker"),
            ("Files", "Tools.Files", "worker"), ("Shell", "Tools.Shell", "worker"),
            ("BuilderWorker", "Builder", "worker"), ("BuilderApp", "Builder", "app"),
            ("McpWorker", "Mcp", "worker"), ("McpApp", "Mcp", "app"),
            ("SkillsWorker", "Skills", "worker"), ("SkillsApp", "Skills", "app"),
            ("MemoryWorker", "Memory.Semantic", "worker"), ("MemoryApp", "Memory.Semantic", "app"),
            ("SubagentsWorker", "Subagents", "worker"), ("SubagentsApp", "Subagents", "app"),
            ("LocalWorker", "Execution.Local", "worker"), ("LocalApp", "Execution.Local", "app"),
            ("DockerWorker", "Execution.Docker", "worker"), ("DockerApp", "Execution.Docker", "app"),
            ("AgentApp", "Agent", "app"), ("AgentHelpers", "Agent", "worker"),
        })
        {
            var assembly = roles.Load(family, role);
            assemblies.Add(assembly);
            switch (name)
            {
                case "ProviderApp":
                    Assert.Same(assembly, assembly.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Provider_Gemini_Services.ICredentialSettingsService", true)!.Assembly);
                    break;
                case "ProviderWorker":
                    Assert.Same(assembly, assembly.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Models.AgentModelDescriptor", true)!.Assembly);
                    break;
                case "Files" or "Shell": AgentSourceArtifactMigrationTests.AssertExecutionToolSemanticOwnership(assembly); break;
                case "BuilderWorker" or "BuilderApp": AgentSourceArtifactMigrationTests.AssertBuilderSemanticOwnership(assembly, role == "app"); break;
                case "McpWorker" or "McpApp": AgentSourceArtifactMigrationTests.AssertMcpSemanticOwnership(assembly, role == "app"); break;
                case "SkillsWorker" or "SkillsApp": AgentSourceArtifactMigrationTests.AssertSkillsSemanticOwnership(assembly, role == "app"); break;
                case "MemoryWorker" or "MemoryApp": AgentSourceArtifactMigrationTests.AssertMemorySemanticOwnership(assembly, role == "app"); break;
                case "SubagentsWorker" or "SubagentsApp": AgentSourceArtifactMigrationTests.AssertSubagentsSemanticOwnership(assembly, role == "app"); break;
                case "LocalWorker" or "LocalApp" or "DockerWorker" or "DockerApp": AgentSourceArtifactMigrationTests.AssertExecutionBackendOwnership(assembly, role == "app"); break;
                case "AgentApp": AgentSourceArtifactMigrationTests.AssertAgentAppSemanticOwnership(assembly); break;
                case "AgentHelpers":
                    AgentSourceArtifactMigrationTests.AssertAgentWorkerSemanticOwnership(assembly);
                    break;
                default: break;
            }
            Assert.Equal(family is not ("Agent" or "Execution.Local" or "Execution.Docker"),
                assembly.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Models.AgentChatStreamEvent") is not null);
            Assert.Equal(family == "Agent", assembly.GetType("Sunder.Package.Agent.Protocol.AgentChatStreamEvent") is not null);
        }
        Assert.Same(assemblies[1], assemblies[1].GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Models.AgentModelDescriptor", true)!.Assembly);
        Assert.NotSame(assemblies[2].GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Models.AgentPromptContextReceipt", true), assemblies[3].GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Models.AgentPromptContextReceipt", true));
        Assert.NotSame(assemblies[4].GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Models.AgentWorkspaceRecord", true), assemblies[5].GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Models.AgentWorkspaceRecord", true));
        Assert.NotSame(assemblies[6].GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Models.AgentToolDescriptor", true), assemblies[7].GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Models.AgentToolDescriptor", true));
        Assert.NotSame(assemblies[8].GetType("Sunder.Package.Agent.Skills.Services.InstalledSkillRecord"), assemblies[9].GetType("Sunder.Package.Agent.Skills.Services.InstalledSkillRecord"));
        Assert.NotSame(assemblies[10].GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Models.AgentSessionRecord", true), assemblies[11].GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Models.AgentSessionRecord", true));
        Assert.NotSame(assemblies[12].GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Models.AgentTurnRecord", true), assemblies[13].GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Models.AgentTurnRecord", true));
        Assert.NotSame(assemblies[14].GetType("Sunder.Agent.Execution.Common.HostApprovalLeaseStore`1"), assemblies[16].GetType("Sunder.Agent.Execution.Common.HostApprovalLeaseStore`1"));
    }

    [AgentPublicSourceMigrationFact]
    public async Task CatalogBusinessSource_CompilesAgainstSdkOnlyAndFencesRetiredReferences()
    {
        var repository = BuilderSourceMigrationTests.FindRepository();
        var root = Directory.CreateTempSubdirectory("sunder-agent-catalog-source-").FullName;
        var load = new AssemblyLoadContext("agent-catalog-source", isCollectible: true);
        try
        {
            CopyCanonicalCatalogSources(root);
            File.WriteAllText(Path.Combine(root, "Probe.cs"), ProbeSource);
            File.WriteAllText(Path.Combine(root, "Catalog.csproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>AgentCatalogSource</AssemblyName><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><LangVersion>14</LangVersion><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
                  <ItemGroup><Reference Include="Sunder.Sdk" HintPath="{{SecurityElement.Escape(typeof(ISunderRpcClient).Assembly.Location)}}" />
                    <Reference Include="Microsoft.Extensions.DependencyInjection.Abstractions" HintPath="{{SecurityElement.Escape(typeof(Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute).Assembly.Location)}}" /></ItemGroup>
                </Project>
                """);
            await BuilderSourceMigrationTests.Run(root, "restore", "Catalog.csproj", "--disable-parallel");
            await BuilderSourceMigrationTests.Run(root, "build", "Catalog.csproj", "--no-restore");
            using var content = File.OpenRead(Path.Combine(root, "bin/Debug/net10.0/AgentCatalogSource.dll"));
            var assembly = load.LoadFromStream(content);
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name!.StartsWith("Sunder.Package.Agent", StringComparison.Ordinal));
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name == "Sunder.Agent.Execution.Common");
            foreach (var name in new[] { "Sunder.Package.Agent.Contracts.Models.AgentToolExecutionContext", "Sunder.Package.Agent.Contracts.Models.AgentToolDescriptor",
                "Sunder.Package.Agent.Contracts.Models.AgentToolResult", "Sunder.Package.Agent.Contracts.Contracts.IAgentToolSource", "Sunder.Agent.Execution.Common.BoundedValue" })
                Assert.Same(assembly, assembly.GetType(name, true)!.Assembly);
            // Generated catalog lifecycle and retirement behavior is covered by the
            // current-service tests; this probe fences role-local model sources.
        }
        finally { load.Unload(); Directory.Delete(root, recursive: true); }
    }

    private static void CopyCanonicalCatalogSources(string destination)
    {
        // This SDK-only probe selects hash-verified canonical role snapshots by
        // file name. Authored paths and historical captures are not compiler inputs.
        var requested = new HashSet<string>(StringComparer.Ordinal)
        {
            "BoundedValue.cs",
            "IAgentToolSource.cs",
            "IAgentPermissionAwareToolSource.cs",
            "IAgentToolPresentationResolver.cs",
            "IAgentExecutionTarget.cs",
            "AgentProfileRecord.cs",
            "AgentProfileModelBindingRecord.cs",
            "AgentWorkspaceRecord.cs",
            "AgentProfileSelectableCapabilityAssignmentRecord.cs",
            "AgentWorkspaceBindingRecord.cs",
            "AgentExecutionTargetContext.cs",
            "AgentExecutionTargetDescriptor.cs",
            "AgentExecutionTargetReadiness.cs",
            "AgentExecutionTargetReadinessStatus.cs",
            "AgentExecutionShellDescriptor.cs",
            "AgentExecutionScopeDescriptor.cs",
            "AgentExecutionPathMapping.cs",
            "AgentExecutionResourceDescriptor.cs",
            "AgentProcessCommandRequest.cs",
            "AgentFileSearchModels.cs",
            "AgentScopedInstructionDiscoveryModels.cs",
            "IAgentResourceAuthorityExecutionTarget.cs",
            "IAgentTool.cs",
            "IAgentPermissionAwareTool.cs",
            "AgentToolDescriptor.cs",
            "AgentToolActivationRequirement.cs",
            "AgentToolPriority.cs",
            "AgentToolSelectionScope.cs",
            "AgentToolReadiness.cs",
            "AgentToolReadinessStatus.cs",
            "AgentToolRequest.cs",
            "AgentToolResult.cs",
            "AgentToolSourceItem.cs",
            "AgentToolPresentation.cs",
            "AgentToolArgumentObject.cs",
            "AgentPayloadLimits.cs",
            "AgentToolExecutionContext.cs",
            "AgentToolSourceContext.cs",
            "AgentPermissionRequest.cs",
            "AgentResourceClaim.cs",
            "AgentResolvedResource.cs",
            "AgentFileDeleteRequest.cs",
            "AgentFileMutationResult.cs",
            "AgentFileReadRequest.cs",
            "AgentFileReadResult.cs",
            "AgentFileWriteRequest.cs",
            "AgentShellCommandRequest.cs",
            "AgentShellCommandResult.cs",
            "AgentProfileSelectableCapabilityDescriptor.cs",
            "AgentProfileSelectableCapabilityKinds.cs", "IAgentProfileSelectableCapabilityProvider.cs",
            "IAgentProfileSelectableCapabilityChangeNotifier.cs",
        };
        foreach (var family in new[] { "Agent", "Tools.Files" })
        {
            var analysis = CanonicalPackageRoles.Analysis(CanonicalPackageRoles.Source(family));
            using var plan = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(analysis, "package-role-plan.v1.json")));
            foreach (var item in plan.RootElement.GetProperty("workerCompile").EnumerateArray())
            {
                var relative = item.GetProperty("path").GetString()!;
                if (!relative.StartsWith("roles/worker/source/", StringComparison.Ordinal)) continue;
                var bytes = File.ReadAllBytes(Path.Combine(analysis, relative));
                var text = System.Text.Encoding.UTF8.GetString(bytes);
                var origin = Regex.Match(text, "(?m)^#line 1 \"([^\"]+)\"");
                if (!origin.Success || !requested.Remove(Path.GetFileName(origin.Groups[1].Value))) continue;
                Assert.Equal(item.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
                File.WriteAllBytes(Path.Combine(destination, Path.GetFileName(origin.Groups[1].Value)), bytes);
            }
            if (family == "Agent")
            {
                var api = Assert.Single(plan.RootElement.GetProperty("workerCompile").EnumerateArray(),
                    item => item.GetProperty("path").GetString() == "service-apis.g.cs");
                var bytes = File.ReadAllBytes(Path.Combine(analysis, "service-apis.g.cs"));
                Assert.Equal(api.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
                // Copy the exact generated interface block needed by the source
                // contexts, not a handwritten declaration or unrelated service APIs.
                var block = Assert.Single(Regex.Matches(System.Text.Encoding.UTF8.GetString(bytes),
                    @"(?ms)^namespace Sunder\.Generated\.Packages\.Sunder_Package_Agent_Services \{\r?\npublic interface IExecutionTargetService \{.*?^\}\r?\n\}"));
                File.WriteAllText(Path.Combine(destination, "ServiceApis.g.cs"), "#nullable enable\n" + block.Value + "\n");
            }
        }
        Assert.Empty(requested);
    }

    private const string ProbeSource = """
        public static class CatalogProbe
        {
        }
        """;
}
