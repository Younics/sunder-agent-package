using System.Text.Json;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Agent.Execution.Docker;

internal sealed class DockerPackageStorageValidation(IPackageContext packageContext) : ISunderWorkerLifecycleService, ISunderWorkerSettingsSchemaProvider
{
    private const string WorkspaceKeyPrefix = "workspace-bindings.config.v2.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
    private static readonly HashSet<string> WorkspaceProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "schemaVersion",
        "imageReference",
        "imageReferenceNeedsAttention",
        "containerName",
        "shellPath",
        "pathEntries",
    };

    private readonly object _syncRoot = new();
    private readonly DockerImageCatalogCoordinator _coordinator =
        DockerImageCatalogService.GetCoordinator(packageContext.Storage.State);
    private Task? _initialization;

    public async Task StartAsync(CancellationToken cancellationToken = default)
        => await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

    public Task Completion => Task.CompletedTask;

    public Sunder.Sdk.Settings.PackageSettingsSchema SettingsSchema => DockerExecutionConfiguration.Schema;

    public ValueTask PrepareCandidateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask CommitGenerationAsync(
        SunderWorkerGenerationContext generation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask ActivateAsync(
        SunderWorkerContext context,
        CancellationToken cancellationToken = default)
        => await StartAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask ShutdownAsync(
        SunderWorkerShutdownContext context,
        CancellationToken cancellationToken = default)
        => await StopAsync(cancellationToken).ConfigureAwait(false);

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    internal async Task EnsureAsync(CancellationToken cancellationToken = default)
        => await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

    private Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        Task validation;
        var ownsValidation = false;
        lock (_syncRoot)
        {
            if (_initialization is null)
            {
                _initialization = InitializeStateAsync(cancellationToken);
                ownsValidation = true;
            }
            validation = _initialization;
        }

        return AwaitValidationAsync(validation, ownsValidation, cancellationToken);
    }

    private async Task AwaitValidationAsync(
        Task validation,
        bool ownsValidation,
        CancellationToken cancellationToken)
    {
        try
        {
            await (cancellationToken.CanBeCanceled
                    ? validation.WaitAsync(cancellationToken)
                    : validation)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            validation.IsCanceled || (ownsValidation && cancellationToken.IsCancellationRequested))
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(_initialization, validation))
                {
                    _initialization = null;
                }
            }
            throw;
        }
    }

    private async Task InitializeStateAsync(CancellationToken cancellationToken)
    {
        await _coordinator.ValidationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ValidateCatalogAsync(cancellationToken).ConfigureAwait(false);
            await ValidateWorkspaceConfigsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _coordinator.ValidationGate.Release();
        }
    }

    private async Task ValidateCatalogAsync(CancellationToken cancellationToken)
    {
        await _coordinator.MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var json = await packageContext.Storage.State.GetValueAsync(
                DockerImageCatalogService.ImagesKey,
                cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("Catalog root is not an object.");
                }
                if (!TryReadInt32(document.RootElement, "schemaVersion", out var schemaVersion))
                {
                    throw new DockerExecutionDomainException(
                        "docker.catalog.malformed",
                        "The stored Docker image catalog is malformed and was left unchanged.");
                }
                if (schemaVersion != DockerImageCatalogService.CurrentSchemaVersion)
                {
                    throw new DockerExecutionDomainException(
                        "docker.catalog.unsupported-schema",
                        "The stored Docker image catalog uses an unsupported schema and was left unchanged.");
                }

                DockerImageCatalogService.ValidateCatalogProperties(document.RootElement);
                var catalog = JsonSerializer.Deserialize<DockerImageCatalogState>(json, JsonOptions)
                    ?? throw new JsonException("Docker image catalog is null.");
                if (catalog.Revision < 0 || catalog.Images is null)
                {
                    throw new DockerExecutionDomainException(
                        "docker.catalog.malformed",
                        "The stored Docker image catalog is malformed and was left unchanged.");
                }

                foreach (var image in catalog.Images)
                {
                    if (!Enum.IsDefined(image.Status))
                    {
                        throw new DockerExecutionDomainException(
                            "docker.catalog.malformed",
                            "The stored Docker image catalog contains an unknown image status and was left unchanged.");
                    }
                    _ = image.Status == DockerImageStatus.NeedsAttention
                        ? DockerImageCatalogService.NormalizeAttentionImageReference(image.ImageReference)
                        : DockerImageCatalogService.NormalizeImageReference(image.ImageReference);
                }
                if (catalog.Images.Select(image => image.ImageReference)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() != catalog.Images.Count)
                {
                    throw new DockerExecutionDomainException(
                        "docker.catalog.malformed",
                        "The stored Docker image catalog contains duplicate references and was left unchanged.");
                }
            }
            catch (DockerExecutionDomainException)
            {
                throw;
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                throw new DockerExecutionDomainException(
                    "docker.catalog.malformed",
                    "The stored Docker image catalog is malformed and was left unchanged.",
                    innerException: exception);
            }
        }
        finally
        {
            _coordinator.MutationGate.Release();
        }
    }

    private async Task ValidateWorkspaceConfigsAsync(CancellationToken cancellationToken)
    {
        var keys = await packageContext.Storage.State.ListKeysAsync(
            WorkspaceKeyPrefix,
            cancellationToken).ConfigureAwait(false);
        foreach (var key in keys)
        {
            var json = await packageContext.Storage.State.GetValueAsync(key, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            _ = ReadWorkspaceConfig(json);
        }
    }

    internal static DockerExecutionWorkspaceConfig ReadWorkspaceConfig(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Workspace config root is not an object.");
            }

            if (!TryReadInt32(root, "schemaVersion", out var schemaVersion))
            {
                throw new DockerExecutionDomainException(
                    "docker.workspace-config.malformed",
                    "A stored Docker workspace configuration is malformed and was left unchanged.");
            }
            if (schemaVersion != DockerImageCatalogService.CurrentSchemaVersion)
            {
                throw new DockerExecutionDomainException(
                    "docker.workspace-config.unsupported-schema",
                    "A stored Docker workspace configuration uses an unsupported schema and was left unchanged.");
            }

            var observedProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in root.EnumerateObject())
            {
                if (!WorkspaceProperties.Contains(property.Name))
                {
                    throw new DockerExecutionDomainException(
                        "docker.workspace-config.unknown-data",
                        "A stored Docker workspace configuration contains unknown data and was left unchanged.");
                }
                if (!observedProperties.Add(property.Name))
                {
                    throw new DockerExecutionDomainException(
                        "docker.workspace-config.malformed",
                        "A stored Docker workspace configuration is malformed and was left unchanged.");
                }
            }

            var config = JsonSerializer.Deserialize<DockerExecutionWorkspaceConfig>(json, JsonOptions)
                ?? throw new DockerExecutionDomainException(
                    "docker.workspace-config.malformed",
                    "A stored Docker workspace configuration is malformed and was left unchanged.");
            if (!string.IsNullOrWhiteSpace(config.ImageReference))
            {
                _ = config.ImageReferenceNeedsAttention
                    ? DockerImageCatalogService.NormalizeAttentionImageReference(config.ImageReference)
                    : DockerImageCatalogService.NormalizeImageReference(config.ImageReference);
            }
            return config;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new DockerExecutionDomainException(
                "docker.workspace-config.malformed",
                "A stored Docker workspace configuration is malformed and was left unchanged.",
                innerException: exception);
        }
    }

    private static bool TryReadInt32(JsonElement element, string propertyName, out int value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt32(out value))
            {
                return true;
            }
        }
        value = default;
        return false;
    }

}
