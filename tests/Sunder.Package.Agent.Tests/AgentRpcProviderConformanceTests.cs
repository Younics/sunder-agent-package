using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias DockerWorker;
extern alias LocalWorker;
using DockerWorker::Sunder.Package.Agent.Execution.Docker;
using LocalWorker::Sunder.Package.Agent.Execution.Local;

#pragma warning disable MEAI001

public sealed class AgentRpcProviderConformanceTests
{
    [Fact]
    public async Task MainWorkerRegistrations_AreCompleteAndConformBeforeActivation()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var rpcCatalog = new RegressionTestExtensionCatalog();
        var generated = AgentSourceArtifactMigrationTests.CreateGeneratedWorker(typeof(Rpc.Public.RunControlService).Assembly,
            scope.Context, rpcCatalog.RpcForCaller("sunder.package.agent"));
        await using var lifetime = generated.Lifetime;
        var serviceProvider = generated.Services;
        var registrations = generated.Options.Providers;
        var manifestProviders = ReadManifestProviders("Sunder.Package.Agent");

        Assert.Equal(11, registrations.Count);
        Assert.Empty(Directory.EnumerateFiles(scope.RootPath, "*", SearchOption.AllDirectories));
        Assert.Equal(
            manifestProviders.Keys.Order(StringComparer.Ordinal),
            registrations.Select(static registration => registration.ProviderId).Order(StringComparer.Ordinal));

        foreach (var registration in registrations)
        {
            var descriptor = registration.ContractId == "sunder.stack.contributor"
                ? ReadBundledDescriptor("Sunder.Package.Agent", registration.ContractId)
                : RegressionTestRpcDescriptors.Get(registration.ContractId);
            AssertRegistrationConforms(registration, descriptor);
            AssertManifestIdentity(registration, manifestProviders[registration.ProviderId]);
        }

        var lifecycle = serviceProvider.GetRequiredService<AgentWorkerLifecycle>();
        var background = serviceProvider.GetRequiredService<Services.AgentBackgroundWorkService>();
        Assert.False(background.IsRunning);
        lifecycle.CommitGeneration(new Contracts.PackageRuntimeGeneration(Guid.NewGuid(), 1));
        try
        {
            await lifecycle.ActivateAsync(static (_, _, _) => ValueTask.CompletedTask, CancellationToken.None);
            Assert.True(background.IsRunning);
        }
        finally
        {
            await lifecycle.StopAsync(CancellationToken.None);
        }

