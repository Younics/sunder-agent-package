using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Agent.Execution.Common;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Storage;

namespace Sunder.Package.Agent.Execution.Docker;

internal sealed class DockerPackageStorageMigration(IPackageContext packageContext) : IPackageBackgroundService
{
    private const string WorkspaceKeyPrefix = "workspace-bindings.config.v2.";
    private static readonly PackageStorageKeyMigration[] PhysicalMigrations =
    [
        PackageStorageKeyMigration.OpaqueId(
            "workspace-bindings:",
            ":config",
            "workspace-bindings.config",
            2),
        PackageStorageKeyMigration.Exact("docker.images:v1", DockerImageCatalogService.ImagesKey),
        PackageStorageKeyMigration.Exact("docker.images.initialized", "docker.images.initialized.v1"),
    ];
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
        "allowedRoots",
        "hostRoots",
        "defaultWorkingDirectory",
    };

    private readonly object _syncRoot = new();
    private Task? _physicalMigration;
    private Task? _semanticMigration;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await EnsurePhysicalMigrationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureSemanticMigrationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DockerExecutionDomainException)
        {
            // The cached fault is returned through the first Docker operation instead of rejecting Runtime publication.
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    internal async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        await EnsurePhysicalMigrationAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSemanticMigrationAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task EnsurePhysicalMigrationAsync(CancellationToken cancellationToken)
    {
        Task migration;
        lock (_syncRoot)
        {
            migration = _physicalMigration ??= MigratePhysicalKeysAsync(CancellationToken.None);
        }

        return cancellationToken.CanBeCanceled ? migration.WaitAsync(cancellationToken) : migration;
    }

    private Task EnsureSemanticMigrationAsync(CancellationToken cancellationToken)
    {
        Task migration;
        lock (_syncRoot)
        {
            migration = _semanticMigration ??= MigrateSemanticStateAsync(CancellationToken.None);
        }

        return cancellationToken.CanBeCanceled ? migration.WaitAsync(cancellationToken) : migration;
    }

    private async Task MigratePhysicalKeysAsync(CancellationToken cancellationToken)
    {
        if (packageContext.Storage.State is IPackageStorageKeyMigrator migrator)
        {
            await migrator.MigrateKeysAsync(PhysicalMigrations, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MigrateSemanticStateAsync(CancellationToken cancellationToken)
    {
        await MigrateCatalogAsync(cancellationToken).ConfigureAwait(false);
        await MigrateWorkspaceConfigsAsync(cancellationToken).ConfigureAwait(false);
        await MigrateSettingAsync(
            DockerExecutionConfiguration.TimeoutKey,
            static value => BoundedValue.TryParseInt32(
                value,
                1,
                BoundedProcessRunner.MaximumTimeoutSeconds,
                out _),
            "docker.migration.timeout-invalid",
            cancellationToken).ConfigureAwait(false);
        await MigrateSettingAsync(
            DockerCli.ExecutablePathConfigurationKey,
            static value => value.Length <= 1024 && Path.IsPathFullyQualified(value),
            "docker.migration.cli-path-invalid",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task MigrateCatalogAsync(CancellationToken cancellationToken)
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
            var hasSchemaVersion = HasProperty(document.RootElement, "schemaVersion");
            if (TryReadInt32(document.RootElement, "schemaVersion", out var schemaVersion))
            {
                if (schemaVersion > DockerImageCatalogService.CurrentSchemaVersion)
                {
                    return;
                }
                if (schemaVersion == DockerImageCatalogService.CurrentSchemaVersion)
                {
                    DockerImageCatalogService.ValidateCatalogProperties(document.RootElement, legacy: false);
                    return;
                }
                throw new DockerExecutionDomainException(
                    "docker.catalog.unsupported-schema",
                    "The stored Docker image catalog uses an unsupported schema and was left unchanged.");
            }
            if (hasSchemaVersion)
            {
                throw new DockerExecutionDomainException(
                    "docker.catalog.malformed",
                    "The stored Docker image catalog is malformed and was left unchanged.");
            }

            DockerImageCatalogService.ValidateCatalogProperties(document.RootElement, legacy: true);
            var legacy = JsonSerializer.Deserialize<LegacyDockerImageCatalogState>(json, JsonOptions)
                ?? throw new JsonException("Legacy catalog is null.");
            if (legacy.Version != 1 || legacy.Images is null)
            {
                throw new DockerExecutionDomainException(
                    "docker.catalog.unsupported-schema",
                    "The stored Docker image catalog uses an unsupported schema and was left unchanged.");
            }

            var migratedImages = legacy.Images.Select(MigrateLegacyImage).ToArray();
            if (migratedImages.Select(image => image.ImageReference)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != migratedImages.Length)
            {
                throw new DockerExecutionDomainException(
                    "docker.catalog.malformed",
                    "The stored Docker image catalog contains duplicate references and was left unchanged.");
            }
            var migrated = new DockerImageCatalogState(
                DockerImageCatalogService.CurrentSchemaVersion,
                1,
                migratedImages);
            await packageContext.Storage.State.SetValueAsync(
                DockerImageCatalogService.ImagesKey,
                JsonSerializer.Serialize(migrated, JsonOptions),
                cancellationToken).ConfigureAwait(false);
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

    private async Task MigrateWorkspaceConfigsAsync(CancellationToken cancellationToken)
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

            JsonObject root;
            try
            {
                root = JsonNode.Parse(json) as JsonObject
                    ?? throw new JsonException("Workspace config root is not an object.");
            }
            catch (JsonException exception)
            {
                throw new DockerExecutionDomainException(
                    "docker.workspace-config.malformed",
                    "A stored Docker workspace configuration is malformed and was left unchanged.",
                    innerException: exception);
            }

            var hasSchemaVersion = TryGetNode(root, "schemaVersion", out var schemaNode);
            var schemaVersion = hasSchemaVersion
                ? ReadWorkspaceSchemaVersion(schemaNode)
                : 1;
            if (schemaVersion > DockerImageCatalogService.CurrentSchemaVersion)
            {
                continue;
            }
            if (schemaVersion is < 1 or > DockerImageCatalogService.CurrentSchemaVersion)
            {
                throw new DockerExecutionDomainException(
                    "docker.workspace-config.unsupported-schema",
                    "A stored Docker workspace configuration uses an unsupported schema and was left unchanged.");
            }

            var unknownProperty = root.Select(property => property.Key)
                .FirstOrDefault(property => !WorkspaceProperties.Contains(property));
            if (unknownProperty is not null)
            {
                throw new DockerExecutionDomainException(
                    "docker.workspace-config.unknown-data",
                    "A stored Docker workspace configuration contains unknown data and was left unchanged.");
            }
            if (schemaVersion == DockerImageCatalogService.CurrentSchemaVersion)
            {
                continue;
            }

            var needsAttention = false;
            if (TryGetNode(root, "imageReference", out var imageNode)
                && imageNode is not null)
            {
                var imageReference = imageNode.GetValue<string?>();
                if (!string.IsNullOrWhiteSpace(imageReference))
                {
                    try
                    {
                        _ = DockerImageCatalogService.NormalizeImageReference(imageReference);
                    }
                    catch (DockerExecutionDomainException exception) when (
                        exception.Code is "docker.image-reference.unpinned" or "docker.image-reference.latest")
                    {
                        _ = DockerImageCatalogService.NormalizeLegacyImageReference(imageReference);
                        needsAttention = true;
                    }
                }
            }

            SetNode(root, "schemaVersion", DockerImageCatalogService.CurrentSchemaVersion);
            SetNode(root, "imageReferenceNeedsAttention", needsAttention);
            await packageContext.Storage.State.SetValueAsync(
                key,
                root.ToJsonString(JsonOptions),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MigrateSettingAsync(
        string key,
        Func<string, bool> validate,
        string invalidCode,
        CancellationToken cancellationToken)
    {
        var legacy = await packageContext.Storage.State.GetValueAsync(key, cancellationToken)
            .ConfigureAwait(false);
        if (legacy is null)
        {
            return;
        }
        if (!validate(legacy))
        {
            throw new DockerExecutionDomainException(
                invalidCode,
                "Legacy Docker settings contain an invalid value and were left unchanged.");
        }

        var authoritative = await packageContext.Settings.GetStoredValueAsync(key, cancellationToken)
            .ConfigureAwait(false);
        if (authoritative is null)
        {
            await packageContext.Settings.SetValueAsync(key, legacy, cancellationToken).ConfigureAwait(false);
        }
        else if (!string.Equals(authoritative, legacy, StringComparison.Ordinal))
        {
            throw new DockerExecutionDomainException(
                "docker.migration.settings-conflict",
                "Legacy and authoritative Docker settings conflict and were left unchanged.");
        }

        await packageContext.Storage.State.DeleteValueAsync(key, cancellationToken).ConfigureAwait(false);
    }

    private static DockerImageDefinition MigrateLegacyImage(DockerImageDefinition image)
    {
        if (!Enum.IsDefined(image.Status))
        {
            throw new DockerExecutionDomainException(
                "docker.catalog.malformed",
                "The stored Docker image catalog contains an unknown image status and was left unchanged.");
        }
        try
        {
            var normalized = DockerImageCatalogService.NormalizeImageReference(image.ImageReference);
            return image with
            {
                ImageReference = normalized,
                Status = image.Status == DockerImageStatus.Pulling
                    ? DockerImageStatus.NotPulled
                    : image.Status,
            };
        }
        catch (DockerExecutionDomainException exception) when (
            exception.Code is "docker.image-reference.unpinned" or "docker.image-reference.latest")
        {
            return image with
            {
                ImageReference = DockerImageCatalogService.NormalizeLegacyImageReference(image.ImageReference),
                Status = DockerImageStatus.NeedsAttention,
                LastMessage = "Legacy floating reference requires a pinned version tag or sha256 digest.",
            };
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

    private static bool HasProperty(JsonElement element, string propertyName)
        => element.EnumerateObject().Any(property => string.Equals(
            property.Name,
            propertyName,
            StringComparison.OrdinalIgnoreCase));

    private static bool TryGetNode(JsonObject root, string propertyName, out JsonNode? node)
    {
        foreach (var property in root)
        {
            if (string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                node = property.Value;
                return true;
            }
        }
        node = null;
        return false;
    }

    private static int ReadWorkspaceSchemaVersion(JsonNode? node)
    {
        try
        {
            return node?.GetValue<int>()
                ?? throw new InvalidOperationException("Workspace schema version is null.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            throw new DockerExecutionDomainException(
                "docker.workspace-config.malformed",
                "A stored Docker workspace configuration is malformed and was left unchanged.",
                innerException: exception);
        }
    }

    private static void SetNode(JsonObject root, string propertyName, JsonNode? value)
    {
        var existingName = root.Select(property => property.Key)
            .FirstOrDefault(name => string.Equals(name, propertyName, StringComparison.OrdinalIgnoreCase));
        root[existingName ?? propertyName] = value;
    }

    private sealed record LegacyDockerImageCatalogState(
        int Version,
        IReadOnlyList<DockerImageDefinition> Images);
}
