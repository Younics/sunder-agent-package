using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Xml.Linq;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias MemoryWorker;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal const string MemoryCleanerSource = "src/Sunder.Package.Agent.Memory.Semantic/Rpc/SessionCleanerProvider.cs";
    private static async Task VerifyImports(string root, string core, string driver, string archive, string cache)
    {
        var consumer = Path.Combine(root, "Consumer");
        var provider = Path.Combine(root, "Provider");
        foreach (var directory in new[] { consumer, provider })
        {
            Directory.CreateDirectory(directory);
            var isProvider = directory == provider;
            WriteImportProject(directory, core, driver, cache, archive, isProvider);
            File.Copy(Path.Combine(core, "specs/rpc/v1/authoring/conformance/valid/minimal-contract-lock.json"), Path.Combine(directory, "sunder-contracts.lock.json"));
            File.WriteAllText(Path.Combine(directory, "Source.cs"), "public sealed class Seed;");
            await Run(directory, "restore", "Imported.csproj", "--disable-parallel");
            await Run(directory, "msbuild", "Imported.csproj", "-t:FixtureOperation", "-p:Operation=Accept");
            await Run(directory, "msbuild", "Imported.csproj", "-t:FixtureOperation", "-p:Operation=Freeze");
            File.WriteAllText(Path.Combine(directory, "Source.cs"), isProvider ? ProviderSource : ConsumerSource);
            await Run(directory, "msbuild", "Imported.csproj", "-t:FixtureOperation", "-p:Operation=" + (isProvider ? "Implement" : "Add"));
            if (!isProvider) await Run(directory, "msbuild", "Imported.csproj", "-t:FixtureOperation", "-p:Operation=Require", "-p:ContractId=sunder.agent.runtime.catalog");
            var selected = File.ReadAllBytes(Path.Combine(directory, "sunder-contracts.lock.json"));
            await Run(directory, "build", "Imported.csproj", "--no-restore");
            await Run(directory, "msbuild", "Imported.csproj", "-t:FixtureOperation", "-p:Operation=Check");
            Assert.Equal(selected, File.ReadAllBytes(Path.Combine(directory, "sunder-contracts.lock.json")));
            using var locked = JsonDocument.Parse(selected);
            var artifact = locked.RootElement.GetProperty("packageArtifacts").GetProperty("sunder.package.agent");
            Assert.Equal(20, artifact.GetProperty("publicContractCount").GetInt32());
            Assert.Equal(20, artifact.GetProperty("contracts").EnumerateObject().Count());
            Assert.False(artifact.GetProperty("contracts").TryGetProperty("sunder.agent.owner.runtime", out _));
            Assert.Equal("platform", artifact.GetProperty("contracts").GetProperty("sunder.stack.contributor").GetProperty("authorityKind").GetString());
            using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "obj/Debug/net10.0/sunder-rpc/authoring-receipt.v1.json")));
            Assert.Equal(isProvider ? 1 : 19, receipt.RootElement.GetProperty("bundles").GetArrayLength());
            Assert.Equal(isProvider ? 1 : 0, receipt.RootElement.GetProperty("providers").GetArrayLength());
            Assert.Equal(isProvider ? 0 : 19, receipt.RootElement.GetProperty("uses").GetArrayLength());
            Assert.Empty(receipt.RootElement.GetProperty("platformImports").EnumerateArray());
            Assert.DoesNotContain(receipt.RootElement.GetProperty("contracts").EnumerateArray(), contract => contract.GetProperty("contractId").GetString() is "sunder.stack.contributor" or "sunder.agent.owner.runtime");
            if (!isProvider) Assert.Single(receipt.RootElement.GetProperty("uses").EnumerateArray(), use => use.GetProperty("required").GetBoolean());
        }
        await VerifyControlledReferenceParticipantsAsync(root, core, driver, archive, cache);
        await VerifyScriptedToolProvider(root, core, driver, archive, cache);
        await VerifyRejectedImports(root, consumer, archive, cache);
        await VerifyWebArtifact(root, core, driver, archive, cache);
        await VerifyLocalArtifact(root, core, driver, archive, cache);
        await VerifyFilesArtifact(root, core, driver, archive, cache);
        await VerifyShellArtifact(root, core, driver, archive, cache);
        await VerifyBuilderArtifact(root, core, driver, archive, cache);
        await VerifySubagentsArtifact(root, core, driver, archive, cache);
        await VerifySkillsArtifact(root, core, driver, archive, cache);
        await VerifyMemoryWorkerArtifact(root, core, driver, cache);
        await VerifyMemoryProviders(root, consumer);
        await VerifyMemoryConsumers(root);
        await VerifyMcpArtifact(root, core, driver, archive, cache);
        await VerifyOpenAiArtifact(root, core, driver, archive, cache);
        await VerifyDockerArtifact(root, core, driver, archive, cache);
        await VerifyGeminiArtifact(root, core, driver, archive, cache);
        await VerifyAnthropicArtifact(root, core, driver, archive, cache);
        await VerifyLMStudioArtifact(root, core, driver, archive, cache);
        var memoryProvider = Path.Combine(root, "MemoryProvider");
        Directory.CreateDirectory(memoryProvider);
        WriteImportProject(memoryProvider, core, driver, cache, archive, provider: true, packageId: "sunder.package.agent.memory.semantic");
        File.Copy(Path.Combine(core, "specs/rpc/v1/authoring/conformance/valid/minimal-contract-lock.json"), Path.Combine(memoryProvider, "sunder-contracts.lock.json"));
        File.WriteAllText(Path.Combine(memoryProvider, "Source.cs"), "public sealed class Seed;");
        await Run(memoryProvider, "restore", "Imported.csproj", "--disable-parallel");
        await Run(memoryProvider, "msbuild", "Imported.csproj", "-t:FixtureOperation", "-p:Operation=Accept");
        await Run(memoryProvider, "msbuild", "Imported.csproj", "-t:FixtureOperation", "-p:Operation=Freeze");
        File.WriteAllText(Path.Combine(memoryProvider, "Source.cs"), MemoryCleanerProviderSource);
        await Run(memoryProvider, "msbuild", "Imported.csproj", "-t:FixtureOperation", "-p:Operation=Implement", "-p:ProviderKey=sessionCleaner");
        await Run(memoryProvider, "build", "Imported.csproj", "--no-restore");
        var load = new AssemblyLoadContext("agent-artifact-imports", isCollectible: true);
        try
        {
            var consumerAssembly = Load(consumer, "AgentConsumer");
            var providerAssembly = Load(provider, "AgentProvider");
            Assert.All(new[] { consumerAssembly, providerAssembly }, assembly => Assert.DoesNotContain(assembly.GetReferencedAssemblies(),
                reference => reference.Name!.StartsWith("Sunder.Package.Agent", StringComparison.Ordinal)));
            var catalogs = consumerAssembly.GetTypes().Where(type => type.IsClass && !type.IsAbstract
                && !type.ContainsGenericParameters && type.GetInterfaces().Any(contract => contract.IsGenericType
                    && contract.GetGenericTypeDefinition() == typeof(IServiceProviders<>))).ToArray();
            Assert.Equal(19, catalogs.Length);
            Assert.All(catalogs, catalog => Assert.Single(catalog.GetInterfaces(), contract => contract.IsGenericType
                && contract.GetGenericTypeDefinition() == typeof(IServiceProviders<>)));
            var instance = Activator.CreateInstance(providerAssembly.GetType("ExternalCleaner", true)!)!;
            var registration = Assert.IsType<SunderWorkerProviderRegistration>(instance.GetType().GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(instance, null));
            using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(provider, "obj/Debug/net10.0/sunder-rpc/authoring-receipt.v1.json")));
            var contract = Assert.Single(receipt.RootElement.GetProperty("contracts").EnumerateArray());
            var descriptor = SunderRpcContractDescriptor.Parse(System.Text.Encoding.UTF8.GetBytes(contract.GetProperty("canonicalDescriptor").GetString()!));
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            state.Add("example.agent.provider", descriptor, instance, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
            var invoke = consumerAssembly.GetType("Consumer", true)!.GetMethod("DeleteAsync")!
                .CreateDelegate<Func<ISunderRpcClient, Guid, CancellationToken, Task<string>>>();
            var rpc = new RegressionTestRpcClient(state, "example.agent.consumer");
            var session = Guid.NewGuid();
            Assert.Equal("external-cleaner", await invoke(rpc, session, default));
            Assert.Equal(session, instance.GetType().GetProperty("Deleted")!.GetValue(instance));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invoke(rpc, Guid.NewGuid(), cancelled.Token));
            Assert.Equal(session, instance.GetType().GetProperty("Deleted")!.GetValue(instance));
            await state.RetireAsync(instance);
            await Assert.ThrowsAsync<InvalidOperationException>(() => invoke(rpc, Guid.NewGuid(), default));
            var memoryAssembly = Load(memoryProvider, "SemanticMemoryProvider");
            Assert.DoesNotContain(memoryAssembly.GetReferencedAssemblies(), reference => reference.Name!.StartsWith("Sunder.Package.Agent", StringComparison.Ordinal));
            using var package = RegressionTestPackageScope.Create();
            await using var store = new MemoryLocalStore(package.Context);
            var feature = new MemorySemanticFeature(store, null!, null!);
            var cleaner = Activator.CreateInstance(memoryAssembly.GetType("MemoryCleanerProvider", true)!,
                feature.CleanerId, (Func<Guid, CancellationToken, ValueTask>)((sessionId, token) => feature.DeleteSessionAsync(sessionId, token)))!;
            var memoryRegistration = Assert.IsType<SunderWorkerProviderRegistration>(cleaner.GetType().GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(cleaner, null));
            Assert.Equal("semantic.memory.session.cleaner", memoryRegistration.ProviderId);
            using var memoryState = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            memoryState.Add("sunder.package.agent.memory.semantic", descriptor, cleaner, memoryRegistration.Handler, Noop.Instance, memoryRegistration.ProviderId, "2.0.0");
            var memoryRpc = new RegressionTestRpcClient(memoryState, "example.agent.consumer");
            var targetSession = Guid.NewGuid();
            var retainedSession = Guid.NewGuid();
            var deleted = store.UpsertMemory(new(targetSession, "fact", "delete this", "delete this", "evidence", null, false, 0.8f, 0.9f));
            var retained = store.UpsertMemory(new(retainedSession, "fact", "keep this", "keep this", "evidence", null, false, 0.8f, 0.9f));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invoke(memoryRpc, targetSession, cancelled.Token));
            Assert.NotNull(store.GetMemory(deleted.MemoryId));
            Assert.Equal(feature.CleanerId, await invoke(memoryRpc, targetSession, default));
            Assert.Null(store.GetMemory(deleted.MemoryId));
            Assert.Empty(store.ListEvidence(deleted.MemoryId));
            Assert.NotNull(store.GetMemory(retained.MemoryId));
            Assert.Equal(feature.CleanerId, await invoke(memoryRpc, targetSession, default));

            Assembly Load(string directory, string name)
            {
                using var stream = File.OpenRead(Path.Combine(directory, "bin/Debug/net10.0", name + ".dll"));
                return load.LoadFromStream(stream);
            }
        }
        finally { load.Unload(); }
        await VerifyAgentMetadataConsumers(root, core, driver, archive, cache);
    }

    private static void WriteImportProject(string root, string core, string driver, string cache, string archive, bool provider, string? packageId = null,
        string? assemblyName = null, string[]? domainReferences = null)
    {
        var name = assemblyName ?? (packageId is not null ? "SemanticMemoryProvider" : provider ? "AgentProvider" : "AgentConsumer");
        var id = packageId ?? (provider ? "example.agent.provider" : "example.agent.consumer");
        File.WriteAllText(Path.Combine(root, "Imported.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>{{name}}</AssemblyName><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><LangVersion>14</LangVersion><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
              <ItemGroup>
                <Compile Remove="App/**" />
                <Reference Include="Sunder.Sdk" HintPath="{{Escape(typeof(SunderServiceAttribute).Assembly.Location)}}" />
                {{(provider ? $"<Reference Include=\"Sunder.Sdk.Worker\" HintPath=\"{Escape(typeof(SunderWorker).Assembly.Location)}\" />" : "")}}
                {{string.Join("", (domainReferences ?? []).Append(typeof(Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute).Assembly.Location).Distinct(StringComparer.Ordinal)
                    .Select(path => $"<Reference Include=\"{Escape(Path.GetFileNameWithoutExtension(path))}\" HintPath=\"{Escape(path)}\" />"))}}
              </ItemGroup>
              <UsingTask TaskName="Sunder.Package.Build.Tests.AgentSourceArtifactTestTask" AssemblyFile="{{Escape(driver)}}" />
              <Target Name="FixtureOperation" BeforeTargets="CoreCompile" DependsOnTargets="ResolveReferences;GenerateGlobalUsings">
                <PropertyGroup><Operation Condition="'$(Operation)' == ''">Generate</Operation><ContractId Condition="'$(Operation)' == 'Implement' and '$(ContractId)' == ''">sunder.agent.session.cleaner</ContractId><ProviderKey Condition="'$(ProviderKey)' == ''">cleaner</ProviderKey><Candidate Condition="'$(Candidate)' == ''">{{Escape(archive)}}</Candidate></PropertyGroup>
                <ItemGroup><_Source Include="@(Compile)" Condition="'%(Compile.AutoGen)' != 'true'" /></ItemGroup>
                <AgentSourceArtifactTestTask Operation="$(Operation)" ProjectDirectory="$(MSBuildProjectDirectory)"
                    IntermediateOutputPath="$(IntermediateOutputPath)" AssemblyName="$(AssemblyName)" ProjectIdentity="Imported.csproj" OutputType="Library"
                    PackageId="{{id}}" CacheRoot="{{Escape(cache)}}" Candidate="$(Candidate)" ContractId="$(ContractId)" LocalName="$(ProviderKey)"
                    SourceFiles="@(_Source)" ReferencePaths="@(ReferencePath)">
                  <Output TaskParameter="CompileItems" ItemName="Compile" />
                </AgentSourceArtifactTestTask>
              </Target>
            </Project>
            """);
    }

    private const string ConsumerSource = """
        using Sunder.Sdk.Rpc;
        using Sunder.Sdk.Services;
        using System.Text.Json;
        using System.Text.Json.Serialization;
        using Sunder.Generated.Packages.Sunder_Package_Agent_Services;
        using Models = Sunder.Generated.Packages.Sunder_Package_Agent_Models;
        public static class Consumer
        {
            private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Converters = { new SunderRpcJsonConverterFactory(), new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) },
            };
            public static async Task<string> DeleteAsync(ISunderRpcClient rpc, Guid sessionId, CancellationToken token)
            {
                await using var receiver = new ConsumerScope(rpc);
                var api = receiver.Providers<ISessionCleanerService>();
                var provider = (await api.ListAsync(token)).Providers.Single();
                var identity = await provider.Service.DescribeAsync(token);
                await provider.Service.DeleteSessionAsync(sessionId, token);
                return identity.Id;
            }
            public static async Task<string> DescribeCapabilitiesAsync(ISunderRpcClient rpc, CancellationToken token)
            {
                await using var receiver = new ConsumerScope(rpc);
                var api = receiver.Providers<IProfileCapabilityConsumerService>();
                var provider = (await api.ListAsync(token)).Providers.Single();
                return JsonSerializer.Serialize(await provider.Service.DescribeAsync(token), Json);
            }
            public static async Task HandleLifecycleAsync(ISunderRpcClient rpc, string json, CancellationToken token)
            {
                await using var receiver = new ConsumerScope(rpc);
                var api = receiver.Providers<IDurableLifecycleObserverService>();
                var provider = (await api.ListAsync(token)).Providers.Single();
                await provider.Service.HandleAsync(
                    JsonSerializer.Deserialize<Sunder.Generated.Packages.Sunder_Package_Agent_Models.AgentDurableLifecycleEventEnvelope>(json, Json)!, token);
            }
            public static async Task<string> ContributeAsync(ISunderRpcClient rpc, string json, CancellationToken token)
            {
                await using var receiver = new ConsumerScope(rpc);
                var api = receiver.Providers<IPromptContextContributorService>();
                var provider = (await api.ListAsync(token)).Providers.Single();
                using var document = JsonDocument.Parse(json);
                var value = document.RootElement;
                // This memory-only fixture has no selected execution target. Reject
                // supplied authority rather than dropping or JSON-rebinding it.
                if (value.GetProperty("executionTarget").ValueKind != JsonValueKind.Null)
                    throw new JsonException("This fixture requires explicit absence of an execution target.");
                T Read<T>(string name) => value.GetProperty(name).Deserialize<T>(Json)!;
                var request = new Models.AgentPromptContextRequest
                {
                    Session = Read<Models.AgentSessionContextRecord>("session"),
                    Run = Read<Models.AgentRunContextRecord>("run"),
                    Turn = Read<Models.AgentTurnContextRecord>("turn"),
                    Turns = Read<IReadOnlyList<Models.AgentTurnRecord>>("turns"),
                    RecentLiveBufferTurns = Read<IReadOnlyList<Models.AgentTurnRecord>>("recentLiveBufferTurns"),
                    ContextPlan = Read<Models.AgentPromptContextPlan>("contextPlan"),
                    Profile = Read<Models.AgentProfileRecord?>("profile"),
                    Workspace = Read<Models.AgentWorkspaceRecord?>("workspace"),
                    ExecutionBinding = Read<Models.AgentWorkspaceBindingRecord?>("executionBinding"),
                    AvailableTools = Read<IReadOnlyList<Models.AgentToolDescriptor>>("availableTools"),
                    MemoryConsistencyBarrier = Read<Models.AgentMemoryConsistencyBarrier?>("memoryConsistencyBarrier"),
                    TranscriptEpoch = value.GetProperty("transcriptEpoch").GetInt64(),
                    ExecutionTarget = null,
                };
                var result = await provider.Service.ContributeAsync(request, token);
                return JsonSerializer.Serialize(result, Json);
            }
            public static async Task AcknowledgeAsync(ISunderRpcClient rpc, string json, CancellationToken token)
            {
                await using var receiver = new ConsumerScope(rpc);
                var api = receiver.Providers<IPromptContextContributorService>();
                var provider = (await api.ListAsync(token)).Providers.Single();
                await provider.Service.AcknowledgeAsync(
                    JsonSerializer.Deserialize<Sunder.Generated.Packages.Sunder_Package_Agent_Models.AgentPromptContextReceipt>(json, Json)!, token);
            }
        }
        internal sealed class ConsumerScope(ISunderRpcClient rpc) : IServiceProvider, IAsyncDisposable
        {
            private readonly SunderRpcServiceBindingLifetime lifetime = new(rpc);
            private readonly List<IAsyncDisposable> catalogs = [];
            public object? GetService(Type type) => type == typeof(SunderRpcServiceBindingLifetime) ? lifetime
                : type == typeof(ISunderRpcClient) ? rpc : type == typeof(IServiceProvider) ? this : null;
            public IServiceProviders<T> Providers<T>() where T : class
            {
                var implementation = typeof(T).Assembly.GetTypes().Single(type => type.IsClass && !type.IsAbstract
                    && !type.ContainsGenericParameters && typeof(IServiceProviders<T>).IsAssignableFrom(type)
                    && type.GetConstructor([typeof(ISunderRpcClient), typeof(IServiceProvider)]) is not null);
                var catalog = (IServiceProviders<T>)Activator.CreateInstance(implementation, rpc, this)!;
                if (catalog is IAsyncDisposable disposable) catalogs.Add(disposable);
                return catalog;
            }
            public async ValueTask DisposeAsync()
            {
                try { foreach (var catalog in catalogs) await catalog.DisposeAsync(); }
                finally { await lifetime.DisposeAsync(); }
            }
        }
        """;

    private const string ProviderSource = """
        using Sunder.Generated.Packages.Sunder_Package_Agent_Models;
        using Sunder.Generated.Packages.Sunder_Package_Agent_Services;
        using Sunder.Sdk.Services;
        [SunderServiceProvider(typeof(ISessionCleanerService), ProviderId = "example.agent.cleaner")]
        public sealed partial class ExternalCleaner : ISessionCleanerService
        {
            public Guid? Deleted { get; private set; }
            public ValueTask<AgentServiceIdentity> DescribeAsync(CancellationToken token = default)
            { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new AgentServiceIdentity { Id = "external-cleaner", DisplayName = "External cleaner" }); }
            public ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken token = default)
            { token.ThrowIfCancellationRequested(); Deleted = sessionId; return ValueTask.CompletedTask; }
        }
        """;

    private const string MemoryCleanerProviderSource = """
        using Sunder.Generated.Packages.Sunder_Package_Agent_Models;
        using Sunder.Generated.Packages.Sunder_Package_Agent_Services;
        using Sunder.Sdk.Services;
        [SunderServiceProvider(typeof(ISessionCleanerService), ProviderId = "semantic.memory.session.cleaner")]
        public sealed partial class MemoryCleanerProvider(string id, Func<Guid, CancellationToken, ValueTask> delete) : ISessionCleanerService
        {
            public ValueTask<AgentServiceIdentity> DescribeAsync(CancellationToken token = default)
            { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new AgentServiceIdentity { Id = id, DisplayName = "Semantic memory" }); }
            public ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken token = default)
            { token.ThrowIfCancellationRequested(); return delete(sessionId, token); }
        }
        """;

    private sealed class Noop : IDisposable { internal static Noop Instance { get; } = new(); public void Dispose() { } }
}
