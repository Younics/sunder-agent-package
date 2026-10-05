using Sunder.Generated.Rpc;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Generated.Rpc.Management;

namespace Sunder.Package.Agent.Execution.Local.App;

internal sealed class LocalExecutionSourceClient(ISunderRpcClient rpc) : ILocalExecutionPresentationClient
{
    public async ValueTask<LocalExecutionOperationResponse> ExecuteAsync(LocalExecutionOperationRequest request, CancellationToken token = default)
    {
        var api = new PackageApi(rpc).Management;
        Sunder.Generated.Rpc.PackageApis.CSunder_Package_Agent_Execution_Local_Presentation.Provider provider;
        try { provider = await api.ResolveSelectedPackageAsync(token).ConfigureAwait(false); }
        catch (SunderRpcException unavailable) when (unavailable.Error.Kind == SunderRpcErrorKind.NotFound && unavailable.Error.Code == "rpc.provider-not-found")
        { throw new SunderRpcException(new(SunderRpcErrorKind.Unavailable, "local.presentation.unavailable", "The local execution presentation provider is unavailable.")); }
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: token).ConfigureAwait(false);
        var result = await api.CreateClient(scope, provider).SPresentation.ExecuteAsync(new Wire.LocalExecutionOperationRequest
        {
            Kind = request.Kind switch
            {
                LocalExecutionOperationKind.GetSettings => Wire.LocalOperation.GetSettings,
                LocalExecutionOperationKind.SaveSettings => Wire.LocalOperation.SaveSettings,
                LocalExecutionOperationKind.SaveShells => Wire.LocalOperation.SaveShells,
                _ => throw new InvalidDataException("The local execution operation is invalid."),
            },
            TimeoutSeconds = request.TimeoutSeconds,
            Shells = request.Shells?.Select(shell => new Wire.LocalShellDefinition
            {
                ShellId = shell.ShellId, DisplayName = shell.DisplayName, ExecutablePath = shell.ExecutablePath,
                SyntaxKind = shell.SyntaxKind, IsDetected = shell.IsDetected,
            }).ToArray(),
            ExpectedShellCatalogRevision = request.ExpectedShellCatalogRevision,
        }, token).ConfigureAwait(false);
        return new(result.TimeoutSeconds, result.DetectedShells?.Select(Shell).ToArray(), result.CustomShells?.Select(Shell).ToArray(),
            result.ShellCatalogRevision, result.Message, result.Success,
            result.Error is { } error ? new(error.Code, error.Message, error.IsTransient, error.CorrelationId) : null);
    }

    private static LocalShellDefinition Shell(Wire.LocalShellDefinition shell)
        => new(shell.ShellId, shell.DisplayName, shell.ExecutablePath, shell.SyntaxKind, shell.IsDetected);
}
