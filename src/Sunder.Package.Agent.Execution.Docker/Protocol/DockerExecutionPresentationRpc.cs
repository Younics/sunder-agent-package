using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Package.Agent.Execution.Docker.Protocol.Generated;

namespace Sunder.Package.Agent.Execution.Docker;

public interface IDockerExecutionPresentationClient
{
    ValueTask<DockerExecutionOperationResponse> ExecuteAsync(
        DockerExecutionOperationRequest request,
        CancellationToken cancellationToken = default);
}

public static class DockerExecutionPresentationRpc
{
    public const string ContractId = "sunder.package.agent.execution.docker.presentation";
    public const string ContractVersion = "1.0.0";
    public const string ProviderId = "docker.presentation";
    public const string ServiceId = "presentation";
    internal const int MaximumImageCatalogBytes = 8 * 1024 * 1024;
    private const string JsonMediaType = "application/json";
    private static readonly JsonSerializerOptions CatalogJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    private const string DescriptorResourceName =
        "Sunder.Package.Agent.Execution.Docker.Protocol.Contracts.docker-execution-presentation.rpc.json";
    private static readonly Lazy<SunderRpcContractDescriptor> ContractDescriptor =
        new(LoadDescriptor, LazyThreadSafetyMode.ExecutionAndPublication);

    public static SunderRpcContractDescriptor Descriptor => ContractDescriptor.Value;

    public static ISunderRpcServiceHandler CreateHandler(IDockerExecutionPresentationClient client)
        => new Wire.SunderPackageAgentExecutionDockerPresentationRpcHandler(
            new ProviderAdapter(client));

