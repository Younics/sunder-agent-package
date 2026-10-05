using Sunder.Generated.Rpc;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Generated.Rpc.Management;

namespace Sunder.Package.Agent.Execution.Docker.App;

internal sealed class DockerExecutionSourceClient(ISunderRpcClient rpc) : IDockerExecutionPresentationClient
{
    public async ValueTask<DockerExecutionOperationResponse> ExecuteAsync(DockerExecutionOperationRequest request, CancellationToken token = default)
    {
        var api = new PackageApi(rpc).Management;
        Sunder.Generated.Rpc.PackageApis.CSunder_Package_Agent_Execution_Docker_Presentation.Provider provider;
        try { provider = await api.ResolveSelectedPackageAsync(token).ConfigureAwait(false); }
        catch (SunderRpcException unavailable) when (unavailable.Error.Kind == SunderRpcErrorKind.NotFound && unavailable.Error.Code == "rpc.provider-not-found")
        { throw new SunderRpcException(new(SunderRpcErrorKind.Unavailable, "docker.presentation.unavailable", "The Docker execution presentation provider is unavailable.")); }
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: token).ConfigureAwait(false);
        var result = await api.CreateClient(scope, provider).SPresentation.ExecuteAsync(new Wire.DockerExecutionOperationRequest
        {
            Kind = request.Kind switch
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
            TimeoutSeconds = request.TimeoutSeconds,
            DockerCliPath = request.DockerCliPath, ImageReference = request.ImageReference,
        }, token).ConfigureAwait(false);
        var images = result.ImageCatalog is { } content ? await ImageCatalogContent.Read(scope, content, token).ConfigureAwait(false) : null;
        return new(result.TimeoutSeconds, result.DockerCliPath, images, result.Success, result.Message, result.CatalogRevision,
            result.Error is { } error ? new(error.Code, error.Message, error.IsTransient, error.CorrelationId) : null);
    }
}
