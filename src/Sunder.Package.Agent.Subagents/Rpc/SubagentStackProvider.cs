using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Subagents.Rpc;

[SunderRpcProvider(typeof(IPackageStackContributor), ProviderId = "subagents.stack")]
public sealed partial class SubagentStackProvider : IPackageStackExporter, IPackageStackImporter, IPackageStackImportAppliedHandler
{
    private readonly IPackageStackExporter _exporter;
    private readonly IPackageStackImporter _importer;
    private readonly IPackageStackImportAppliedHandler _applied;
    public SubagentStackProvider(IPackageStackContributor contributor)
    {
        _exporter = contributor as IPackageStackExporter ?? throw new ArgumentException("The Subagents Stack contributor must export.", nameof(contributor));
        _importer = contributor as IPackageStackImporter ?? throw new ArgumentException("The Subagents Stack contributor must import.", nameof(contributor));
        _applied = contributor as IPackageStackImportAppliedHandler ?? throw new ArgumentException("The Subagents Stack contributor must handle applied imports.", nameof(contributor));
        ContributorId = contributor.ContributorId; DisplayName = contributor.DisplayName;
    }
    public string ContributorId { get; }
    public string DisplayName { get; }
    public ValueTask<IReadOnlyList<StackExportItemDescriptor>> ListExportItemsAsync(StackExportDiscoveryContext context, CancellationToken token = default) => _exporter.ListExportItemsAsync(context, token);
    public ValueTask<StackExportContribution> ExportAsync(StackExportRequest request, CancellationToken token = default) => _exporter.ExportAsync(request, token);
    public ValueTask<StackImportPreview> PreviewImportAsync(StackImportPreviewRequest request, CancellationToken token = default) => _importer.PreviewImportAsync(request, token);
    public ValueTask<StackImportResult> ImportAsync(StackImportRequest request, CancellationToken token = default) => _importer.ImportAsync(request, token);
    public ValueTask OnStackImportAppliedAsync(StackImportAppliedContext context, CancellationToken token = default) => _applied.OnStackImportAppliedAsync(context, token);
}
