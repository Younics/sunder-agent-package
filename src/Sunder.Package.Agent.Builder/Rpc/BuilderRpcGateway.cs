using Sunder.Generated.Rpc;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Generated.Rpc.BuilderRuntime;

namespace Sunder.Package.Agent.Builder;

// App transport over the generated, role-local package API.
internal sealed class BuilderRpcGateway(ISunderRpcClient rpcClient) : IBuilderRpcGateway
{
    public async ValueTask<BuilderRpcResponse> InvokeAsync(
        BuilderRpcRequest request,
        CancellationToken cancellationToken = default)
    {
        if (BuilderRpcPayloadLimits.GetSerializedSize(request) >= BuilderRpcPayloadLimits.MaximumRequestBytes)
            throw new InvalidOperationException("Package Builder RPC request exceeds the host transport limit.");

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var api = new PackageApi(rpcClient).BuilderRuntime;
            var provider = await api.ResolveSelectedPackageAsync(cancellationToken).ConfigureAwait(false);
            await using var scope = await rpcClient.CreateCallScopeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var response = await api.CreateClient(scope, provider).SBuilder_2druntime
                .ExecuteAsync(ToWire(request), cancellationToken).ConfigureAwait(false);
            return await BuilderRpcPayloadLimits.ReadResponseAsync(scope, response.Content, cancellationToken).ConfigureAwait(false);
        }
        catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Cancelled)
        {
            throw new OperationCanceledException(exception.Message, exception,
                cancellationToken.IsCancellationRequested ? cancellationToken : CancellationToken.None);
        }
        catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.NotFound
            && exception.Error.Code == "rpc.provider-not-found")
        {
            throw new SunderRpcException(new SunderRpcError(SunderRpcErrorKind.Unavailable,
                "agent.builder.workspace-execution-unavailable", "Package Builder workspace execution is unavailable."));
        }
    }

    private static Wire.BuilderExecuteRequest ToWire(BuilderRpcRequest request)
        => new()
        {
            Kind = request.Kind switch
            {
                BuilderRpcOperationKind.ListWorkspaces => Wire.BuilderOperation.ListWorkspaces,
                BuilderRpcOperationKind.ResolveWorkspace => Wire.BuilderOperation.ResolveWorkspace,
                BuilderRpcOperationKind.ExecuteProcess => Wire.BuilderOperation.ExecuteProcess,
                BuilderRpcOperationKind.ExecuteShell => Wire.BuilderOperation.ExecuteShell,
                BuilderRpcOperationKind.MapToHostPath => Wire.BuilderOperation.MapToHostPath,
                BuilderRpcOperationKind.AddPathEntry => Wire.BuilderOperation.AddPathEntry,
                _ => throw new InvalidDataException("The Builder RPC operation is invalid."),
            },
            WorkspaceId = request.WorkspaceId,
            FileName = request.FileName,
            Arguments = request.Arguments,
            Command = request.Command,
            WorkingDirectory = request.WorkingDirectory,
            TimeoutSeconds = request.TimeoutSeconds,
            ExecutionPath = request.ExecutionPath,
        };
}
