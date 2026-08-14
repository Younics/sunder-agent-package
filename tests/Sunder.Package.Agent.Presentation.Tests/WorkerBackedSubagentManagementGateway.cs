extern alias SubagentsApp;
extern alias SubagentsWorker;

using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Package.Agent.Subagents.Runtime;
using AppGateway = SubagentsApp::Sunder.Package.Agent.Subagents.Runtime.ISubagentManagementGateway;
using AppProviderCatalogOption = SubagentsApp::Sunder.Package.Agent.Shared.Presentation.ProviderCatalogOption;
using AppProviderModelCatalogOption = SubagentsApp::Sunder.Package.Agent.Shared.Presentation.ProviderModelCatalogOption;
using AppProviderModelCatalogResult = SubagentsApp::Sunder.Package.Agent.Shared.Presentation.ProviderModelCatalogResult;
using WorkerGateway = SubagentsWorker::Sunder.Package.Agent.Subagents.Runtime.SubagentLocalManagementGateway;

namespace Sunder.Package.Agent.Presentation.Tests;

internal sealed class WorkerBackedSubagentManagementGateway(
    SubagentService service,
    Sunder.Package.Agent.Protocol.AgentRpcCatalog rpcCatalog) : AppGateway, IDisposable
{
    private readonly WorkerGateway _worker = new(service, rpcCatalog);

    public event Action? SubagentsChanged
    {
        add => _worker.SubagentsChanged += value;
        remove => _worker.SubagentsChanged -= value;
    }

    public event Action? CatalogChanged
    {
        add => _worker.CatalogChanged += value;
        remove => _worker.CatalogChanged -= value;
    }

    public Task<IReadOnlyList<SubagentRecord>> ListSubagentsAsync(
        CancellationToken cancellationToken = default)
        => _worker.ListSubagentsAsync(cancellationToken);

    public Task<SubagentRecord> CreateSubagentAsync(
        string displayName,
        CancellationToken cancellationToken = default)
        => _worker.CreateSubagentAsync(displayName, cancellationToken);

    public Task<SubagentRecord> SaveSubagentAsync(
        SubagentSaveRequest request,
        CancellationToken cancellationToken = default)
        => _worker.SaveSubagentAsync(request, cancellationToken);

    public Task DeleteSubagentAsync(
        string subagentId,
        CancellationToken cancellationToken = default)
        => _worker.DeleteSubagentAsync(subagentId, cancellationToken);

    public async Task<IReadOnlyList<AppProviderCatalogOption>> ListChatProvidersAsync(
        CancellationToken cancellationToken = default)
        => (await _worker.ListChatProvidersAsync(cancellationToken).ConfigureAwait(false))
            .Select(static provider => new AppProviderCatalogOption(
                provider.Id,
                provider.Label,
                provider.PackageId))
            .ToArray();

    public async Task<AppProviderModelCatalogResult> LoadChatModelsAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var result = await _worker.LoadChatModelsAsync(providerId, cancellationToken)
            .ConfigureAwait(false);
        return new AppProviderModelCatalogResult(
            result.Models.Select(static model => new AppProviderModelCatalogOption(
                model.Id,
                model.Label,
                model.Variants,
                model.SpeedOptions,
                model.ModeOptions)).ToArray(),
            result.StatusText,
            result.WarningText);
    }

    public Task<IReadOnlyList<AgentToolDescriptor>> ListLocalToolsAsync(
        CancellationToken cancellationToken = default)
        => _worker.ListLocalToolsAsync(cancellationToken);

    public Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListPackageCapabilitiesAsync(
        CancellationToken cancellationToken = default)
        => _worker.ListPackageCapabilitiesAsync(cancellationToken);

    public void Dispose() => _worker.Dispose();
}