    private static SunderRpcContractDescriptor LoadDescriptor()
    {
        using var stream = typeof(DockerExecutionPresentationRpc).Assembly
            .GetManifestResourceStream(DescriptorResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded RPC descriptor '{DescriptorResourceName}' was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var descriptor = SunderRpcContractDescriptor.Parse(memory.ToArray());
        if (descriptor.DescriptorVersion != SunderRpcProtocol.DescriptorVersionV2
            || !string.Equals(descriptor.ContractId, ContractId, StringComparison.Ordinal)
            || !string.Equals(descriptor.Version, ContractVersion, StringComparison.Ordinal)
            || descriptor.AbiSha256 is null)
        {
            throw new InvalidDataException(
                $"Embedded RPC descriptor '{DescriptorResourceName}' does not match its V2 identity.");
        }
        return descriptor;
    }

    private sealed class ProviderAdapter(IDockerExecutionPresentationClient client)
        : Wire.IPresentationProvider
    {
        public async ValueTask<Wire.DockerExecutionOperationResponse> ExecuteAsync(
            SunderRpcInvocationContext context,
            Wire.DockerExecutionOperationRequest request,
            CancellationToken cancellationToken = default)
            => await ToWireAsync(
                context,
                await client.ExecuteAsync(FromWire(request), cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
    }

    internal static Wire.DockerExecutionOperationRequest ToWire(DockerExecutionOperationRequest value)
        => new()
        {
            Kind = value.Kind switch
            {
                DockerExecutionOperationKind.GetSettings => Wire.DockerOperation.GetSettings,
                DockerExecutionOperationKind.SaveSettings => Wire.DockerOperation.SaveSettings,
                DockerExecutionOperationKind.AddImage => Wire.DockerOperation.AddImage,
                DockerExecutionOperationKind.DeleteImage => Wire.DockerOperation.DeleteImage,
                DockerExecutionOperationKind.RefreshImage => Wire.DockerOperation.RefreshImage,
                DockerExecutionOperationKind.RefreshImages => Wire.DockerOperation.RefreshImages,
                DockerExecutionOperationKind.PullImage => Wire.DockerOperation.PullImage,
                DockerExecutionOperationKind.TestDocker => Wire.DockerOperation.TestDocker,
                _ => throw new InvalidDataException("The Docker execution operation is invalid."),
            },
            TimeoutSeconds = value.TimeoutSeconds,
            DockerCliPath = value.DockerCliPath,
            ImageReference = value.ImageReference,
        };

    private static DockerExecutionOperationRequest FromWire(Wire.DockerExecutionOperationRequest value)
        => new(
            value.Kind switch
            {
                Wire.DockerOperation.GetSettings => DockerExecutionOperationKind.GetSettings,
                Wire.DockerOperation.SaveSettings => DockerExecutionOperationKind.SaveSettings,
                Wire.DockerOperation.AddImage => DockerExecutionOperationKind.AddImage,
                Wire.DockerOperation.DeleteImage => DockerExecutionOperationKind.DeleteImage,
                Wire.DockerOperation.RefreshImage => DockerExecutionOperationKind.RefreshImage,
                Wire.DockerOperation.RefreshImages => DockerExecutionOperationKind.RefreshImages,
                Wire.DockerOperation.PullImage => DockerExecutionOperationKind.PullImage,
                Wire.DockerOperation.TestDocker => DockerExecutionOperationKind.TestDocker,
                _ => throw new InvalidDataException("The Docker execution operation is invalid."),
            },
            value.TimeoutSeconds,
            value.DockerCliPath,
            value.ImageReference);

    private static async ValueTask<Wire.DockerExecutionOperationResponse> ToWireAsync(
        SunderRpcInvocationContext context,
        DockerExecutionOperationResponse value,
        CancellationToken cancellationToken)
    {
        SunderRpcContentReference? imageCatalog = null;
        if (value.Images is not null)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value.Images, CatalogJson);
            if (bytes.Length > MaximumImageCatalogBytes)
            {
                throw new InvalidDataException("The Docker image catalog exceeds its 8 MiB content limit.");
            }
            await using var stream = new MemoryStream(bytes, writable: false);
            imageCatalog = await context.RegisterContentAsync(
                stream,
                new SunderRpcContentRegistrationOptions(
                    JsonMediaType,
                    "docker-image-catalog.json",
                    bytes.LongLength,
                    Repeatability: SunderRpcContentRepeatability.SingleUse,
                    MaximumUses: 1),
                cancellationToken).ConfigureAwait(false);
            ValidateReference(imageCatalog, context.DeadlineUtc);
            var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (imageCatalog.Length != bytes.LongLength
                || !string.Equals(imageCatalog.Sha256, digest, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The Host returned inconsistent Docker catalog metadata.");
            }
        }

        return new Wire.DockerExecutionOperationResponse
        {
            TimeoutSeconds = value.TimeoutSeconds,
            DockerCliPath = value.DockerCliPath,
            ImageCatalog = imageCatalog,
            Success = value.Success,
            Message = value.Message,
            CatalogRevision = value.CatalogRevision,
            Error = value.Error is null ? null : new Wire.DockerExecutionOperationError
            {
                Code = value.Error.Code,
                Message = value.Error.Message,
                IsTransient = value.Error.IsTransient,
                CorrelationId = value.Error.CorrelationId,
            },
        };
    }

    internal static async ValueTask<DockerExecutionOperationResponse> FromWireAsync(
        ISunderRpcCallScope scope,
        Wire.DockerExecutionOperationResponse value,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DockerImageDefinition>? images = null;
        if (value.ImageCatalog is { } reference)
        {
            ValidateReference(reference, scope.DeadlineUtc);
            await using var stream = await scope.OpenContentAsync(reference, cancellationToken).ConfigureAwait(false);
            using var content = new MemoryStream(checked((int)reference.Length));
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                if (content.Length + read > reference.Length || content.Length + read > MaximumImageCatalogBytes)
                {
                    throw new InvalidDataException("The Docker image catalog exceeds its authenticated length.");
                }
                content.Write(buffer, 0, read);
            }

            var bytes = content.ToArray();
            var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (bytes.LongLength != reference.Length
                || !string.Equals(reference.Sha256, digest, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The Docker image catalog digest or length is invalid.");
            }
            images = JsonSerializer.Deserialize<IReadOnlyList<DockerImageDefinition>>(bytes, CatalogJson)
                     ?? throw new InvalidDataException("The Docker image catalog is empty.");
        }

        return new DockerExecutionOperationResponse(
            value.TimeoutSeconds,
            value.DockerCliPath,
            images,
            value.Success,
            value.Message,
            value.CatalogRevision,
            value.Error is null ? null : new DockerExecutionOperationError(
                value.Error.Code,
                value.Error.Message,
                value.Error.IsTransient,
                value.Error.CorrelationId));
    }

    private static void ValidateReference(SunderRpcContentReference reference, DateTimeOffset deadlineUtc)
    {
        if (reference.Length < 0
            || reference.Length > MaximumImageCatalogBytes
            || !string.Equals(reference.MediaType, JsonMediaType, StringComparison.OrdinalIgnoreCase)
            || reference.Repeatability != SunderRpcContentRepeatability.SingleUse
            || reference.ExpiresAtUtc <= DateTimeOffset.UtcNow
            || reference.ExpiresAtUtc > deadlineUtc)
        {
            throw new InvalidDataException("The Docker image catalog content reference is invalid.");
        }
    }
}

public sealed class DockerExecutionPresentationRpcClient(ISunderRpcClient client)
    : IDockerExecutionPresentationClient
{
    public async ValueTask<DockerExecutionOperationResponse> ExecuteAsync(
        DockerExecutionOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        var descriptor = DockerExecutionPresentationRpc.Descriptor;
        var catalog = await client.DiscoverAsync(descriptor.ContractId, cancellationToken)
            .ConfigureAwait(false);
        var providers = catalog.Providers.Where(provider =>
                string.Equals(provider.ProviderId, DockerExecutionPresentationRpc.ProviderId, StringComparison.Ordinal)
                && provider.State == SunderRpcProviderState.Active
                && string.Equals(provider.ContractVersion, descriptor.Version, StringComparison.Ordinal)
                && string.Equals(provider.DescriptorSha256, descriptor.Sha256, StringComparison.Ordinal)
                && string.Equals(provider.AbiSha256, descriptor.AbiSha256, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (providers.Length != 1)
        {
            throw new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.Unavailable,
                "docker.presentation.unavailable",
                "The Docker execution presentation provider is unavailable."));
        }

        await using var scope = await client.CreateCallScopeAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var response = await new Wire.PresentationClient(scope, providers[0].Endpoint)
            .ExecuteAsync(DockerExecutionPresentationRpc.ToWire(request), cancellationToken)
            .ConfigureAwait(false);
        return await DockerExecutionPresentationRpc.FromWireAsync(
            scope,
            response,
            cancellationToken).ConfigureAwait(false);
    }
}

public static class DockerExecutionPresentationDefaults
{
    public const string TimeoutKey = "docker.timeoutSeconds.default";
    public const string DockerCliPathKey = "docker.executablePath";
    public const string DefaultTimeoutSeconds = "300";
    public const int MaximumTimeoutSeconds = 86_400;

    public static bool TryParseTimeout(string? value, out int timeoutSeconds)
        => int.TryParse(value, out timeoutSeconds)
           && timeoutSeconds is >= 1 and <= MaximumTimeoutSeconds;
}

public enum DockerExecutionOperationKind
{
    GetSettings,
    SaveSettings,
    AddImage,
    DeleteImage,
    RefreshImage,
    RefreshImages,
    PullImage,
    TestDocker,
}

public sealed record DockerExecutionOperationRequest(
    DockerExecutionOperationKind Kind,
    string? TimeoutSeconds = null,
    string? DockerCliPath = null,
    string? ImageReference = null);

public sealed record DockerExecutionOperationResponse(
    string? TimeoutSeconds = null,
    string? DockerCliPath = null,
    IReadOnlyList<DockerImageDefinition>? Images = null,
    bool Success = true,
    string? Message = null,
    long? CatalogRevision = null,
    DockerExecutionOperationError? Error = null);

public sealed record DockerExecutionOperationError(
    string Code,
    string Message,
    bool IsTransient,
    string CorrelationId);

public sealed record DockerImageDefinition(
    string ImageReference,
    DockerImageStatus Status,
    DateTimeOffset? LastPulledAtUtc,
    string? LastMessage);

public sealed record DockerImageReadiness(
    bool IsReady,
    string Message,
    DockerImageDefinition? Image);

public sealed record DockerImagePullResult(
    bool Success,
    string Message,
    DockerImageDefinition Image);

public enum DockerImageStatus
{
    NotPulled = 0,
    Ready = 1,
    Pulling = 2,
    Failed = 3,
    NeedsAttention = 4,
}
