using System.Globalization;
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
    public const string ContractVersion = "2.0.0";
    public const string ProviderId = "docker.presentation";
    public const string ServiceId = "presentation";

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
            => ToWire(await client.ExecuteAsync(FromWire(request), cancellationToken).ConfigureAwait(false));
    }

    internal static Wire.DockerExecutionOperationRequest ToWire(DockerExecutionOperationRequest value)
        => new()
        {
            Kind = (long)value.Kind,
            TimeoutSeconds = value.TimeoutSeconds,
            DockerCliPath = value.DockerCliPath,
            ImageReference = value.ImageReference,
        };

    private static DockerExecutionOperationRequest FromWire(Wire.DockerExecutionOperationRequest value)
        => new(
            (DockerExecutionOperationKind)checked((int)value.Kind),
            value.TimeoutSeconds,
            value.DockerCliPath,
            value.ImageReference);

    private static Wire.DockerExecutionOperationResponse ToWire(DockerExecutionOperationResponse value)
        => new()
        {
            TimeoutSeconds = value.TimeoutSeconds,
            DockerCliPath = value.DockerCliPath,
            Images = value.Images?.Select(ToWire).ToArray(),
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

    internal static DockerExecutionOperationResponse FromWire(Wire.DockerExecutionOperationResponse value)
        => new(
            value.TimeoutSeconds,
            value.DockerCliPath,
            value.Images?.Select(FromWire).ToArray(),
            value.Success,
            value.Message,
            value.CatalogRevision,
            value.Error is null ? null : new DockerExecutionOperationError(
                value.Error.Code,
                value.Error.Message,
                value.Error.IsTransient,
                value.Error.CorrelationId));

    private static Wire.DockerImageDefinition ToWire(DockerImageDefinition value)
        => new()
        {
            ImageReference = value.ImageReference,
            Status = (long)value.Status,
            LastPulledAtUtc = value.LastPulledAtUtc?.ToString("O", CultureInfo.InvariantCulture),
            LastMessage = value.LastMessage,
        };

    private static DockerImageDefinition FromWire(Wire.DockerImageDefinition value)
        => new(
            value.ImageReference,
            (DockerImageStatus)checked((int)value.Status),
            value.LastPulledAtUtc is null
                ? null
                : DateTimeOffset.Parse(value.LastPulledAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            value.LastMessage);
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
        return DockerExecutionPresentationRpc.FromWire(response);
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
