extern alias BuilderApp;
extern alias BuilderWorker;

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Xunit;
using App = BuilderApp::Sunder.Package.Agent.Builder;
using Worker = BuilderWorker::Sunder.Package.Agent.Builder;
using MakerContract = BuilderWorker::Sunder.Generated.Packages.Younics_Sunder_Package_Maker_Internal.CYounics_Sunder_Package_Maker_Runtime.__SunderContract;

namespace Sunder.Package.Agent.Tests;

// Generated App startup/catalog/client -> real generated Worker handler/content
// transport -> activation-owned App operations. Only the finite job backend is a
// domain fake; no page, process, model provider or live development owner is used.
public sealed partial class BuilderDevelopmentRelayTests
{
    [Fact]
    public async Task GeneratedStartupWorksWithoutAgentOrPageAndCancelledCandidatesStayIdle()
    {
        await using var fixture = new RelayFixture();
        using (var abandoned = new CancellationTokenSource())
        {
            abandoned.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => fixture.Module.StartAppServices(fixture.AppServices, abandoned.Token));
            Assert.False(fixture.Runtime.HasDevelopmentApp);
        }
        fixture.StartApp();
        await fixture.WaitConnectedAsync();
        fixture.StartApp(); // Idempotent publication cannot start a second consumer.
        var reply = await fixture.Runtime.InvokeDevelopmentAsync("info", "", fixture.SourceRequest(), null, default)
            .WaitAsync(TimeSpan.FromSeconds(5));
        using var info = JsonDocument.Parse(reply.PayloadJson);
        Assert.Equal(fixture.Project.SourceRevision, info.RootElement.GetProperty("project").GetProperty("sourceRevision").GetString());
        Assert.Single(fixture.Transport.Calls, method => method == "connect-development-app");
        Assert.Empty(fixture.State.Discover(RegressionTestBindings.ToolSources).Providers);
        Assert.Empty(fixture.State.Discover(RegressionTestBindings.ExecutionTargets).Providers);
        Assert.Empty(fixture.Backend.Starts);
        Assert.Empty(fixture.Development.Loads);
        Assert.Equal(0, fixture.PageResolutions);
    }

    [Fact]
    public async Task GeneratedPackageInfoStaysSmallAndFilteredLogsRemainAvailableAfterUnloadWithoutReacquisition()
    {
        await using var fixture = new RelayFixture();
        var tools = await fixture.ConnectToolsAsync();
        var info = await tools.ExecuteAsync(fixture.Context, new AgentToolRequest("maker_package", fixture.Reload.ArgumentsJson));
        Assert.False(info.IsError, info.Content);
        using (var json = JsonDocument.Parse(info.Content!))
        {
            Assert.False(json.RootElement.TryGetProperty("operations", out _));
            Assert.False(json.RootElement.TryGetProperty("logs", out _));
            Assert.False(json.RootElement.TryGetProperty("job", out _));
        }
        var since = DateTimeOffset.Parse("2026-10-24T03:35:00+08:00");
        var logs = new AgentToolRequest("maker_logs", JsonSerializer.Serialize(new { package = fixture.Project.EntryPoint,
            since, level = "error", source = "runtime", limit = 7 }));
        var denied = await tools.ExecuteAsync(fixture.Context with { ToolCallId = "unloaded-logs" }, logs);
        Assert.True(denied.IsError);
        Assert.Equal("maker.development.unavailable", denied.ErrorCode);
        await fixture.LoadOutputAsync();
        await fixture.Operations.ReleaseAsync(fixture.Operations.GetSession(fixture.Project.EntryPoint)!);
        var result = await tools.ExecuteAsync(fixture.Context with { ToolCallId = "retained-logs" }, logs);
        Assert.False(result.IsError, result.Content);
        var query = Assert.IsType<PackageDevelopmentLogQuery>(fixture.Development.LastLogQuery);
        Assert.Equal(since, query.Since);
        Assert.Equal(Sunder.Sdk.Logging.PackageLogLevel.Error, query.MinimumLevel);
        Assert.Equal("runtime", query.Source);
        Assert.Equal(7, query.MaximumEntries);
        Assert.Equal(4096, query.MaximumBytes);
        Assert.Single(fixture.Development.Loads);
        Assert.True(fixture.Development.Sessions[0].Disposed);
        Assert.Empty(fixture.Backend.Starts);
        Assert.Equal(0, fixture.PageResolutions);
    }

    [Fact]
    public async Task GeneratedToolReloadUsesAlreadyBuiltOutputAndDuplicateCallOnlyObservesItsOriginalReceipt()
    {
        await using var fixture = new RelayFixture();
        var tools = await fixture.ConnectToolsAsync();
        await fixture.LoadOutputAsync();
        Assert.Null(await tools.PreflightAsync(fixture.Context, fixture.Reload));
        var accepted = await tools.ExecuteAsync(fixture.Context, fixture.Reload);
        Assert.False(accepted.IsError, accepted.Content);
        using var acceptance = JsonDocument.Parse(accepted.Content!);
        Assert.Equal("applied", acceptance.RootElement.GetProperty("status").GetString());
        Assert.False(accepted.WasTruncated);
        Assert.Equal(fixture.BuiltIdentity, Assert.Single(fixture.Development.Sessions[0].Reloads).Item2);
        Assert.False(acceptance.RootElement.TryGetProperty("job", out _));

        fixture.Backend.Projects[fixture.Project.EntryPoint] = fixture.Project with { SourceRevision = "later-source" };
        var repeated = await tools.ExecuteAsync(fixture.Context, fixture.Reload);
        Assert.False(repeated.IsError, repeated.Content);
        Assert.Equal(accepted.Content, repeated.Content);
        Assert.Empty(fixture.Backend.Starts);
        Assert.Single(fixture.Development.Loads);
        Assert.Single(Assert.Single(fixture.Development.Sessions).Reloads);
        Assert.Equal(0, fixture.PageResolutions);
    }

    [Theory]
    [InlineData("missing.csproj")]
    [InlineData("sunder.package.json")]
    public async Task GeneratedInspectionRejectionIsCorrectiveWithoutFaultingEitherProvider(string entryName)
    {
        await using var fixture = new RelayFixture();
        var tools = await fixture.ConnectToolsAsync();
        var catalog = fixture.AppServices.GetRequiredService<Sunder.Sdk.Services.IServiceProviders<BuilderApp::Sunder.Generated.Packages.Younics_Sunder_Package_Maker_Services.IBuilderRuntimeService>>();
        var gateway = new App.BuilderBackendGateway(catalog);
        fixture.Backend.Inspection = (entry, token) => entry == fixture.Project.EntryPoint
            ? Task.FromResult(fixture.Project) : gateway.InspectAsync(entry, token);
        if (entryName == "sunder.package.json") await File.WriteAllTextAsync(Path.Combine(fixture.Project.ProjectFolder, entryName), "{invalid-json");
        // Real discovery, generated owner-only inspection, App relay and generated
        // tool-source boundary. No fixture backend can turn this into a success.
        var invalid = new AgentToolRequest("maker_apply", JsonSerializer.Serialize(new
            { package = Path.Combine(fixture.Project.ProjectFolder, entryName) }));
        var rejected = await tools.PreflightAsync(fixture.Context, invalid);
        Assert.NotNull(rejected);
        Assert.True(rejected.IsError);
        Assert.Equal("maker.invalid-request", rejected.ErrorCode);
        Assert.Empty(fixture.Backend.Starts);
        Assert.Empty(fixture.Development.Loads);
        Assert.True(fixture.Runtime.HasDevelopmentApp);
        Assert.Equal("package-maker", (await tools.DescribeAsync()).SourceId);
        await fixture.LoadOutputAsync();
        Assert.Null(await tools.PreflightAsync(fixture.Context, fixture.Reload));
        var accepted = await tools.ExecuteAsync(fixture.Context, fixture.Reload);
        Assert.False(accepted.IsError, accepted.Content);
        using var receipt = JsonDocument.Parse(accepted.Content!);
        Assert.Equal("applied", receipt.RootElement.GetProperty("status").GetString());
        Assert.Empty(fixture.Backend.Starts);
        Assert.Single(Assert.Single(fixture.Development.Sessions).Reloads);
    }

    [Theory]
    [InlineData(false, App.BuilderProjectKind.Dotnet)]
    [InlineData(true, App.BuilderProjectKind.Dotnet)]
    [InlineData(false, App.BuilderProjectKind.Node)]
    [InlineData(true, App.BuilderProjectKind.Node)]
    public async Task GeneratedHotreloadUsesTheSameOwnerLoadedByUiOrSharedOperations(bool throughUi, App.BuilderProjectKind kind)
    {
        await using var fixture = new RelayFixture(kind);
        var tools = await fixture.ConnectToolsAsync();
        fixture.Development.PackageIdResolver = _ => fixture.Project.PackageId;
        fixture.BuiltIdentity = await BuilderTestDevelopmentOutput.WriteAsync(fixture.Project, Path.Combine(fixture.Project.ProjectFolder, "sunder-dev"));
        App.BuilderViewModel? model = null;
        if (throughUi)
        {
            model = new(fixture.Application, new(new BuilderUiStore(), new BuilderUiStateUpdates(), TimeSpan.FromMinutes(1)),
                new(), new BuilderUiDispatcher(), fixture.Development, fixture.Development,
                NullPackageSettingsNavigationService.Instance, fixture.Operations);
            await model.OpenFolderAsync(fixture.Project.ProjectFolder);
            await model.LoadSelectedProjectAsync();
        }
        else await fixture.Operations.LoadAsync(fixture.Project, Path.Combine(fixture.Project.ProjectFolder, "sunder-dev"), default);
        var owner = Assert.Single(fixture.Development.Sessions);
        var buildsBefore = fixture.Backend.Starts.Count;
        var request = fixture.Reload;
        var context = fixture.Context with { ToolCallId = "hotreload-call" };
        Assert.Null(await tools.PreflightAsync(context, request));
        var acceptance = await tools.ExecuteAsync(context, request);
        Assert.False(acceptance.IsError, acceptance.Content);
        using var receipt = JsonDocument.Parse(acceptance.Content!);
        Assert.Equal("applied", receipt.RootElement.GetProperty("status").GetString());
        Assert.Equal(buildsBefore, fixture.Backend.Starts.Count);
        Assert.Equal(fixture.BuiltIdentity, Assert.Single(owner.Reloads).Item2);
        Assert.Single(fixture.Development.Loads);
        Assert.False(owner.Disposed);
        Assert.Same(owner, fixture.Operations.GetSession(fixture.Project.EntryPoint)!.Handle);
        // Opening or using the UI later borrows this same owner; a duplicate tool
        // call observes its receipt rather than compiling or loading again.
        Assert.False((await tools.ExecuteAsync(context, request)).IsError);
        Assert.Equal(buildsBefore, fixture.Backend.Starts.Count);
        if (model is not null) Assert.True(model.SelectedProject!.HasSession);
        if (model is not null) await model.DisposeAsync();
    }

    [Fact]
    public async Task GeneratedBridgePreservesExpectedLeaseContentionInsteadOfFaultingTheAppConsumer()
    {
        await using var fixture = new RelayFixture();
        fixture.StartApp();
        await fixture.WaitConnectedAsync();
        var catalog = fixture.AppServices.GetRequiredService<Sunder.Sdk.Services.IServiceProviders<BuilderApp::Sunder.Generated.Packages.Younics_Sunder_Package_Maker_Services.IBuilderRuntimeService>>();
        var worker = Assert.Single((await catalog.ListAsync()).Providers).Service;
        var busy = await Assert.ThrowsAsync<SunderRpcException>(() => worker.ConnectDevelopmentAppAsync(Guid.NewGuid().ToString("N")).AsTask());
        Assert.Equal(SunderRpcErrorKind.Domain, busy.Error.Kind);
        Assert.Equal("agent.builder.development-unavailable", busy.Error.Code);

        // A successor can start before the original lease expires. It must wait
        // rather than fail its activation root or steal the old command queue.
        await using var successor = new App.BuilderDevelopmentService(fixture.Operations, fixture.Application, catalog);
        var before = fixture.Transport.Calls.Count(method => method == "connect-development-app");
        successor.Start(default);
        await BuilderUiFixture.WaitAsync(() => fixture.Transport.Calls.Count(method => method == "connect-development-app") >= before + 2);
        await successor.DisposeAsync();
        Assert.Empty(fixture.Backend.Starts);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("configuration")]
    [InlineData("binding")]
    [InlineData("workspace")]
    [InlineData("target-retirement")]
    public async Task GeneratedToolRevalidatesAuthorizedInputsBeforeMutation(string boundary)
    {
        await using var fixture = new RelayFixture();
        var tools = await fixture.ConnectToolsAsync();
        Assert.Null(await tools.PreflightAsync(fixture.Context, fixture.Reload));
        var context = fixture.Context;
        switch (boundary)
        {
            case "source": fixture.Backend.Projects[fixture.Project.EntryPoint] = fixture.Project with { SourceRevision = "changed" }; break;
            case "configuration": fixture.Target.Configuration = "changed"; break;
            case "binding": context = context with { ExecutionBinding = context.ExecutionBinding! with { UpdatedAtUtc = context.ExecutionBinding.UpdatedAtUtc.AddSeconds(1) } }; break;
            case "workspace": context = context with { Workspace = context.Workspace! with { UpdatedAtUtc = context.Workspace.UpdatedAtUtc.AddSeconds(1) } }; break;
            case "target-retirement": await fixture.State.RetireAsync(fixture.Target); break;
        }
        var result = await tools.ExecuteAsync(context, fixture.Reload);
        Assert.True(result.IsError, "Changed authorized inputs must not admit a reload.");
        Assert.Equal("package-maker", (await tools.DescribeAsync()).SourceId);
        Assert.Empty(fixture.Backend.Starts);
        Assert.Empty(fixture.Development.Loads);
    }

    [Fact]
    public async Task LostAcceptanceAcknowledgementCanOnlyObserveTheOriginalOperation()
    {
        await using var fixture = new RelayFixture();
        var tools = await fixture.ConnectToolsAsync();
        await fixture.LoadOutputAsync();
        Assert.Null(await tools.PreflightAsync(fixture.Context, fixture.Reload));
        var completing = BuilderUiFixture.Signal();
        var release = BuilderUiFixture.Signal();
        fixture.Transport.Before = async (method, request, token) =>
        {
            if (method != "complete-development-command" || !ContainsOperationReceipt(request)) return;
            completing.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        using var caller = new CancellationTokenSource();
        var execute = tools.ExecuteAsync(fixture.Context, fixture.Reload, caller.Token).AsTask();
        try
        {
            await completing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await BuilderUiFixture.WaitAsync(() => !fixture.Development.Sessions[0].Reloads.IsEmpty);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execute.WaitAsync(TimeSpan.FromSeconds(5)));
            fixture.Transport.Before = null;
            release.TrySetResult();
            var recovered = await tools.ExecuteAsync(fixture.Context, fixture.Reload).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(recovered.IsError, recovered.Content);
            using var receipt = JsonDocument.Parse(recovered.Content!);
            Assert.Equal("applied", receipt.RootElement.GetProperty("status").GetString());
            Assert.Empty(fixture.Backend.Starts);
            Assert.Single(fixture.Development.Sessions[0].Reloads);
            Assert.Equal(recovered.Content, (await tools.ExecuteAsync(fixture.Context, fixture.Reload)).Content);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task AppRevocationCancelsAnAcceptedReloadAndTheOutstandingGeneratedReadWithoutBuilding()
    {
        await using var fixture = new RelayFixture();
        var tools = await fixture.ConnectToolsAsync();
        await fixture.LoadOutputAsync();
        var entered = BuilderUiFixture.Signal();
        var cancelled = BuilderUiFixture.Signal();
        var readEntered = BuilderUiFixture.Signal();
        var readCancelled = BuilderUiFixture.Signal();
        fixture.Development.Sessions[0].OnReload = async (_, _, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { if (token.IsCancellationRequested) cancelled.TrySetResult(); }
            throw new InvalidOperationException("The reload must be cancelled by its owning activation.");
        };
        Assert.Null(await tools.PreflightAsync(fixture.Context, fixture.Reload));
        fixture.Transport.Before = async (method, _, token) =>
        {
            if (method != "read-development-command" || !entered.Task.IsCompleted) return;
            readEntered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { if (token.IsCancellationRequested) readCancelled.TrySetResult(); }
        };
        using var caller = new CancellationTokenSource();
        var executing = tools.ExecuteAsync(fixture.Context, fixture.Reload, caller.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Activation.CancelAsync();
        await fixture.AppServices.GetRequiredService<App.BuilderDevelopmentService>().DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await readCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The current tool waits for observation, unlike the retired admission-only
        // reply. Stopping its caller is separate from revoking the App-owned work/read.
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(fixture.Backend.Starts);
        Assert.Single(fixture.Development.Sessions[0].Reloads);
        Assert.Equal(0, fixture.PageResolutions);
    }

    [Fact]
    public async Task WorkerRetirementDuringGeneratedReadCannotDispatchTheDequeuedMutation()
    {
        await using var fixture = new RelayFixture();
        var tools = await fixture.ConnectToolsAsync();
        Assert.Null(await tools.PreflightAsync(fixture.Context, fixture.Reload));
        var read = BuilderUiFixture.Signal();
        var emptyBatchBytes = System.Text.Encoding.UTF8.GetByteCount(Worker.BuilderContent.Serialize(new Worker.BuilderDevelopmentCommandBatch(null)));
        fixture.Transport.After = async (method, response, token) =>
        {
            if (method != "read-development-command" || response.GetProperty("length").GetInt64() == emptyBatchBytes) return;
            // A timed-out empty poll cannot satisfy this barrier: the generated
            // content metadata must identify a nonempty, already dequeued batch.
            read.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        using var caller = new CancellationTokenSource();
        var execute = tools.ExecuteAsync(fixture.Context, fixture.Reload, caller.Token).AsTask();
        await read.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.State.RetireAsync(fixture.Runtime).WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execute.WaitAsync(TimeSpan.FromSeconds(5)));
        await fixture.Activation.CancelAsync();
        await fixture.AppServices.GetRequiredService<App.BuilderDevelopmentService>().DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(fixture.Backend.Starts);
        Assert.Empty(fixture.Development.Loads);
    }

    private sealed class RelayFixture : IAsyncDisposable
    {
        private const string MakerId = "younics.sunder.package.maker";
        private readonly RegressionTestPackageScope _package = RegressionTestPackageScope.Create();
        private readonly BuilderInspectionOnlyContext _inspectionContext = new();
        private readonly GeneratedServiceTestScope _receiver;
        private readonly GeneratedServiceTestScope _agent;
        private readonly ConcurrentBag<GeneratedServiceTestScope> _owned = [];
        internal RegressionTestRpcState State { get; } = new(workerStyleProviderBoundary: true);
        internal Worker.BuilderRuntimeService Runtime { get; }
        internal BuilderUiBackend Backend { get; } = new();
        internal BuilderUiDevelopment Development { get; } = new();
        internal App.BuilderProjectApplicationService Application { get; }
        internal App.BuilderPackageOperations Operations { get; }
        internal App.BuilderProjectInspection Project { get; }
        internal ISunderAppPackageModule Module { get; } = new BuilderApp::Sunder.Generated.Package.AppModule();
        internal ServiceProvider AppServices { get; }
        internal CancellationTokenSource Activation { get; } = new();
        internal TransportProbe Transport { get; }
        internal LocalTarget Target { get; } = new();
        internal AgentToolExecutionContext Context { get; private set; } = null!;
        internal AgentToolRequest Reload => new("maker_apply", JsonSerializer.Serialize(new { package = Project.EntryPoint }));
        internal string? BuiltIdentity;
        internal int PageResolutions;

        internal RelayFixture(App.BuilderProjectKind kind = App.BuilderProjectKind.Dotnet)
        {
            Runtime = new(_inspectionContext);
            var client = new RegressionTestRpcClient(State, MakerId);
            _receiver = new(client);
            _agent = new(client.ForCaller("younics.ai.agent"));
            var registration = _receiver.Register(Runtime, MakerContract.Descriptor.ContractId);
            Transport = new(registration.Handler);
            // This exercises the current generated private contract. Independent
            // byte-record acceptance remains in the existing artifact suite.
            State.Add(MakerId, MakerContract.Descriptor, Runtime, Transport, Noop.Instance, registration.ProviderId, "2.0.0");
            var root = new App.BuilderPathService().ResolveContainedHostPath(_package.RootPath, Path.GetTempPath());
            Project = BuilderUiFixture.Project("relay", kind) with { ProjectFolder = root,
                EntryPoint = Path.Combine(root, kind == App.BuilderProjectKind.Dotnet ? "Package.csproj" : "sunder.package.json"),
                TargetFramework = kind == App.BuilderProjectKind.Dotnet ? "net10.0" : null };
            Backend.Projects[Project.EntryPoint] = Project;
            var store = new BuilderUiStore();
            var queue = new BuilderUiBackgroundQueue();
            Application = new(Backend, store, new(queue), new App.BuilderJobJournal(new BuilderMemoryJournalStorage(), new BuilderUiStateUpdates()));
            Operations = new(Application, Development);
            var services = new ServiceCollection();
            Module.ConfigureAppServices(services, _package.Context);
            services.AddSingleton<ISunderRpcClient>(client);
            services.AddSingleton(Application);
            services.AddSingleton(Operations);
            services.AddSingleton<App.BuilderViewModel>(_ => { Interlocked.Increment(ref PageResolutions); throw new InvalidOperationException("Startup must not resolve the page model."); });
            services.AddTransient<App.BuilderView>(_ => { Interlocked.Increment(ref PageResolutions); throw new InvalidOperationException("Startup must not resolve the page."); });
            AppServices = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        }

        internal void StartApp() => Module.StartAppServices(AppServices, Activation.Token);
        internal async Task LoadOutputAsync()
        {
            Development.PackageIdResolver = _ => Project.PackageId;
            var folder = Path.Combine(Project.ProjectFolder, "sunder-dev");
            BuiltIdentity = await BuilderTestDevelopmentOutput.WriteAsync(Project, folder);
            await Operations.LoadAsync(Project, folder, default);
        }
        internal Task WaitConnectedAsync() => BuilderUiFixture.WaitAsync(() => Runtime.HasDevelopmentApp);
        internal string SourceRequest() => JsonSerializer.Serialize(new { entryPoint = Project.EntryPoint, roots = new[] { Project.ProjectFolder }, expectedSourceRevision = (string?)null });
        internal async Task<IToolSourceService> ConnectToolsAsync()
        {
            var source = new Worker.BuilderToolSource(Runtime);
            State.AddGenerated(MakerId, source, _receiver.Register(source, RegressionTestBindings.ToolSources), Noop.Instance, "maker.tools");
            var targetReceiver = new GeneratedServiceTestScope(new RegressionTestRpcClient(State, "test.local.target"));
            _owned.Add(targetReceiver);
            using var providers = new RegressionTestSourceProviders();
            State.AddGenerated("test.local.target", Target, providers.Create(RegressionTestBindings.ExecutionTargets, Target, targetReceiver), Noop.Instance, "test.local");
            var target = Assert.Single((await _agent.Providers<IExecutionTargetService>().ListAsync()).Providers);
            var now = DateTimeOffset.UtcNow;
            var workspace = new AgentWorkspaceRecord("relay-workspace", "Relay workspace", null, now, now,
                [new("relay-root", "relay-workspace", Project.ProjectFolder, true, 0, now, now)]);
            var binding = new AgentWorkspaceBindingRecord("relay-binding", workspace.WorkspaceId, RegressionTestBindings.ExecutionTargets, "local", "primary", true, 0, now, now);
            Context = new(Guid.NewGuid(), Workspace: workspace, ExecutionBinding: binding, RunId: Guid.NewGuid(), RunRevision: 1, ToolCallId: "build-call")
            { ExecutionTarget = target.Reference, ExecutionTargetConfigurationGeneration = Target.Configuration };
            StartApp();
            await WaitConnectedAsync();
            return Assert.Single((await _agent.Providers<IToolSourceService>().ListAsync()).Providers).Service;
        }

        public async ValueTask DisposeAsync()
        {
            await Activation.CancelAsync();
            try { await AppServices.DisposeAsync(); }
            finally
            {
                try { await Operations.DisposeAsync(); await Application.DisposeAsync(); }
                finally
                {
                    await Runtime.DisposeAsync();
                    await _agent.DisposeAsync();
                    await _receiver.DisposeAsync();
                    foreach (var owner in _owned) await owner.DisposeAsync();
                    State.Dispose(); Activation.Dispose(); _package.Dispose();
                    Assert.Equal(0, _inspectionContext.ForbiddenAccesses);
                }
            }
        }
    }

    // Transparent failure/barrier injection around actual generated dispatch and
    // content registration, never a substitute service or manually encoded reply.
    private sealed class TransportProbe(ISunderRpcServiceHandler generated) : ISunderRpcServiceHandler
    {
        internal ConcurrentQueue<string> Calls { get; } = new();
        internal Func<string, JsonElement, CancellationToken, Task>? Before { get; set; }
        internal Func<string, JsonElement, CancellationToken, Task>? After { get; set; }
        public async ValueTask<JsonElement> InvokeUnaryAsync(SunderRpcInvocationContext context, string serviceId, string methodId, JsonElement request, CancellationToken token = default)
        {
            Calls.Enqueue(methodId);
            if (Before is { } before) await before(methodId, request, token);
            var response = await generated.InvokeUnaryAsync(context, serviceId, methodId, request, token);
            if (After is { } after) await after(methodId, response, token);
            return response;
        }
        public IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(SunderRpcInvocationContext context, string serviceId, string methodId, JsonElement request, CancellationToken token = default)
            => generated.InvokeServerStreamAsync(context, serviceId, methodId, request, token);
    }

    private static bool ContainsOperationReceipt(JsonElement request)
    {
        foreach (var property in request.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) continue;
            try
            {
                using var response = JsonDocument.Parse(property.Value.GetString()!);
                if (response.RootElement.ValueKind == JsonValueKind.Object
                    && response.RootElement.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String)
                    return true;
            }
            catch (JsonException) { }
        }
        return false;
    }

    private sealed class LocalTarget : IAgentExecutionTarget
    {
        internal string Configuration = "initial";
        public AgentExecutionTargetDescriptor Descriptor { get; } = new("local", "local", "Test local target", null, false, false);
        public ValueTask<string?> GetConfigurationGenerationAsync(AgentExecutionTargetContext context, CancellationToken token = default) => ValueTask.FromResult<string?>(Configuration);
        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(AgentExecutionTargetContext context, CancellationToken token = default)
            => ValueTask.FromResult(new AgentExecutionTargetReadiness("local", "local",
                context.ExpectedConfigurationGeneration == Configuration ? AgentExecutionTargetReadinessStatus.Ready : AgentExecutionTargetReadinessStatus.Failed,
                context.ExpectedConfigurationGeneration == Configuration ? "Ready" : "Configuration changed."));
        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(AgentExecutionTargetContext context, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(AgentExecutionTargetContext context, string path, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask<AgentFileReadResult> ReadFileAsync(AgentExecutionTargetContext context, AgentFileReadRequest request, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask<AgentFileMutationResult> WriteFileAsync(AgentExecutionTargetContext context, AgentFileWriteRequest request, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask<AgentFileMutationResult> DeleteFileAsync(AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken token = default) => throw new NotSupportedException();
    }

    private sealed class Noop : IDisposable
    {
        internal static Noop Instance { get; } = new();
        public void Dispose() { }
    }
}