        var runControl = Assert.Single(
            registrations,
            static registration => registration.ProviderId == "agent.run.control");
        Assert.Contains(
            "agent.run-control.capability-unavailable",
            runControl.DeclaredErrors["run-control"]["select-session-context"]);
        Assert.DoesNotContain(
            "agent.run-control.undeclared",
            runControl.DeclaredErrors.Values.SelectMany(static methods => methods.Values).SelectMany(static errors => errors));
    }

    [Fact]
    public async Task LocalAndDockerCustomHandlers_ConformToManifestAndProductionSchemas()
    {
        using var roles = new RegressionTestProviderRoles();
        using var scope = RegressionTestPackageScope.Create();
        using var localTarget = new LocalExecutionTarget(scope.Context, new LocalExecutionWorkspaceConfigService(scope.Context),
            new LocalShellCatalogService(scope.Context));
        using var dockerLifecycle = new DockerContainerLifecycleService();
        var dockerTarget = new DockerExecutionTarget(scope.Context, new DockerExecutionWorkspaceConfigService(scope.Context), dockerLifecycle);
        var executionDescriptor = RegressionTestRpcDescriptors.Get(AgentRpcContractIds.ExecutionTarget);
        var migratorDescriptor = RegressionTestRpcDescriptors.Get(AgentRpcContractIds.WorkspacePathMigrator);
        var editorDescriptor = RegressionTestRpcDescriptors.Get(AgentRpcContractIds.WorkspaceEditor);
        var localDescriptor = RegressionTestRpcDescriptors.Get("sunder.package.agent.execution.local.presentation");
        var dockerDescriptor = RegressionTestRpcDescriptors.Get("sunder.package.agent.execution.docker.presentation");
        var localRegistration = new LocalWorker::Sunder.Package.Agent.Execution.Local.Rpc.ManagementService(new LocalPresentationProbe())
            .CreateSunderRpcProviderRegistration();
        var localHandler = localRegistration.Handler;
        var localRegistrations = new[]
        {
            localRegistration,
            new LocalWorker::Sunder.Package.Agent.Execution.Local.Rpc.ExecutionTargetProvider(localTarget).CreateSunderRpcProviderRegistration(),
            roles.Create("Execution.Local", "Rpc.WorkspacePathMigratorProvider", new WorkspacePathMigratorProbe()),
            roles.Create("Execution.Local", "Rpc.WorkspaceEditorProvider", new WorkspaceEditorProbe()),
        };
        var localManifest = ReadManifestProviders("Sunder.Package.Agent.Execution.Local");
        Assert.Equal(
            localManifest.Keys.Order(StringComparer.Ordinal),
            localRegistrations.Select(static registration => registration.ProviderId).Order(StringComparer.Ordinal));
        foreach (var registration in localRegistrations)
        {
            var descriptor = registration.ProviderId switch
            {
                "local.execution.target" => executionDescriptor,
                "local.workspace.path.migrator" => migratorDescriptor,
                "local.workspace.editor" => editorDescriptor,
                _ => localDescriptor,
            };
            AssertRegistrationConforms(registration, descriptor);
            AssertManifestIdentity(registration, localManifest[registration.ProviderId]);
        }
        var localResponse = await localHandler.InvokeUnaryAsync(
            null!,
            "presentation",
            "execute",
            Parse("{\"kind\":\"get-settings\"}"));
        AssertMethodOutputSchemaValid(
            localDescriptor,
            "presentation",
            "execute",
            localResponse);

        var dockerRegistration = new DockerWorker::Sunder.Package.Agent.Execution.Docker.Rpc.ManagementService(new DockerPresentationProbe())
            .CreateSunderRpcProviderRegistration();
        var dockerHandler = dockerRegistration.Handler;
        var dockerRegistrations = new[]
        {
            dockerRegistration,
            new DockerWorker::Sunder.Package.Agent.Execution.Docker.Rpc.ExecutionTargetProvider(dockerTarget).CreateSunderRpcProviderRegistration(),
            roles.Create("Execution.Docker", "Rpc.WorkspacePathMigratorProvider", new WorkspacePathMigratorProbe()),
            roles.Create("Execution.Docker", "Rpc.WorkspaceEditorProvider", new WorkspaceEditorProbe()),
        };
        var dockerManifest = ReadManifestProviders("Sunder.Package.Agent.Execution.Docker");
        Assert.Equal(
            dockerManifest.Keys.Where(static providerId => providerId != "docker.stack").Order(StringComparer.Ordinal),
            dockerRegistrations.Select(static registration => registration.ProviderId).Order(StringComparer.Ordinal));
        foreach (var registration in dockerRegistrations)
        {
            var descriptor = registration.ProviderId switch
            {
                "docker.execution.target" => executionDescriptor,
                "docker.workspace.path.migrator" => migratorDescriptor,
                "docker.workspace.editor" => editorDescriptor,
                _ => dockerDescriptor,
            };
            AssertRegistrationConforms(registration, descriptor);
            AssertManifestIdentity(registration, dockerManifest[registration.ProviderId]);
        }
        var dockerResponse = await dockerHandler.InvokeUnaryAsync(
            null!,
            "presentation",
            "execute",
            Parse("{\"kind\":\"get-settings\"}"));
        AssertMethodOutputSchemaValid(
            dockerDescriptor,
            "presentation",
            "execute",
            dockerResponse);
    }

    [Fact]
    public async Task GeneratedHandler_RejectsWrongMethodRouteKind()
    {
        using var roles = new RegressionTestProviderRoles();
        var registration = roles.Create("Mcp", "Rpc.ObservedExecutionProvider", new ToolSourceProbe());
        var descriptor = RegressionTestRpcDescriptors.Get(registration.ContractId);
        Assert.Equal(SunderRpcMethodKind.ServerStream, descriptor.FindService("tool-execution-observed")!.FindMethod("execute")!.Kind);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => registration.Handler.InvokeUnaryAsync(null!,
            "tool-execution-observed", "execute", RegressionTestRpcValues.ToolInvocation(new(Guid.NewGuid()), new("read_file", "{}"))).AsTask());
        Assert.Contains("does not implement unary method 'tool-execution-observed/execute'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RemainingGeneratedStyleFactories_ConformToExactDescriptorRoutesAndErrors()
    {
        using var sources = new RegressionTestSourceProviders();
        using var catalog = new RegressionTestExtensionCatalog();
        var providers = new (string ContractId, object Implementation)[]
        {
            (AgentRpcContractIds.EmbeddingProvider,
                InterfaceProbe.Create<IAgentEmbeddingProvider>()),
            (AgentRpcContractIds.SystemPromptContributor,
                InterfaceProbe.Create<IAgentSystemPromptContributor>()),
            (AgentRpcContractIds.PermissionSurface,
                InterfaceProbe.Create<IAgentPermissionSurface>()),
            (AgentRpcContractIds.DurableLifecycleObserver,
                InterfaceProbe.Create<IAgentDurableLifecycleObserver>()),
            (AgentRpcContractIds.ProfileCapabilityConsumer,
                InterfaceProbe.Create<IAgentProfileCapabilityConsumer>()),
            (AgentRpcContractIds.SelectableCapabilityProvider,
                InterfaceProbe.Create<IAgentProfileSelectableCapabilityProvider>()),
        };

        foreach (var (contractId, implementation) in providers)
        {
            var generated = sources.Create(contractId, implementation, catalog, catalog.RpcForCaller("test.package"));
            using var lifetime = generated.Lifetime;
            var handler = generated.Registration.Handler;
            var descriptor = RegressionTestRpcDescriptors.Get(contractId);
            var metadata = Assert.IsAssignableFrom<ISunderRpcDeclaredErrorMetadata>(handler);
            AssertDeclaredErrors(descriptor, metadata.DeclaredErrors);
        }
    }

    [Fact]
    public async Task OwnerHandler_ProductionSerializerEmitsSchemaValidToolDetailAndTurnMutation()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var updatedAt = new DateTimeOffset(2026, 8, 20, 12, 34, 56, TimeSpan.Zero);
        var provider = new OwnerProviderProbe(
            new AgentTranscriptToolDetailResult(
                Found: true,
                new AgentTranscriptToolDetailRecord(
                    sessionId,
                    Guid.NewGuid(),
                    "call-1",
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "read_file",
                    "{\"path\":\"README.md\"}",
                    "contents",
                    "Read file.",
                    "{\"lineCount\":1}",
                    "[]",
                    "{\"kind\":\"file\"}",
                    WasTruncated: false,
                    IsError: false,
                    ErrorCode: null,
                    BackendId: "files",
                    AgentToolExecutionStatus.Completed,
                    ToolOwnerPackageId: "sunder.package.agent.tools.files",
                    ToolSchemaId: "read-file",
                    ToolSchemaVersion: "1.0.0",
                    Revision: 7)
                {
                    RunId = Guid.NewGuid(),
                    RunRevision = 3,
                }),
            new AgentRuntimeChange(
                12,
                AgentRuntimeChangeKind.TurnMutation,
                SessionId: sessionId,
                TurnMutation: new AgentTurnMutation(
                    sessionId,
                    turnId,
                    4,
                    AgentTurnMutationKind.Append,
                    5,
                    " world",
                    updatedAt,
                    RuntimeRevision: 12),
                RuntimeInstanceId: "runtime-1"));
        var handler = new Rpc.Owner.OwnerRuntimeService(provider).CreateSunderRpcProviderRegistration().Handler;

        var toolDetail = await handler.InvokeUnaryAsync(
            null!,
            "agent-runtime",
            "transcript-tool-detail",
            JsonSerializer.SerializeToElement(new AgentTranscriptToolDetailRequest(sessionId), RegressionTestRpcValues.Json));
        AssertMethodOutputSchemaValid(
            RegressionTestRpcDescriptors.Get("sunder.agent.owner.runtime"),
            "agent-runtime",
            "transcript-tool-detail",
            toolDetail);
        Assert.Equal("read_file", toolDetail.GetProperty("detail").GetProperty("toolId").GetString());

        var changes = await ReadStreamAsync(
            handler,
            "agent-runtime",
            "changes",
            JsonSerializer.SerializeToElement(new AgentChangeSubscription(11, SupportsTurnMutations: true), RegressionTestRpcValues.Json));
        var change = Assert.Single(changes);
        AssertMethodOutputSchemaValid(RegressionTestRpcDescriptors.Get("sunder.agent.owner.runtime"), "agent-runtime", "changes", change);
        Assert.Equal(" world", change.GetProperty("turnMutation").GetProperty("text").GetString());
        Assert.Equal("2026-08-20T12:34:56Z", change.GetProperty("turnMutation").GetProperty("updatedAtUtc").GetString());
    }

    [Fact]
    public async Task ProviderHandlers_ProductionSerializerEmitsSchemaValidChatAndToolEvents()
    {
        using var roles = new RegressionTestProviderRoles();
        var chatHandler = roles.Create("Provider.Gemini", "Rpc.ChatProvider", new ChatProviderProbe()).Handler;
        var chatRequest = new
        {
            providerId = "schema.chat", modelId = "schema-model", messages = Array.Empty<object>(), options = EmptyChatOptions(),
        };
        var chatEvents = await ReadStreamAsync(
            chatHandler,
            "chat-provider",
            "stream-chat",
            JsonSerializer.SerializeToElement(chatRequest, RegressionTestRpcValues.Json));
        Assert.Equal(2, chatEvents.Count);
        Assert.All(chatEvents, item => AssertMethodOutputSchemaValid(
            RegressionTestRpcDescriptors.Get(AgentRpcContractIds.ChatProvider),
            "chat-provider",
            "stream-chat",
            item));
        Assert.Equal("tool-call", chatEvents[0].GetProperty("contentItems")[0].GetProperty("kind").GetString());
        Assert.Equal("tool-result", chatEvents[1].GetProperty("contentItems")[0].GetProperty("kind").GetString());

        var source = new ToolSourceProbe();
        var requestJson = RegressionTestRpcValues.ToolInvocation(new AgentToolExecutionContext(Guid.NewGuid()),
            new AgentToolRequest("read_file", "{\"path\":\"README.md\"}"));
        using var sources = new RegressionTestSourceProviders();
        using var catalog = new RegressionTestExtensionCatalog();
        var generated = sources.Create(AgentRpcContractIds.ToolSource, source, catalog, catalog.RpcForCaller("test.package"));
        using var lifetime = generated.Lifetime;
        var toolHandler = generated.Registration.Handler;
        var toolResult = await toolHandler.InvokeUnaryAsync(
            null!,
            "tool-source",
            "execute",
            requestJson);
        AssertMethodOutputSchemaValid(
            RegressionTestRpcDescriptors.Get(AgentRpcContractIds.ToolSource),
            "tool-source",
            "execute",
            toolResult);

        var observedHandler = roles.Create("Mcp", "Rpc.ObservedExecutionProvider", source).Handler;
        var observedEvents = await ReadStreamAsync(
            observedHandler,
            "tool-execution-observed",
            "execute",
            requestJson);
        Assert.Equal(["dispatched", "result"], observedEvents.Select(static item => item.GetProperty("kind").GetString()));
        Assert.All(observedEvents, item => AssertMethodOutputSchemaValid(
            RegressionTestRpcDescriptors.Get(AgentRpcContractIds.ObservedToolExecution),
            "tool-execution-observed",
            "execute",
            item));
    }

    private static SunderWorkerProviderRegistration Register(
        string providerId,
        SunderRpcContractDescriptor descriptor,
        ISunderRpcServiceHandler handler)
        => new(
            providerId,
            descriptor.ContractId,
            descriptor.Version,
            descriptor.Sha256,
            descriptor.AbiSha256 ?? throw new InvalidOperationException("The test descriptor has no ABI identity."),
            handler);

    private static void AssertRegistrationConforms(
        SunderWorkerProviderRegistration registration,
        SunderRpcContractDescriptor descriptor)
    {
        Assert.False(string.IsNullOrWhiteSpace(registration.ProviderId));
        Assert.Equal(descriptor.ContractId, registration.ContractId);
        Assert.Equal(descriptor.Version, registration.ContractVersion);
        Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
        Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
        Assert.NotNull(descriptor.AbiSha256);
        if (descriptor.ContractId.StartsWith("sunder.agent.", StringComparison.Ordinal)
            || descriptor.ContractId.StartsWith("sunder.package.agent.execution.", StringComparison.Ordinal))
        {
            Assert.Equal("1.0.0", registration.ContractVersion);
        }
        AssertDeclaredErrors(descriptor, registration.DeclaredErrors);
    }

    private static void AssertDeclaredErrors(
        SunderRpcContractDescriptor descriptor,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> actual)
    {
        var expected = descriptor.Services.ToDictionary(service => service.ServiceId,
            service => service.Methods.ToDictionary(method => method.MethodId, method => method.Errors, StringComparer.Ordinal), StringComparer.Ordinal);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (serviceId, expectedMethods) in expected)
        {
            var actualMethods = actual[serviceId];
            Assert.Equal(expectedMethods.Keys.Order(StringComparer.Ordinal), actualMethods.Keys.Order(StringComparer.Ordinal));
            foreach (var (methodId, expectedErrors) in expectedMethods)
            {
                Assert.Equal(expectedErrors, actualMethods[methodId]);
            }
        }
    }

    private static void AssertManifestIdentity(
        SunderWorkerProviderRegistration registration,
        JsonElement manifestProvider)
    {
        Assert.Equal(registration.ProviderId, manifestProvider.GetProperty("providerId").GetString());
        Assert.Equal(registration.ContractId, manifestProvider.GetProperty("contractId").GetString());
        Assert.Equal(registration.ContractVersion, manifestProvider.GetProperty("contractVersion").GetString());
        Assert.Equal(registration.DescriptorSha256, manifestProvider.GetProperty("descriptorSha256").GetString());
        Assert.Equal(registration.AbiSha256, manifestProvider.GetProperty("abiSha256").GetString());
    }

    private static SunderRpcContractDescriptor ReadBundledDescriptor(string projectDirectoryName, string contractId)
    {
        var manifestPath = ManifestPath(projectDirectoryName);
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var bundle = Assert.Single(document.RootElement.GetProperty("contractBundles").EnumerateArray(),
            value => value.GetProperty("contractId").GetString() == contractId);
        var descriptor = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(manifestPath))!, "payload", "shared", bundle.GetProperty("descriptorPath").GetString()!)));
        Assert.Equal(bundle.GetProperty("descriptorSha256").GetString(), descriptor.Sha256);
        Assert.Equal(bundle.GetProperty("abiSha256").GetString(), descriptor.AbiSha256);
        return descriptor;
    }

    private static string ManifestPath(string projectDirectoryName)
    {
        var output = new DirectoryInfo(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var targetFramework = output.Name;
        var configuration = output.Parent?.Name ?? "Debug";
        var path = Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "src",
            projectDirectoryName,
            "bin",
            configuration,
            targetFramework,
            "sunder-dev", "manifest", "sunder-package.json");
        Assert.True(File.Exists(path), $"Generated manifest was not found: {path}");
        return path;
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadManifestProviders(string projectDirectoryName)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(ManifestPath(projectDirectoryName)));
        return document.RootElement.GetProperty("provides")
            .EnumerateArray()
            .ToDictionary(
                static provider => provider.GetProperty("providerId").GetString()!,
                static provider => provider.Clone(),
                StringComparer.Ordinal);
    }

    private static void AssertMethodOutputSchemaValid(
        SunderRpcContractDescriptor descriptor,
        string serviceId,
        string methodId,
        JsonElement value)
    {
        var service = Assert.IsType<SunderRpcServiceDescriptor>(descriptor.FindService(serviceId));
        var method = Assert.IsType<SunderRpcMethodDescriptor>(service.FindMethod(methodId));
        Assert.True(descriptor.IsValid(method.OutputSchemaReference, value, out var error), error);
    }

    private static async Task<IReadOnlyList<JsonElement>> ReadStreamAsync(
        ISunderRpcServiceHandler handler,
        string serviceId,
        string methodId,
        JsonElement request)
    {
        var items = new List<JsonElement>();
        await foreach (var item in handler.InvokeServerStreamAsync(null!, serviceId, methodId, request))
        {
            items.Add(item.Clone());
        }
        return items;
    }

    private static AgentChatOptions EmptyChatOptions()
        => new(
            Instructions: null,
            ConversationId: null,
            ModelId: null,
            Temperature: null,
            MaxOutputTokens: null,
            TopP: null,
            TopK: null,
            FrequencyPenalty: null,
            PresencePenalty: null,
            Seed: null,
            AllowMultipleToolCalls: null,
            ReasoningEffort: null,
            Tools: [],
            AdditionalProperties: null,
            ContinuationHandle: null);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private class InterfaceProbe : DispatchProxy
    {
        internal static T Create<T>() where T : class
            => DispatchProxy.Create<T, InterfaceProbe>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new NotSupportedException("The conformance probe must not invoke provider behavior.");
    }

    private sealed class LocalPresentationProbe : ILocalExecutionPresentationClient
    {
        public ValueTask<LocalExecutionOperationResponse> ExecuteAsync(
            LocalExecutionOperationRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new LocalExecutionOperationResponse(
                TimeoutSeconds: "300",
                DetectedShells: [],
                CustomShells: [],
                ShellCatalogRevision: 4,
                Message: "Ready."));
    }

    private sealed class DockerPresentationProbe : IDockerExecutionPresentationClient
    {
        public ValueTask<DockerExecutionOperationResponse> ExecuteAsync(
            DockerExecutionOperationRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new DockerExecutionOperationResponse(
                TimeoutSeconds: "300",
                DockerCliPath: "/usr/bin/docker",
                Success: true,
                Message: "Ready.",
                CatalogRevision: 5));
    }

    private sealed class ExecutionTargetProbe : IAgentExecutionTarget
    {
        public AgentExecutionTargetDescriptor Descriptor { get; } = new(
            "test",
            "test.execution.target",
            "Test execution target",
            null,
            SupportsShell: true,
            SupportsFiles: true);

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(
            AgentExecutionTargetContext context,
            string path,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(
            AgentExecutionTargetContext context,
            AgentShellCommandRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileReadResult> ReadFileAsync(
            AgentExecutionTargetContext context,
            AgentFileReadRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> WriteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileWriteRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileDeleteRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class WorkspacePathMigratorProbe : IAgentWorkspacePathMigrationContributor
    {
        public string ContributorId => "test.workspace.path.migrator";

        public bool CanMigrate(AgentWorkspacePathMigrationContext context) => false;

        public Task<IReadOnlyList<AgentWorkspacePathMigrationItem>> GetLegacyWorkspacePathsAsync(
            AgentWorkspacePathMigrationContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentWorkspacePathMigrationItem>>([]);

        public Task CompleteWorkspacePathMigrationAsync(
            AgentWorkspacePathMigrationContext context,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class WorkspaceEditorProbe : IAgentWorkspaceEditorContributor
    {
        public string ContributorId => "test.workspace.editor";

        public bool CanEdit(AgentWorkspaceEditorContext context) => false;

        public ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(
            AgentWorkspaceEditorContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentEditorSection>>([]);

        public ValueTask<AgentEditorSaveResult> SaveSectionAsync(
            AgentWorkspaceEditorContext context,
            AgentEditorSaveRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ChatProviderProbe : IAgentChatProvider
    {
        public AgentProviderDescriptor Descriptor { get; } = new(
            "schema.chat",
            "Schema Chat",
            [],
            SupportsStreaming: true,
            SupportsInterruptibleRuns: true);

        public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentModelDescriptor>>([]);

        public ValueTask<AgentProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.Ready,
                "Ready."));

        public ValueTask<AgentProviderRunCapabilities> GetRunCapabilitiesAsync(
            string? modelId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<IChatClient> CreateChatClientAsync(
            AgentChatClientContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IChatClient>(new ChatClientProbe());
    }

    private sealed class ChatClientProbe : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>
                {
                    ["path"] = "README.md",
                })]);
            yield return new ChatResponseUpdate(
                ChatRole.Tool,
                [new FunctionResultContent("call-1", "contents")]);
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class ToolSourceProbe :
        IAgentToolSource,
        IAgentObservedToolExecution,
        IAgentToolDispatchAwareSource
    {
        public string SourceId => "schema.tools";
        public string DisplayName => "Schema Tools";
        public string SourceKind => "test";

        public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
            AgentToolSourceContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentToolDescriptor>>([]);

        public ValueTask<AgentToolReadiness?> GetReadinessAsync(
            string toolId,
            AgentToolSourceContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentToolReadiness?>(null);

        public ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            AgentToolRequest request,
            CancellationToken cancellationToken = default)
        {
            context.DispatchStarted?.Invoke();
            return ValueTask.FromResult(new AgentToolResult(
                request.ToolId,
                "Read file.",
                Content: "contents",
                StructuredPayloadJson: "{\"lineCount\":1}",
                BackendId: SourceId));
        }
    }

    private sealed class OwnerProviderProbe(
        AgentTranscriptToolDetailResult toolDetail,
        AgentRuntimeChange change) : IAgentOwnerRuntimeProvider
    {
        public ValueTask<AgentTransportProfile> NegotiateTransportProfileAsync(
            AgentTransportProfileRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<AgentChatSnapshotProjection> GetChatSnapshotAsync(
            AgentChatSnapshotRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<AgentDashboardProjection> GetDashboardAsync(
            AgentDashboardRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<AgentTranscriptPage> GetTranscriptPageAsync(
            AgentTranscriptPageRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<AgentCatalogProjection> GetCatalogAsync(
            AgentCatalogRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<AgentProfileCommandResult> ExecuteProfileCommandAsync(
            AgentProfileCommand request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<AgentWorkspaceCommandResult> ExecuteWorkspaceCommandAsync(
            AgentWorkspaceCommand request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<AgentSessionCommandResult> ExecuteSessionCommandAsync(
            AgentSessionCommand request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<AgentRunCommandResult> ExecuteRunCommandAsync(
            AgentRunCommand request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<AgentRunCommandStatusResult> GetRunStatusAsync(
            AgentRunCommandStatusRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<AgentPermissionProjection> ExecutePermissionCommandAsync(
            AgentPermissionCommand request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<AgentAttachmentContentUploadResult> UploadAttachmentAsync(
            SunderRpcInvocationContext context,
            AgentAttachmentContentUploadRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<AgentAttachmentContentDownloadResult> DownloadAttachmentAsync(
            SunderRpcInvocationContext context,
            AgentAttachmentContentDownloadRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<AgentAttachmentContentDiscardResult> DiscardAttachmentAsync(
            AgentAttachmentContentDiscardRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<HistorySearchResponse> SearchHistoryAsync(
            HistorySearchRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<HistorySearchState> GetHistoryStateAsync(
            HistorySearchStateRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<HistorySearchCommandResult> ExecuteHistoryCommandAsync(
            HistorySearchCommand request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<AgentTranscriptAroundTurnPage> GetTranscriptAroundAsync(
            AgentTranscriptAroundTurnRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<AgentTranscriptToolDetailResult> GetTranscriptToolDetailAsync(
            AgentTranscriptToolDetailRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(toolDetail);

        public async IAsyncEnumerable<AgentRuntimeChange> WatchChangesAsync(
            AgentChangeSubscription request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return change;
            await Task.CompletedTask;
        }

        public IAsyncEnumerable<HistorySearchStatus> WatchHistoryStatusAsync(
            HistorySearchStatusSubscription request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
