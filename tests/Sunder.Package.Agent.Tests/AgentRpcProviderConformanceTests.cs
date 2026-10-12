using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
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

extern alias AgentApp;
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
        var generated = AgentSourceArtifactMigrationTests.CreateGeneratedWorker(typeof(Services.AgentRunControlService).Assembly,
            scope.Context, rpcCatalog.RpcForCaller("sunder.package.agent"));
        await using var lifetime = generated.Lifetime;
        var serviceProvider = generated.Services;
        var registrations = generated.Options.Providers;
        var manifestProviders = ReadManifestProviders("Sunder.Package.Agent");

        Assert.Equal(12, registrations.Count);
        Assert.Empty(Directory.EnumerateFiles(scope.RootPath, "*", SearchOption.AllDirectories));
        Assert.Equal(
            manifestProviders.Keys.Order(StringComparer.Ordinal),
            registrations.Select(static registration => registration.ProviderId).Order(StringComparer.Ordinal));

        foreach (var registration in registrations)
        {
            var descriptor = registration.ContractId == "sunder.stack.contributor"
                ? ReadBundledDescriptor("Sunder.Package.Agent", registration.ContractId)
                : CurrentPrivateRpcDescriptors.Get(registration.ContractId);
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
    public async Task LocalAndDockerCurrentPresentationProviders_ConformToManifestAndDescriptors()
    {
        using var package = RegressionTestPackageScope.Create();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent.tests");
        await using var generated = new GeneratedServiceTestScope(rpc);
        var localDescriptor = CurrentPrivateRpcDescriptors.Get("sunder.package.agent.execution.local.presentation");
        var dockerDescriptor = CurrentPrivateRpcDescriptors.Get("sunder.package.agent.execution.docker.presentation");
        var localService = new LocalExecutionPresentationService(package.Context, new LocalShellCatalogService(package.Context));
        var runner = new ConformanceDockerCli(package.Context);
        var dockerService = new DockerExecutionPresentationService(package.Context, runner,
            new DockerImageCatalogService(package.Context, runner));
        var localRegistration = generated.Register(localService, localDescriptor.ContractId);
        var dockerRegistration = generated.Register(dockerService, dockerDescriptor.ContractId);
        var localManifest = ReadManifestProviders("Sunder.Package.Agent.Execution.Local");
        var dockerManifest = ReadManifestProviders("Sunder.Package.Agent.Execution.Docker");

        AssertRegistrationConforms(localRegistration, localDescriptor);
        AssertManifestIdentity(localRegistration, localManifest[localRegistration.ProviderId]);
        AssertRegistrationConforms(dockerRegistration, dockerDescriptor);
        AssertManifestIdentity(dockerRegistration, dockerManifest[dockerRegistration.ProviderId]);
        Assert.Contains("local.presentation", localManifest.Keys);
        Assert.Contains("docker.presentation", dockerManifest.Keys);
    }

    [Fact]
    public async Task GeneratedHandler_RejectsWrongMethodRouteKind()
    {
        using var providers = new RegressionTestSourceProviders();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        await using var receiver = new GeneratedServiceTestScope(new RegressionTestRpcClient(state, "sunder.package.agent.tests"));
        var registration = providers.Create(RegressionTestBindings.ObservedToolExecutions, new ToolSourceProbe(), receiver);
        var descriptor = CurrentPublicRpcDescriptors.Get(registration.ContractId);
        Assert.Equal(SunderRpcMethodKind.ServerStream, descriptor.FindService("tool-execution-observed")!.FindMethod("execute")!.Kind);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => registration.Handler.InvokeUnaryAsync(null!,
            "tool-execution-observed", "execute", JsonSerializer.SerializeToElement(new { context = new AgentToolExecutionContext(Guid.NewGuid()), request = new AgentToolRequest("read_file", "{}") })).AsTask());
        Assert.Contains("Unknown service method.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentPublicDescriptors_DeclareExactRoutesAndErrors()
    {
        var contractIds = new[]
        {
            RegressionTestBindings.EmbeddingProviders,
            RegressionTestBindings.SystemPromptContributors,
            RegressionTestBindings.PermissionSurfaces,
            RegressionTestBindings.DurableLifecycleObservers,
            RegressionTestBindings.ProfileCapabilityConsumers,
            RegressionTestBindings.SelectableCapabilityProviders,
        };

        foreach (var contractId in contractIds)
        {
            var descriptor = CurrentPublicRpcDescriptors.Get(contractId);
            CurrentPublicRpcDescriptors.AssertCurrent(descriptor);
            Assert.NotEmpty(descriptor.Services);
            Assert.All(descriptor.Services, service => Assert.NotEmpty(service.Methods));
        }
    }

    [Fact]
    public void OwnerCodec_ProductionSerializerEmitsSchemaValidToolDetailAndTurnMutation()
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
        var toolDetail = JsonSerializer.SerializeToElement(provider.ToolDetail, CurrentPrivateRpcDescriptors.OwnerJson);
        AssertMethodOutputSchemaValid(
            CurrentPrivateRpcDescriptors.Get("sunder.agent.owner.runtime"),
            "agent-runtime",
            "transcript-tool-detail",
            toolDetail);
        Assert.Equal("read_file", toolDetail.GetProperty("detail").GetProperty("toolId").GetString());

        var change = JsonSerializer.SerializeToElement(provider.Change, CurrentPrivateRpcDescriptors.OwnerJson);
        AssertMethodOutputSchemaValid(CurrentPrivateRpcDescriptors.Get("sunder.agent.owner.runtime"), "agent-runtime", "changes", change);
        Assert.Equal(" world", change.GetProperty("turnMutation").GetProperty("text").GetString());
        Assert.Equal("2026-08-20T12:34:56Z", change.GetProperty("turnMutation").GetProperty("updatedAtUtc").GetString());
    }

    [Fact]
    public async Task ProviderHandlers_ProductionSerializerEmitsSchemaValidChatAndToolEvents()
    {
        var requestJsonOptions = new JsonSerializerOptions(RegressionTestRpcValues.Json)
        { DefaultIgnoreCondition = JsonIgnoreCondition.Never };
        using var providers = new RegressionTestSourceProviders();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent.tests");
        await using var receiver = new GeneratedServiceTestScope(rpc);
        var chatRegistration = providers.Create(RegressionTestBindings.ChatProviders, new ChatProviderProbe(), receiver);
        var chatProvider = state.AddGenerated("schema.chat", chatRegistration, chatRegistration, providers, chatRegistration.ProviderId);
        var chatRequest = new
        {
            providerId = "schema.chat", modelId = "schema-model", messages = Array.Empty<object>(), options = EmptyChatOptions(),
        };
        var chatEvents = await ReadStreamAsync(
            rpc,
            chatProvider.Endpoint,
            "chat-provider",
            "stream-chat",
            JsonSerializer.SerializeToElement(chatRequest, requestJsonOptions));
        Assert.Equal(2, chatEvents.Count);
        Assert.All(chatEvents, item => AssertMethodOutputSchemaValid(
            CurrentPublicRpcDescriptors.Get(RegressionTestBindings.ChatProviders),
            "chat-provider",
            "stream-chat",
            item));
        Assert.Equal("tool-call", chatEvents[0].GetProperty("contentItems")[0].GetProperty("kind").GetString());
        Assert.Equal("tool-result", chatEvents[1].GetProperty("contentItems")[0].GetProperty("kind").GetString());

        var source = new ToolSourceProbe();
        var requestJson = JsonSerializer.SerializeToElement(new
        {
            context = new
            {
                sessionId = Guid.NewGuid(), profileId = (string?)null, workspace = (object?)null,
                executionBinding = (object?)null, allowOutsideConfiguredScope = false,
                runId = (Guid?)null, runRevision = (long?)null, userTurnId = (Guid?)null, toolCallId = (string?)null,
                transcriptEpoch = 0L, approvedResourceCapabilities = Array.Empty<string>(),
                approvedResourceClaims = Array.Empty<object>(), approvedResourceReferences = Array.Empty<string>(),
                resourceOperation = (object?)null, executionTarget = (object?)null,
                executionTargetConfigurationGeneration = (string?)null,
            },
            request = new AgentToolRequest("read_file", "{\"path\":\"README.md\"}"),
        }, requestJsonOptions);
        var toolRegistration = providers.Create(RegressionTestBindings.ToolSources, source, receiver);
        var toolProvider = state.AddGenerated("schema.tools", toolRegistration, toolRegistration, providers, toolRegistration.ProviderId);
        var toolResult = await rpc.InvokeAsync(
            toolProvider.Endpoint,
            "tool-source",
            "execute",
            requestJson);
        AssertMethodOutputSchemaValid(
            CurrentPublicRpcDescriptors.Get(RegressionTestBindings.ToolSources),
            "tool-source",
            "execute",
            toolResult);

        var observedRegistration = providers.Create(RegressionTestBindings.ObservedToolExecutions, source, receiver);
        var observedProvider = state.AddGenerated("schema.tools", observedRegistration, observedRegistration, providers, observedRegistration.ProviderId);
        var observedEvents = await ReadStreamAsync(
            rpc,
            observedProvider.Endpoint,
            "tool-execution-observed",
            "execute",
            requestJson);
        Assert.Equal(["dispatched", "result"], observedEvents.Select(static item => item.GetProperty("kind").GetString()));
        Assert.All(observedEvents, item => AssertMethodOutputSchemaValid(
            CurrentPublicRpcDescriptors.Get(RegressionTestBindings.ObservedToolExecutions),
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
        if (CurrentPublicRpcDescriptors.IsAgentPublicContract(descriptor.ContractId))
        {
            CurrentPublicRpcDescriptors.AssertCurrent(descriptor);
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
        RegressionTestRpcClient rpc,
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request)
    {
        var items = new List<JsonElement>();
        await foreach (var item in rpc.SubscribeAsync(endpoint, serviceId, methodId, request))
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

    private sealed class ConformanceDockerCli(IPackageContext context) : DockerCliRunner(context)
    {
        protected override Task<string> ResolveEndpointAsync(CancellationToken cancellationToken)
            => Task.FromResult("unix:///var/run/docker.sock");

        protected override Task<DockerCliRunResult> RunCoreAsync(IReadOnlyList<string> args, int timeoutSeconds,
            CancellationToken cancellationToken, string? standardInput = null, IProgress<string>? progress = null)
            => Task.FromResult(new DockerCliRunResult(0, string.Empty, TimedOut: false, WasTruncated: false));
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

        public ValueTask<AgentApp::Sunder.Package.Agent.Contracts.Models.AgentServiceIdentity> DescribeAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentApp::Sunder.Package.Agent.Contracts.Models.AgentServiceIdentity(ContributorId, "Test workspace editor"));

        public ValueTask<bool> CanEditAsync(AgentWorkspaceEditorContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(CanEdit(context));

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

    private sealed record OwnerProviderProbe(AgentTranscriptToolDetailResult ToolDetail, AgentRuntimeChange Change);
}
