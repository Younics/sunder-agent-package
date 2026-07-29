using System.Text.Json;
using System.Runtime.CompilerServices;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Docker;

public sealed class DockerImageCatalogService
{
    internal const string ImagesKey = "docker.images.v1";
    internal const int CurrentSchemaVersion = 2;
    private const int ImageCheckTimeoutSeconds = 30;
    private const int ImagePullTimeoutSeconds = 1800;
    private const int MaximumReferenceLength = 512;
    private const int MaximumDiagnosticLength = 512;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
    private static readonly HashSet<string> CatalogProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "schemaVersion",
        "revision",
        "images",
    };
    private static readonly HashSet<string> LegacyCatalogProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "version",
        "images",
    };
    private static readonly HashSet<string> ImageProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "imageReference",
        "status",
        "lastPulledAtUtc",
        "lastMessage",
    };
    private static readonly ConditionalWeakTable<IPackageKeyValueStore, DockerImageCatalogCoordinator> Coordinators = new();
    private readonly IPackageContext _packageContext;
    private readonly DockerCliRunner _dockerCliRunner;
    private readonly DockerPackageStorageMigration _storageMigration;
    private readonly DockerImageCatalogCoordinator _coordinator;
    private readonly SemaphoreSlim _mutationGate;

    public DockerImageCatalogService(IPackageContext packageContext, DockerCliRunner? dockerCliRunner = null)
        : this(
            packageContext,
            dockerCliRunner ?? new DockerCliRunner(packageContext),
            new DockerPackageStorageMigration(packageContext))
    {
    }

    internal DockerImageCatalogService(
        IPackageContext packageContext,
        DockerCliRunner dockerCliRunner,
        DockerPackageStorageMigration storageMigration)
    {
        _packageContext = packageContext;
        _dockerCliRunner = dockerCliRunner;
        _storageMigration = storageMigration;
        _coordinator = Coordinators.GetValue(
            packageContext.Storage.State,
            static _ => new DockerImageCatalogCoordinator());
        _mutationGate = _coordinator.MutationGate;
    }

    public event Action? ImagesChanged;

    public async Task<IReadOnlyList<DockerImageDefinition>> ListImagesAsync(
        CancellationToken cancellationToken = default)
        => (await GetSnapshotAsync(cancellationToken)).Images;

    internal async Task<DockerImageCatalogSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        await _storageMigration.EnsureAsync(cancellationToken).ConfigureAwait(false);
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        return new DockerImageCatalogSnapshot(state.Revision, state.Images);
    }

    public async Task<string?> GetDefaultImageReferenceAsync(CancellationToken cancellationToken = default)
        => (await ListImagesAsync(cancellationToken))
            .FirstOrDefault(image => image.Status != DockerImageStatus.NeedsAttention)
            ?.ImageReference;

    public async Task<bool> ContainsImageAsync(
        string? imageReference,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imageReference))
        {
            return false;
        }

        var normalized = NormalizeExistingReference(imageReference);
        return (await ListImagesAsync(cancellationToken)).Any(image => string.Equals(
            image.ImageReference,
            normalized,
            StringComparison.OrdinalIgnoreCase));
    }

    public async Task<DockerImageDefinition> AddImageAsync(
        string imageReference,
        CancellationToken cancellationToken = default)
        => (await AddImageAndListAsync(imageReference, cancellationToken)).Image;

    internal async Task<DockerImageCatalogMutationResult> AddImageAndListAsync(
        string imageReference,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeImageReference(imageReference);
        DockerImageCatalogMutationResult result;
        var changed = false;
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadStateAfterMigrationAsync(cancellationToken).ConfigureAwait(false);
            var existing = current.Images.FirstOrDefault(image => string.Equals(
                image.ImageReference,
                normalized,
                StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return new DockerImageCatalogMutationResult(
                    existing,
                    new DockerImageCatalogSnapshot(current.Revision, current.Images));
            }

            var image = new DockerImageDefinition(
                normalized,
                DockerImageStatus.NotPulled,
                null,
                "Image has not been pulled yet.");
            var updated = CreateUpdatedState(current, current.Images.Append(image));
            await SaveStateAsync(updated, cancellationToken).ConfigureAwait(false);
            _coordinator.InvalidateStatus(normalized);
            changed = true;
            result = new DockerImageCatalogMutationResult(
                image,
                new DockerImageCatalogSnapshot(updated.Revision, updated.Images));
        }
        finally
        {
            _mutationGate.Release();
        }

        if (changed)
        {
            ImagesChanged?.Invoke();
        }
        return result;
    }

    public async Task DeleteImageAsync(
        string imageReference,
        CancellationToken cancellationToken = default)
        => _ = await DeleteImageAndListAsync(imageReference, cancellationToken);

    internal async Task<DockerImageCatalogSnapshot> DeleteImageAndListAsync(
        string imageReference,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeExistingReference(imageReference);
        DockerImageCatalogSnapshot result;
        var changed = false;
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadStateAfterMigrationAsync(cancellationToken).ConfigureAwait(false);
            var images = current.Images.Where(image => !string.Equals(
                    image.ImageReference,
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (images.Length == current.Images.Count)
            {
                return new DockerImageCatalogSnapshot(current.Revision, current.Images);
            }

            var updated = CreateUpdatedState(current, images);
            await SaveStateAsync(updated, cancellationToken).ConfigureAwait(false);
            _coordinator.InvalidateStatus(normalized);
            changed = true;
            result = new DockerImageCatalogSnapshot(updated.Revision, updated.Images);
        }
        finally
        {
            _mutationGate.Release();
        }

        if (changed)
        {
            ImagesChanged?.Invoke();
        }
        return result;
    }

    public async Task<DockerImageReadiness> GetReadinessAsync(
        string? imageReference,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imageReference))
        {
            return new DockerImageReadiness(false, "Configure a Docker image before using Docker execution.", null);
        }

        var normalized = NormalizeExistingReference(imageReference);
        var operation = await TryBeginImageStatusOperationAsync(
            normalized,
            markPulling: false,
            cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            return new DockerImageReadiness(
                false,
                $"Docker image '{normalized}' is not configured. Add it in Docker Execution settings before using this workspace.",
                null);
        }
        var image = operation.Image;
        if (image.Status == DockerImageStatus.NeedsAttention)
        {
            return new DockerImageReadiness(
                false,
                "This workspace uses a legacy floating Docker image reference. Select a pinned version tag or sha256 digest.",
                image);
        }

        var inspect = await RunDockerAsync(
            ["image", "inspect", normalized],
            ImageCheckTimeoutSeconds,
            cancellationToken,
            progress: null).ConfigureAwait(false);
        if (inspect.ExitCode == 0)
        {
            var ready = image with { Status = DockerImageStatus.Ready, LastMessage = "Image is ready." };
            var committed = await UpdateImageAsync(operation, ready, cancellationToken).ConfigureAwait(false);
            return new DockerImageReadiness(true, $"Docker image '{normalized}' is ready.", committed);
        }

        return new DockerImageReadiness(
            false,
            AppendOutput(
                $"Docker image '{normalized}' is not ready. Pull it in Docker Execution settings before using this workspace.",
                inspect.Output),
            image);
    }

    public async Task<DockerImageDefinition> RefreshImageAsync(
        string imageReference,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeImageReference(imageReference);
        var operation = await BeginImageStatusOperationAsync(
            normalized,
            markPulling: false,
            cancellationToken).ConfigureAwait(false);
        var image = operation.Image;
        var inspect = await RunDockerAsync(
            ["image", "inspect", normalized],
            ImageCheckTimeoutSeconds,
            cancellationToken,
            progress: null).ConfigureAwait(false);
        var updated = inspect.ExitCode == 0
            ? image with { Status = DockerImageStatus.Ready, LastMessage = "Image is ready." }
            : image with
            {
                Status = DockerImageStatus.NotPulled,
                LastMessage = AppendOutput("Image is not available locally.", inspect.Output),
            };
        return await UpdateImageAsync(operation, updated, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DockerImageDefinition>> RefreshImagesAsync(
        CancellationToken cancellationToken = default)
    {
        var snapshot = await ListImagesAsync(cancellationToken);
        foreach (var image in snapshot.Where(image => image.Status != DockerImageStatus.NeedsAttention))
        {
            await RefreshImageAsync(image.ImageReference, cancellationToken).ConfigureAwait(false);
        }

        return await ListImagesAsync(cancellationToken);
    }

    public async Task<DockerImagePullResult> PullImageAsync(
        string imageReference,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeImageReference(imageReference);
        var operation = await BeginImageStatusOperationAsync(
            normalized,
            markPulling: true,
            cancellationToken).ConfigureAwait(false);
        var pulling = operation.Image;

        var pull = await RunDockerAsync(
            ["pull", normalized],
            ImagePullTimeoutSeconds,
            cancellationToken,
            progress).ConfigureAwait(false);
        if (pull.ExitCode != 0)
        {
            var failed = pulling with
            {
                Status = DockerImageStatus.Failed,
                LastMessage = AppendOutput("Docker image pull failed.", pull.Output),
            };
            var committed = await UpdateImageAsync(operation, failed, cancellationToken).ConfigureAwait(false);
            return new DockerImagePullResult(false, failed.LastMessage ?? "Docker image pull failed.", committed);
        }

        var inspect = await RunDockerAsync(
            ["image", "inspect", normalized],
            ImageCheckTimeoutSeconds,
            cancellationToken,
            progress: null).ConfigureAwait(false);
        if (inspect.ExitCode == 0)
        {
            var ready = pulling with
            {
                Status = DockerImageStatus.Ready,
                LastPulledAtUtc = DateTimeOffset.UtcNow,
                LastMessage = "Image is ready.",
            };
            var committed = await UpdateImageAsync(operation, ready, cancellationToken).ConfigureAwait(false);
            return new DockerImagePullResult(true, $"Docker image '{normalized}' is ready.", committed);
        }

        var unavailable = pulling with
        {
            Status = DockerImageStatus.Failed,
            LastMessage = AppendOutput(
                "Docker image was pulled but could not be inspected.",
                inspect.Output),
        };
        var committedUnavailable = await UpdateImageAsync(
            operation,
            unavailable,
            cancellationToken).ConfigureAwait(false);
        return new DockerImagePullResult(
            false,
            unavailable.LastMessage ?? "Docker image could not be inspected.",
            committedUnavailable);
    }

    public async Task SaveImagesAsync(
        IEnumerable<DockerImageDefinition> images,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(images);
        var normalized = NormalizeImages(images);
        var changed = false;
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadStateAfterMigrationAsync(cancellationToken).ConfigureAwait(false);
            var updated = CreateUpdatedState(current, normalized);
            await SaveStateAsync(updated, cancellationToken).ConfigureAwait(false);
            _coordinator.InvalidateAllStatuses();
            changed = true;
        }
        finally
        {
            _mutationGate.Release();
        }

        if (changed)
        {
            ImagesChanged?.Invoke();
        }
    }

    public void NotifyImagesImported()
        => ImagesChanged?.Invoke();

    public static string NormalizeImageReference(string imageReference)
    {
        var normalized = imageReference?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new DockerExecutionDomainException(
                "docker.image-reference.required",
                "Docker image reference is required.");
        }
        if (normalized.Length > MaximumReferenceLength
            || normalized.Any(char.IsWhiteSpace)
            || normalized[0] == '-')
        {
            throw new DockerExecutionDomainException(
                "docker.image-reference.invalid",
                "Docker image reference is invalid.");
        }

        var digestSeparator = normalized.LastIndexOf('@');
        if (digestSeparator >= 0)
        {
            var digest = normalized[(digestSeparator + 1)..];
            if (digestSeparator == 0
                || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                || digest.Length != "sha256:".Length + 64
                || !digest["sha256:".Length..].All(Uri.IsHexDigit))
            {
                throw new DockerExecutionDomainException(
                    "docker.image-reference.invalid-digest",
                    "Docker image digests must use the complete '@sha256:<64 hex characters>' form.");
            }
            return normalized;
        }

        var lastSlash = normalized.LastIndexOf('/');
        var tagSeparator = normalized.LastIndexOf(':');
        if (tagSeparator <= lastSlash || tagSeparator == normalized.Length - 1)
        {
            throw new DockerExecutionDomainException(
                "docker.image-reference.unpinned",
                "Docker image references must include an explicit version tag or sha256 digest.");
        }

        var tag = normalized[(tagSeparator + 1)..];
        if (string.Equals(tag, "latest", StringComparison.OrdinalIgnoreCase))
        {
            throw new DockerExecutionDomainException(
                "docker.image-reference.latest",
                "Docker image tag 'latest' is not allowed. Use an explicit version tag or sha256 digest.");
        }
        return normalized;
    }

    internal static string NormalizeLegacyImageReference(string imageReference)
    {
        var normalized = imageReference?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > MaximumReferenceLength
            || normalized.Any(char.IsWhiteSpace)
            || normalized[0] == '-')
        {
            throw new DockerExecutionDomainException(
                "docker.image-reference.invalid",
                "Legacy Docker image reference is invalid.");
        }
        return normalized;
    }

    private static string NormalizeExistingReference(string imageReference)
    {
        try
        {
            return NormalizeImageReference(imageReference);
        }
        catch (DockerExecutionDomainException exception) when (
            exception.Code is "docker.image-reference.unpinned" or "docker.image-reference.latest")
        {
            return NormalizeLegacyImageReference(imageReference);
        }
    }

    private async Task<DockerImageCatalogState> LoadStateAfterMigrationAsync(
        CancellationToken cancellationToken)
    {
        await _storageMigration.EnsureAsync(cancellationToken).ConfigureAwait(false);
        return await LoadStateAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<DockerImageCatalogState> LoadStateAsync(CancellationToken cancellationToken)
    {
        var json = await _packageContext.Storage.State.GetValueAsync(ImagesKey, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new DockerImageCatalogState(CurrentSchemaVersion, 0, []);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var state = JsonSerializer.Deserialize<DockerImageCatalogState>(json, JsonOptions)
                ?? throw new JsonException("Docker image catalog is null.");
            if (state.SchemaVersion != CurrentSchemaVersion)
            {
                throw new DockerExecutionDomainException(
                    "docker.catalog.unsupported-schema",
                    "The stored Docker image catalog uses an unsupported schema and was left unchanged.");
            }
            ValidateCatalogProperties(document.RootElement, legacy: false);
            if (state.Revision < 0 || state.Images is null)
            {
                throw new DockerExecutionDomainException(
                    "docker.catalog.malformed",
                    "The stored Docker image catalog is malformed and was left unchanged.");
            }
            return state with { Images = NormalizeImages(state.Images) };
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

    internal static void ValidateCatalogProperties(JsonElement root, bool legacy)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Docker image catalog root is not an object.");
        }

        ValidateObjectProperties(root, legacy ? LegacyCatalogProperties : CatalogProperties);
        var images = root.EnumerateObject().FirstOrDefault(property =>
            string.Equals(property.Name, "images", StringComparison.OrdinalIgnoreCase)).Value;
        if (images.ValueKind == JsonValueKind.Undefined)
        {
            return;
        }
        if (images.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Docker image catalog images are not an array.");
        }

        foreach (var image in images.EnumerateArray())
        {
            if (image.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Docker image catalog entry is not an object.");
            }
            ValidateObjectProperties(image, ImageProperties);
        }
    }

    private static void ValidateObjectProperties(JsonElement value, IReadOnlySet<string> allowedProperties)
    {
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowedProperties.Contains(property.Name))
            {
                throw new DockerExecutionDomainException(
                    "docker.catalog.unknown-data",
                    "The stored Docker image catalog contains unknown data and was left unchanged.");
            }
            if (!observed.Add(property.Name))
            {
                throw new DockerExecutionDomainException(
                    "docker.catalog.malformed",
                    "The stored Docker image catalog contains duplicate properties and was left unchanged.");
            }
        }
    }

    private async Task<DockerImageDefinition> UpdateImageAsync(
        DockerImageStatusOperation operation,
        DockerImageDefinition image,
        CancellationToken cancellationToken)
    {
        var changed = false;
        var committed = image;
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadStateAfterMigrationAsync(cancellationToken).ConfigureAwait(false);
            var images = current.Images.ToList();
            var index = images.FindIndex(candidate => string.Equals(
                candidate.ImageReference,
                operation.ImageReference,
                StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                committed = images[index];
                if (_coordinator.CanCommitStatus(operation.ImageReference, operation.Revision))
                {
                    images[index] = image;
                    var updated = CreateUpdatedState(current, images);
                    await SaveStateAsync(updated, cancellationToken).ConfigureAwait(false);
                    _coordinator.CommitStatus(operation.ImageReference, operation.Revision);
                    committed = updated.Images.First(candidate => string.Equals(
                        candidate.ImageReference,
                        operation.ImageReference,
                        StringComparison.OrdinalIgnoreCase));
                    changed = true;
                }
            }
        }
        finally
        {
            _mutationGate.Release();
        }
        if (changed)
        {
            ImagesChanged?.Invoke();
        }
        return committed;
    }

    private async Task<DockerImageStatusOperation> BeginImageStatusOperationAsync(
        string imageReference,
        bool markPulling,
        CancellationToken cancellationToken)
        => await TryBeginImageStatusOperationAsync(
                imageReference,
                markPulling,
                cancellationToken).ConfigureAwait(false)
           ?? throw ImageNotConfigured();

    private async Task<DockerImageStatusOperation?> TryBeginImageStatusOperationAsync(
        string imageReference,
        bool markPulling,
        CancellationToken cancellationToken)
    {
        DockerImageStatusOperation? operation = null;
        var changed = false;
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadStateAfterMigrationAsync(cancellationToken).ConfigureAwait(false);
            var image = current.Images.FirstOrDefault(candidate => string.Equals(
                candidate.ImageReference,
                imageReference,
                StringComparison.OrdinalIgnoreCase));
            if (image is null)
            {
                return null;
            }

            var revision = _coordinator.BeginStatus(imageReference);
            if (markPulling)
            {
                image = image with
                {
                    Status = DockerImageStatus.Pulling,
                    LastMessage = "Pulling image...",
                };
                var images = current.Images.Select(candidate => string.Equals(
                        candidate.ImageReference,
                        imageReference,
                        StringComparison.OrdinalIgnoreCase)
                    ? image
                    : candidate);
                await SaveStateAsync(CreateUpdatedState(current, images), cancellationToken).ConfigureAwait(false);
                changed = true;
            }
            operation = new DockerImageStatusOperation(imageReference, revision, image);
        }
        finally
        {
            _mutationGate.Release();
        }

        if (changed)
        {
            ImagesChanged?.Invoke();
        }
        return operation;
    }

    private static DockerExecutionDomainException ImageNotConfigured()
        => new(
            "docker.image.not-configured",
            "The selected Docker image is no longer configured.",
            isTransient: true);

    private Task SaveStateAsync(
        DockerImageCatalogState state,
        CancellationToken cancellationToken)
        => _packageContext.Storage.State.SetValueAsync(
            ImagesKey,
            JsonSerializer.Serialize(state, JsonOptions),
            cancellationToken);

    private static DockerImageCatalogState CreateUpdatedState(
        DockerImageCatalogState current,
        IEnumerable<DockerImageDefinition> images)
        => new(
            CurrentSchemaVersion,
            checked(current.Revision + 1),
            NormalizeImages(images));

    private static IReadOnlyList<DockerImageDefinition> NormalizeImages(
        IEnumerable<DockerImageDefinition> images)
    {
        var normalized = images.Select(image =>
        {
            if (string.IsNullOrWhiteSpace(image.ImageReference))
            {
                throw new DockerExecutionDomainException(
                    "docker.catalog.malformed",
                    "The stored Docker image catalog contains an empty image reference.");
            }
            if (!Enum.IsDefined(image.Status))
            {
                throw new DockerExecutionDomainException(
                    "docker.catalog.malformed",
                    "The stored Docker image catalog contains an unknown image status.");
            }
            var reference = image.Status == DockerImageStatus.NeedsAttention
                ? NormalizeLegacyImageReference(image.ImageReference)
                : NormalizeImageReference(image.ImageReference);
            return image with
            {
                ImageReference = reference,
                Status = image.Status == DockerImageStatus.Pulling
                    ? DockerImageStatus.NotPulled
                    : image.Status,
                LastMessage = BoundDiagnostic(image.LastMessage),
            };
        }).OrderBy(image => image.ImageReference, StringComparer.OrdinalIgnoreCase).ToArray();
        if (normalized.Select(image => image.ImageReference)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != normalized.Length)
        {
            throw new DockerExecutionDomainException(
                "docker.catalog.malformed",
                "The stored Docker image catalog contains duplicate image references.");
        }
        return normalized;
    }

    private async Task<DockerCliRunResult> RunDockerAsync(
        IReadOnlyList<string> args,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
        => await _dockerCliRunner.RunAsync(
            args,
            timeoutSeconds,
            cancellationToken,
            progress: progress).ConfigureAwait(false);

    private static string AppendOutput(string message, string? output)
    {
        var trimmed = BoundDiagnostic(output)?.Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? message
            : $"{message} {trimmed}";
    }

    private static string? BoundDiagnostic(string? value)
        => value is { Length: > MaximumDiagnosticLength }
            ? value[..MaximumDiagnosticLength]
            : value;
}

internal sealed record DockerImageCatalogState(
    int SchemaVersion,
    long Revision,
    IReadOnlyList<DockerImageDefinition> Images);

internal sealed record DockerImageCatalogSnapshot(
    long Revision,
    IReadOnlyList<DockerImageDefinition> Images);

internal sealed record DockerImageCatalogMutationResult(
    DockerImageDefinition Image,
    DockerImageCatalogSnapshot Snapshot);

public sealed record DockerImageDefinition(
    string ImageReference,
    DockerImageStatus Status,
    DateTimeOffset? LastPulledAtUtc,
    string? LastMessage);

public sealed record DockerImageReadiness(bool IsReady, string Message, DockerImageDefinition? Image);

public sealed record DockerImagePullResult(bool Success, string Message, DockerImageDefinition Image);

public enum DockerImageStatus
{
    NotPulled = 0,
    Ready = 1,
    Pulling = 2,
    Failed = 3,
    NeedsAttention = 4,
}
