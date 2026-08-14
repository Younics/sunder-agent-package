using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Package.Agent.Execution.Local.Protocol.Generated;

namespace Sunder.Package.Agent.Execution.Local;

public interface ILocalExecutionPresentationClient
{
    ValueTask<LocalExecutionOperationResponse> ExecuteAsync(
        LocalExecutionOperationRequest request,
        CancellationToken cancellationToken = default);
}

public static class LocalExecutionPresentationRpc
{
    public const string ContractId = "sunder.package.agent.execution.local.presentation";
    public const string ContractVersion = "2.0.0";
    public const string ProviderId = "local.presentation";
    public const string ServiceId = "presentation";

    private const string DescriptorResourceName =
        "Sunder.Package.Agent.Execution.Local.Protocol.Contracts.local-execution-presentation.rpc.json";
    private static readonly Lazy<SunderRpcContractDescriptor> ContractDescriptor =
        new(LoadDescriptor, LazyThreadSafetyMode.ExecutionAndPublication);

    public static SunderRpcContractDescriptor Descriptor => ContractDescriptor.Value;

    public static ISunderRpcServiceHandler CreateHandler(ILocalExecutionPresentationClient client)
        => new Wire.SunderPackageAgentExecutionLocalPresentationRpcHandler(
            new ProviderAdapter(client));

    private static SunderRpcContractDescriptor LoadDescriptor()
    {
        using var stream = typeof(LocalExecutionPresentationRpc).Assembly
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

    private sealed class ProviderAdapter(ILocalExecutionPresentationClient client)
        : Wire.IPresentationProvider
    {
        public async ValueTask<Wire.LocalExecutionOperationResponse> ExecuteAsync(
            SunderRpcInvocationContext context,
            Wire.LocalExecutionOperationRequest request,
            CancellationToken cancellationToken = default)
            => ToWire(await client.ExecuteAsync(FromWire(request), cancellationToken).ConfigureAwait(false));
    }

    internal static Wire.LocalExecutionOperationRequest ToWire(LocalExecutionOperationRequest value)
        => new()
        {
            Kind = (long)value.Kind,
            TimeoutSeconds = value.TimeoutSeconds,
            Shells = value.Shells?.Select(ToWire).ToArray(),
            ExpectedShellCatalogRevision = value.ExpectedShellCatalogRevision,
        };

    private static LocalExecutionOperationRequest FromWire(Wire.LocalExecutionOperationRequest value)
        => new(
            (LocalExecutionOperationKind)checked((int)value.Kind),
            value.TimeoutSeconds,
            value.Shells?.Select(FromWire).ToArray(),
            value.ExpectedShellCatalogRevision);

    private static Wire.LocalExecutionOperationResponse ToWire(LocalExecutionOperationResponse value)
        => new()
        {
            TimeoutSeconds = value.TimeoutSeconds,
            DetectedShells = value.DetectedShells?.Select(ToWire).ToArray(),
            CustomShells = value.CustomShells?.Select(ToWire).ToArray(),
            ShellCatalogRevision = value.ShellCatalogRevision,
            Message = value.Message,
            Success = value.Success,
            Error = value.Error is null ? null : new Wire.LocalExecutionOperationError
            {
                Code = value.Error.Code,
                Message = value.Error.Message,
                IsTransient = value.Error.IsTransient,
                CorrelationId = value.Error.CorrelationId,
            },
        };

    internal static LocalExecutionOperationResponse FromWire(Wire.LocalExecutionOperationResponse value)
        => new(
            value.TimeoutSeconds,
            value.DetectedShells?.Select(FromWire).ToArray(),
            value.CustomShells?.Select(FromWire).ToArray(),
            value.ShellCatalogRevision,
            value.Message,
            value.Success,
            value.Error is null ? null : new LocalExecutionOperationError(
                value.Error.Code,
                value.Error.Message,
                value.Error.IsTransient,
                value.Error.CorrelationId));

    private static Wire.LocalShellDefinition ToWire(LocalShellDefinition value)
        => new()
        {
            ShellId = value.ShellId,
            DisplayName = value.DisplayName,
            ExecutablePath = value.ExecutablePath,
            SyntaxKind = value.SyntaxKind,
            IsDetected = value.IsDetected,
        };

    private static LocalShellDefinition FromWire(Wire.LocalShellDefinition value)
        => new(value.ShellId, value.DisplayName, value.ExecutablePath, value.SyntaxKind, value.IsDetected);
}

public sealed class LocalExecutionPresentationRpcClient(ISunderRpcClient client)
    : ILocalExecutionPresentationClient
{
    public async ValueTask<LocalExecutionOperationResponse> ExecuteAsync(
        LocalExecutionOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        var descriptor = LocalExecutionPresentationRpc.Descriptor;
        var catalog = await client.DiscoverAsync(descriptor.ContractId, cancellationToken)
            .ConfigureAwait(false);
        var providers = catalog.Providers.Where(provider =>
                string.Equals(provider.ProviderId, LocalExecutionPresentationRpc.ProviderId, StringComparison.Ordinal)
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
                "local.presentation.unavailable",
                "The local execution presentation provider is unavailable."));
        }

        await using var scope = await client.CreateCallScopeAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var response = await new Wire.PresentationClient(scope, providers[0].Endpoint)
            .ExecuteAsync(LocalExecutionPresentationRpc.ToWire(request), cancellationToken)
            .ConfigureAwait(false);
        return LocalExecutionPresentationRpc.FromWire(response);
    }
}

public static class LocalExecutionPresentationDefaults
{
    public const string TimeoutKey = "shell.timeoutSeconds.default";
    public const string DefaultTimeoutSeconds = "300";
    public const int MaximumTimeoutSeconds = 86_400;

    public static bool TryParseTimeout(string? value, out int timeoutSeconds)
        => int.TryParse(value, out timeoutSeconds)
           && timeoutSeconds is >= 1 and <= MaximumTimeoutSeconds;
}

public enum LocalExecutionOperationKind
{
    GetSettings,
    SaveSettings,
    SaveShells,
}

public sealed record LocalExecutionOperationRequest(
    LocalExecutionOperationKind Kind,
    string? TimeoutSeconds = null,
    IReadOnlyList<LocalShellDefinition>? Shells = null,
    long? ExpectedShellCatalogRevision = null);

public sealed record LocalExecutionOperationResponse(
    string? TimeoutSeconds = null,
    IReadOnlyList<LocalShellDefinition>? DetectedShells = null,
    IReadOnlyList<LocalShellDefinition>? CustomShells = null,
    long? ShellCatalogRevision = null,
    string? Message = null,
    bool Success = true,
    LocalExecutionOperationError? Error = null);

public sealed record LocalExecutionOperationError(
    string Code,
    string Message,
    bool IsTransient = false,
    string? CorrelationId = null);

public sealed record LocalShellDefinition(
    string ShellId,
    string DisplayName,
    string ExecutablePath,
    string SyntaxKind,
    bool IsDetected);
