extern alias SubagentsApp;
extern alias SubagentsWorker;

using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using SubagentsApp::Sunder.Package.Agent.Contracts.Models;
using SubagentsApp::Sunder.Package.Agent.Subagents.Models;
using SubagentsApp::Sunder.Package.Agent.Subagents.Runtime;
using SubagentsApp::Sunder.Package.Agent.Shared.Presentation;
using WorkerRuntime = SubagentsWorker::Sunder.Package.Agent.Subagents.Runtime;
using WorkerRpc = SubagentsWorker::Sunder.Package.Agent.Subagents.Rpc;
using WorkerService = SubagentsWorker::Sunder.Package.Agent.Subagents.Services.SubagentService;

namespace Sunder.Package.Agent.TestSupport.Avalonia;

// Real Worker operations -> generated handler -> scoped content -> generated App
// client. Domain models never cross this boundary by CLR cast or shared reference.
internal sealed class WorkerBackedSubagentManagementGateway : ISubagentManagementGateway,
    ISubsessionSessionReader, ISubsessionCheckpointReader, ISubsessionTranscriptPageReader,
    ISubsessionChangeNotifications, ISubagentPresentationInitialization, IDisposable, IAsyncDisposable
{
    private const string PackageId = "sunder.package.agent.subagents";
    private readonly RegressionTestExtensionCatalog _catalog;
    private readonly WorkerRpc.SubagentRuntimeBindings _bindings;
    private readonly WorkerRuntime.SubagentRuntimeHandler _handler;
    private readonly WorkerRuntime.SubagentRuntimeChangeStream _changes;
    private readonly SubagentAppRuntimeGateway _app;
    private readonly Task _initialization;
    private Task? _disposal;
    private readonly object _gate = new();

    internal static WorkerBackedSubagentManagementGateway ForRuntime(IPackageContext context, RegressionTestExtensionCatalog catalog)
        => new(new WorkerService(new SubagentsWorker::Sunder.Package.Agent.Subagents.Services.SubagentStore(context)), catalog);

    internal WorkerBackedSubagentManagementGateway(WorkerService service, RegressionTestExtensionCatalog catalog)
    {
        _catalog = catalog;
        var rpc = catalog.RpcForCaller(PackageId);
        _bindings = new WorkerRpc.SubagentRuntimeBindings(rpc);
        _bindings.StartAsync().GetAwaiter().GetResult();
        _handler = new WorkerRuntime.SubagentRuntimeHandler(service, _bindings);
        _changes = new WorkerRuntime.SubagentRuntimeChangeStream(service, _bindings);
        var operations = new WorkerRuntime.SubagentRuntimeOwnerClient(_handler, _changes);
        var provider = new WorkerRpc.ManagementService(operations);
        catalog.AddGeneratedProvider(PackageId, this, provider.CreateSunderRpcProviderRegistration());
        _app = new SubagentAppRuntimeGateway(rpc);
        // Avoid capturing an Avalonia dispatcher for the transport observation loop.
        _initialization = Task.Run(() => _app.InitializeAsync());
    }

    public event Action? SubagentsChanged
    {
        add => _app.SubagentsChanged += value;
        remove => _app.SubagentsChanged -= value;
    }

    public event Action? CatalogChanged
    {
        add => _app.CatalogChanged += value;
        remove => _app.CatalogChanged -= value;
    }

    public event Action<Guid>? SessionChanged { add => _app.SessionChanged += value; remove => _app.SessionChanged -= value; }
    public event Action<Guid, AgentTurnRecord>? TurnChanged { add => _app.TurnChanged += value; remove => _app.TurnChanged -= value; }
    public event Action? ResnapshotRequired { add => _app.ResnapshotRequired += value; remove => _app.ResnapshotRequired -= value; }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => _initialization.WaitAsync(cancellationToken);
    public Task<SubsessionSessionCatalog> ListSessionsAsync(CancellationToken cancellationToken = default) => _app.ListSessionsAsync(cancellationToken);
    public Task<IReadOnlyList<AgentRunCheckpointRecord>> ListLatestCheckpointsAsync(CancellationToken cancellationToken = default) => _app.ListLatestCheckpointsAsync(cancellationToken);
    public Task<SubsessionTranscriptPage> ListRecentTurnsAsync(Guid sessionId, int limit, CancellationToken cancellationToken = default)
        => _app.ListRecentTurnsAsync(sessionId, limit, cancellationToken);
    public Task<SubsessionTranscriptPage> ListTurnsBeforeAsync(Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit, CancellationToken cancellationToken = default)
        => _app.ListTurnsBeforeAsync(sessionId, beforeCreatedAtUtc, beforeTurnId, limit, cancellationToken);
    public Task<SubsessionTranscriptPage> ListTurnsAfterAsync(Guid sessionId, DateTimeOffset afterCreatedAtUtc, Guid afterTurnId, int limit, CancellationToken cancellationToken = default)
        => _app.ListTurnsAfterAsync(sessionId, afterCreatedAtUtc, afterTurnId, limit, cancellationToken);
    public Task<SubsessionAroundTurnPage> LoadAroundTurnAsync(Guid sessionId, Guid turnId, DateTimeOffset turnCreatedAtUtc, Guid itemId,
        int beforeLimit, int afterLimit, CancellationToken cancellationToken = default)
        => _app.LoadAroundTurnAsync(sessionId, turnId, turnCreatedAtUtc, itemId, beforeLimit, afterLimit, cancellationToken);
    public Task<AgentTranscriptToolDetailRecord?> LoadToolDetailAsync(AgentTranscriptToolDetailRequest request, CancellationToken cancellationToken = default)
        => _app.LoadToolDetailAsync(request, cancellationToken);

    public Task<IReadOnlyList<SubagentRecord>> ListSubagentsAsync(CancellationToken cancellationToken = default)
        => _app.ListSubagentsAsync(cancellationToken);

    public Task<SubagentRecord> CreateSubagentAsync(string displayName, CancellationToken cancellationToken = default)
        => _app.CreateSubagentAsync(displayName, cancellationToken);

    public Task<SubagentRecord> SaveSubagentAsync(SubagentSaveRequest request, CancellationToken cancellationToken = default)
        => _app.SaveSubagentAsync(request, cancellationToken);

    public Task DeleteSubagentAsync(string subagentId, CancellationToken cancellationToken = default)
        => _app.DeleteSubagentAsync(subagentId, cancellationToken);

    public Task<IReadOnlyList<ProviderCatalogOption>> ListChatProvidersAsync(CancellationToken cancellationToken = default)
        => _app.ListChatProvidersAsync(cancellationToken);

    public Task<ProviderModelCatalogResult> LoadChatModelsAsync(string providerId, CancellationToken cancellationToken = default)
        => _app.LoadChatModelsAsync(providerId, cancellationToken);

    public Task<IReadOnlyList<AgentToolDescriptor>> ListLocalToolsAsync(CancellationToken cancellationToken = default)
        => _app.ListLocalToolsAsync(cancellationToken);

    public Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListPackageCapabilitiesAsync(CancellationToken cancellationToken = default)
        => _app.ListPackageCapabilitiesAsync(cancellationToken);

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_disposal ??= StopAsync());
    }

    private async Task StopAsync()
    {
        try
        {
            await _initialization.ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try { await _catalog.RetireProviderAsync(this).ConfigureAwait(false); }
            finally
            {
                _changes.Dispose();
                _handler.Dispose();
                await _bindings.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
