using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Rpc.Owner;

[SunderRpcProvider(typeof(IPackageStackContributor), ProviderId = "agent.stack.profiles")]
public sealed partial class AgentProfileStackProvider(IPackageStackContributor contributor) : AgentStackProvider(contributor);

[SunderRpcProvider(typeof(IPackageStackContributor), ProviderId = "agent.stack.workspaces")]
public sealed partial class AgentWorkspaceStackProvider(IPackageStackContributor contributor) : AgentStackProvider(contributor);

// The package-local generated platform binding supplies wire types, validation and
// dispatch. These adapters retain the actual contributors' domain behavior.
public abstract class AgentStackProvider : IPackageStackExporter, IPackageStackImporter, IPackageStackImportAppliedHandler
{
    private readonly IPackageStackExporter _exporter;
    private readonly IPackageStackImporter _importer;
    private readonly IPackageStackImportAppliedHandler _applied;

    protected AgentStackProvider(IPackageStackContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(contributor);
        _exporter = contributor as IPackageStackExporter ?? throw new ArgumentException("An Agent Stack contributor must export.", nameof(contributor));
        _importer = contributor as IPackageStackImporter ?? throw new ArgumentException("An Agent Stack contributor must import.", nameof(contributor));
        _applied = contributor as IPackageStackImportAppliedHandler ?? throw new ArgumentException("An Agent Stack contributor must handle applied imports.", nameof(contributor));
        ContributorId = contributor.ContributorId;
        DisplayName = contributor.DisplayName;
    }

    public string ContributorId { get; }
    public string DisplayName { get; }
    public ValueTask<IReadOnlyList<StackExportItemDescriptor>> ListExportItemsAsync(StackExportDiscoveryContext request, CancellationToken token = default)
        => _exporter.ListExportItemsAsync(request, token);
    public ValueTask<StackExportContribution> ExportAsync(StackExportRequest request, CancellationToken token = default)
        => _exporter.ExportAsync(request, token);
    public ValueTask<StackImportPreview> PreviewImportAsync(StackImportPreviewRequest request, CancellationToken token = default)
        => _importer.PreviewImportAsync(request, token);
    public ValueTask<StackImportResult> ImportAsync(StackImportRequest request, CancellationToken token = default)
        => _importer.ImportAsync(request, token);
    public ValueTask OnStackImportAppliedAsync(StackImportAppliedContext request, CancellationToken token = default)
        => _applied.OnStackImportAppliedAsync(request, token);
}
