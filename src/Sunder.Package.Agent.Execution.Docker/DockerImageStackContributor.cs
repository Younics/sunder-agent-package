using System.Text.Json;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Execution.Docker;

internal sealed class DockerImageStackContributor(
    DockerImageCatalogService imageCatalog,
    IPackageContext packageContext) : IPackageStackContributor, IPackageStackImportAppliedHandler
{
    private const string ItemId = "docker-images";
    private const string SchemaId = "sunder.package.agent.execution.docker/images";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public string ContributorId => packageContext.PackageId + ".docker-images";

    public string DisplayName => "Docker Images";

    public async ValueTask<IReadOnlyList<StackExportItemDescriptor>> ListExportItemsAsync(
        StackExportDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        var details = (await imageCatalog.ListImagesAsync(cancellationToken))
            .Select(image => new StackExportItemDetail(
                "Image",
                image.ImageReference,
                StackValueSensitivity.Public,
                ValueWhenExcluded: "Not exported",
                DetailId: image.ImageReference,
                IsEditable: false))
            .ToArray();

        return details.Length == 0
            ? []
            : [new StackExportItemDescriptor(
                ItemId,
                "Docker Images",
                "docker-image",
                "Configure Docker image references when this Stack is used. Images are not pulled during import.",
                DefaultSelected: false,
                Sensitivities: [StackValueSensitivity.Public],
                Details: details)];
    }

    public async ValueTask<StackExportContribution> ExportAsync(
        StackExportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.ItemIds.Contains(ItemId, StringComparer.OrdinalIgnoreCase))
        {
            return new StackExportContribution([], [], []);
        }

        var selectedReferences = (await imageCatalog.ListImagesAsync(cancellationToken))
            .Select(image => image.ImageReference)
            .Where(reference => request.IsDetailSelected(ItemId, reference))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(reference => reference, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedReferences.Length == 0)
        {
            return new StackExportContribution([], [], ["No Docker images were selected for export."]);
        }

        var payload = new DockerImageStackPayload(selectedReferences);
        var fragment = new StackFragmentExport(
            FragmentId: "docker-images",
            ContributorId,
            SchemaId,
            SchemaVersion: 1,
            DisplayName: "Docker Images",
            JsonPayload: JsonSerializer.Serialize(payload, JsonOptions),
            Description: "Configured Docker image references. Importing the Stack configures references only; it does not pull images.",
            DefaultSelected: true,
            SourceItemId: ItemId);

        return new StackExportContribution([fragment], [CreatePackageRequirement()], []);
    }

    public async ValueTask<StackImportPreview> PreviewImportAsync(
        StackImportPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        var actions = new List<StackImportAction>();
        var warnings = new List<string>();
        foreach (var fragment in request.Fragments)
        {
            if (!TryReadPayload(fragment, warnings, out var payload) || payload is null)
            {
                continue;
            }

            foreach (var imageReference in payload.ImageReferences)
            {
                var containsImage = await imageCatalog.ContainsImageAsync(imageReference, cancellationToken);
                actions.Add(new StackImportAction(
                    BuildActionId(fragment.FragmentId, imageReference),
                    containsImage
                        ? $"Keep Docker image reference {imageReference}"
                        : $"Add Docker image reference {imageReference}",
                    containsImage ? StackImportActionKind.Reuse : StackImportActionKind.Create,
                    DefaultSelected: true,
                    Description: "Configures the Docker image reference without pulling the image."));
            }
        }

        return new StackImportPreview(actions, [], [], warnings);
    }

    public async ValueTask<StackImportResult> ImportAsync(
        StackImportRequest request,
        CancellationToken cancellationToken = default)
    {
        var selectedActionIds = request.SelectedActionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var imported = new List<StackImportedItem>();
        var warnings = new List<string>();
        var errors = new List<string>();
        foreach (var fragment in request.Fragments)
        {
            if (!TryReadPayload(fragment, warnings, out var payload) || payload is null)
            {
                continue;
            }

            foreach (var imageReference in payload.ImageReferences)
            {
                if (!selectedActionIds.Contains(BuildActionId(fragment.FragmentId, imageReference)))
                {
                    continue;
                }

                try
                {
                    var image = await imageCatalog.AddImageAsync(imageReference, cancellationToken);
                    imported.Add(new StackImportedItem(image.ImageReference, image.ImageReference, "docker-image"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"Failed to configure Docker image '{imageReference}': {ex.Message}");
                }
            }
        }

        var outcome = errors.Count == 0
            ? StackImportOutcome.Completed
            : imported.Count == 0 ? StackImportOutcome.Failed : StackImportOutcome.Partial;
        return new StackImportResult(outcome, imported, new Dictionary<string, string>(), warnings, errors);
    }

    public ValueTask OnStackImportAppliedAsync(
        StackImportAppliedContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.ImportedItems.Count > 0)
        {
            imageCatalog.NotifyImagesImported();
        }

        return ValueTask.CompletedTask;
    }

    private StackPackageRequirement CreatePackageRequirement()
        => new(packageContext.PackageId, CreatedWithVersion: packageContext.Version.ToString(), MinimumVersion: "1.0.0");

    private static string BuildActionId(string fragmentId, string imageReference)
        => "docker-image:" + fragmentId + ":" + imageReference;

    private static bool TryReadPayload(
        StackFragmentImport fragment,
        ICollection<string> warnings,
        out DockerImageStackPayload? payload)
    {
        payload = null;
        try
        {
            payload = JsonSerializer.Deserialize<DockerImageStackPayload>(fragment.JsonPayload, JsonOptions);
            if (payload is null || payload.ImageReferences.Count == 0)
            {
                warnings.Add($"Stack fragment '{fragment.FragmentId}' does not contain Docker image references.");
                payload = null;
                return false;
            }

            payload = payload with
            {
                ImageReferences = payload.ImageReferences
                    .Where(reference => !string.IsNullOrWhiteSpace(reference))
                    .Select(DockerImageCatalogService.NormalizeImageReference)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(reference => reference, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
            return payload.ImageReferences.Count > 0;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            warnings.Add($"Stack fragment '{fragment.FragmentId}' Docker image payload could not be parsed: {ex.Message}");
            return false;
        }
    }

    private sealed record DockerImageStackPayload(IReadOnlyList<string> ImageReferences);
}
