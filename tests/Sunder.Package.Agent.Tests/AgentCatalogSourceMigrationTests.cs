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
                case "Files" or "Shell": AgentSourceArtifactMigrationTests.AssertExecutionToolSemanticOwnership(assembly); break;
                case "BuilderWorker" or "BuilderApp": AgentSourceArtifactMigrationTests.AssertBuilderSemanticOwnership(assembly, role == "app"); break;
                case "McpWorker" or "McpApp": AgentSourceArtifactMigrationTests.AssertMcpSemanticOwnership(assembly, role == "app"); break;
                case "SkillsWorker" or "SkillsApp": AgentSourceArtifactMigrationTests.AssertSkillsSemanticOwnership(assembly, role == "app"); break;
                case "MemoryWorker" or "MemoryApp": AgentSourceArtifactMigrationTests.AssertMemorySemanticOwnership(assembly, role == "app"); break;
                case "SubagentsWorker" or "SubagentsApp": AgentSourceArtifactMigrationTests.AssertSubagentsSemanticOwnership(assembly, role == "app"); break;
                case "LocalWorker" or "LocalApp" or "DockerWorker" or "DockerApp": AgentSourceArtifactMigrationTests.AssertExecutionBackendOwnership(assembly, role == "app"); break;
                case "AgentApp": AgentSourceArtifactMigrationTests.AssertAgentAppSemanticOwnership(assembly); break;
                case "AgentHelpers":
                    foreach (var typeName in new[] { "AgentRpcContractIds", "AgentRunControlRegistry", "AgentChatWireMapper", "AgentChatFailureProjection" })
                        Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Protocol." + typeName, true)!.Assembly);
                    Assert.Null(assembly.GetType("Sunder.Package.Agent.Protocol.AgentRpcServiceHandler"));
                    break;
                default: Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Contracts.Models.AgentModelDescriptor", true)!.Assembly); break;
            }
            Assert.Equal(name is "ProviderWorker" or "AgentHelpers", assembly.GetType("Sunder.Package.Agent.Protocol.AgentChatStreamEvent") is not null);
        }
        Assert.NotSame(assemblies[0].GetType("Sunder.Package.Agent.Contracts.Models.AgentModelDescriptor"), assemblies[1].GetType("Sunder.Package.Agent.Contracts.Models.AgentModelDescriptor"));
        Assert.NotSame(assemblies[2].GetType("Sunder.Package.Agent.Contracts.Models.AgentPromptContextReceipt"), assemblies[3].GetType("Sunder.Package.Agent.Contracts.Models.AgentPromptContextReceipt"));
        Assert.NotSame(assemblies[4].GetType("Sunder.Package.Agent.Contracts.Models.AgentWorkspaceRecord"), assemblies[5].GetType("Sunder.Package.Agent.Contracts.Models.AgentWorkspaceRecord"));
        Assert.NotSame(assemblies[6].GetType("Sunder.Package.Agent.Contracts.Models.AgentToolDescriptor"), assemblies[7].GetType("Sunder.Package.Agent.Contracts.Models.AgentToolDescriptor"));
        Assert.NotSame(assemblies[8].GetType("Sunder.Package.Agent.Skills.Services.InstalledSkillRecord"), assemblies[9].GetType("Sunder.Package.Agent.Skills.Services.InstalledSkillRecord"));
        Assert.NotSame(assemblies[10].GetType("Sunder.Package.Agent.Contracts.Models.AgentSessionRecord"), assemblies[11].GetType("Sunder.Package.Agent.Contracts.Models.AgentSessionRecord"));
        Assert.NotSame(assemblies[12].GetType("Sunder.Package.Agent.Contracts.Models.AgentTurnRecord"), assemblies[13].GetType("Sunder.Package.Agent.Contracts.Models.AgentTurnRecord"));
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
            var run = assembly.GetType("CatalogProbe", true)!.GetMethod("RunAsync", BindingFlags.Public | BindingFlags.Static)!
                .CreateDelegate<Func<ISunderRpcClient, Func<Task>, Action, Task>>();
            using var state = new RegressionTestRpcState();
            var cleaner = new Cleaner();
            var descriptor = RegressionTestRpcDescriptors.Get(AgentRpcContractIds.SessionCleaner);
            var handler = new Rpc.Public.SessionCleanerService(cleaner).CreateSunderRpcProviderRegistration().Handler;
            Add();
            await run(new RegressionTestRpcClient(state, "sunder.package.agent"), () => state.RetireAsync(cleaner), Add).WaitAsync(TimeSpan.FromSeconds(15));

            void Add() => state.Add("sunder.package.agent", descriptor, cleaner, handler, EmptyDisposable.Instance, "catalog.cleaner", "2.0.0");
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
            "AgentRpcCatalog.cs",
            "AgentRpcCatalog.Async.cs",
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
            "AgentProfileSelectableCapabilityChangeObserver.cs", "AgentProfileSelectableCapabilityDescriptor.cs",
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
        }
        Assert.Empty(requested);
    }

    private sealed class Cleaner : IAgentSessionDataCleaner
    {
        public string CleanerId => "catalog.cleaner";
        public void DeleteSessionData(Guid sessionId) { }
    }
    private sealed class EmptyDisposable : IDisposable
    {
        internal static EmptyDisposable Instance { get; } = new();
        public void Dispose() { }
    }

    private const string ProbeSource = """
        using Sunder.Package.Agent.Protocol;
        using Sunder.Package.Agent.Contracts.Contracts;
        using Sunder.Package.Agent.Contracts.Models;
        using Sunder.Package.Agent.Contracts.Services;
        using Sunder.Sdk.Rpc;
        public static class CatalogProbe
        {
            public static async Task RunAsync(ISunderRpcClient rpc, Func<Task> retire, Action replace)
            {
                using var catalog = AgentRpcCatalog.CreateDormant(rpc);
                var bindings = new List<Binding>();
                var service = AgentRpcService<IAgentProfileSelectableCapabilityProvider>.WithProviderIdentity("sunder.agent.session.cleaner", (_, provider) =>
                { var binding = new Binding(provider); bindings.Add(binding); return binding; });
                var first = (await catalog.GetServiceReferencesAsync(service)).Single();
                if (bindings.Single().Provider != first.Provider) throw new InvalidOperationException("Factory lost the selected provider identity.");
                if (catalog.Availability != AgentRpcCatalogAvailability.Available) throw new InvalidOperationException("Catalog did not become available.");
                using var lease = await first.TryAcquireAsync() ?? throw new InvalidOperationException("Current reference could not be acquired.");
                var retirement = lease.RetirementToken;
                var oldHandle = first.ToHandle();
                using var observer = new AgentProfileSelectableCapabilityChangeObserver(catalog, false, service);
                var changes = 0; observer.Changed += () => changes++;
                observer.RefreshProviderSubscriptions(); observer.RefreshProviderSubscriptions();
                if (bindings[0].Subscriptions != 1) throw new InvalidOperationException("Observer duplicated its subscription.");
                bindings[0].Notify(); if (changes != 1) throw new InvalidOperationException("Observer lost invalidation.");
                lease.Dispose();
                try { _ = lease.Service; throw new InvalidOperationException("Disposed lease remained usable."); }
                catch (ObjectDisposedException) { }
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                try { await first.TryAcquireAsync(cancelled.Token); throw new InvalidOperationException("Caller cancellation was ignored."); }
                catch (OperationCanceledException) { }
                await retire();
                if (await first.TryAcquireAsync() is not null || !retirement.IsCancellationRequested || bindings[0].Disposed != 1)
                    throw new InvalidOperationException("The retired reference or binding survived.");
                observer.RefreshProviderSubscriptions();
                if (bindings[0].Subscriptions != 0) throw new InvalidOperationException("Observer retained a retired provider.");
                replace();
                var replacement = (await catalog.GetServiceReferencesAsync(service)).Single();
                observer.RefreshProviderSubscriptions();
                if (bindings[1].Subscriptions != 1) throw new InvalidOperationException("Observer did not bind the replacement.");
                if (ReferenceEquals(first, replacement) || replacement.ToHandle() == oldHandle)
                    throw new InvalidOperationException("Replacement reused the old activation.");
                if (await catalog.TryGetServiceReferenceAsync(service, oldHandle) is not null)
                    throw new InvalidOperationException("The old handle selected a replacement.");
                using var current = await replacement.TryAcquireAsync() ?? throw new InvalidOperationException("Replacement could not be acquired.");
                var currentRetirement = current.RetirementToken;
                observer.Dispose(); bindings[1].Notify();
                if (changes != 1 || bindings[1].Subscriptions != 0) throw new InvalidOperationException("Disposed observer retained callbacks.");
                catalog.Dispose();
                if (!currentRetirement.IsCancellationRequested || bindings.Any(binding => binding.Disposed != 1))
                    throw new InvalidOperationException("Catalog shutdown did not retire all owned bindings exactly once.");
            }
            private sealed class Binding(SunderRpcProviderSnapshot provider) : IAgentProfileSelectableCapabilityProvider, IAgentProfileSelectableCapabilityChangeNotifier, IDisposable
            {
                internal SunderRpcProviderSnapshot Provider => provider;
                internal int Disposed;
                internal int Subscriptions;
                private Action? _changed;
                public string ProviderId => provider.ProviderId;
                public string DisplayName => "Role-local binding";
                public ValueTask<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListCapabilitiesAsync(AgentProfileSelectableCapabilityRequest request, CancellationToken cancellationToken = default)
                    => ValueTask.FromResult<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>([]);
                public event Action? SelectableCapabilitiesChanged { add { _changed += value; Subscriptions++; } remove { _changed -= value; Subscriptions--; } }
                internal void Notify() => _changed?.Invoke();
                public void Dispose() => Disposed++;
            }
        }
        """;
}
