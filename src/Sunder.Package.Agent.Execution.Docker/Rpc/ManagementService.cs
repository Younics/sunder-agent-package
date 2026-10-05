using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;
using static System.Text.Json.Serialization.JsonIgnoreCondition;

namespace Sunder.Package.Agent.Execution.Docker.Rpc;

[SunderRpcService(SunderRpcExposure.Package, ContractId = "sunder.package.agent.execution.docker.presentation", ServiceId = "presentation", IncludeSchemaDialect = true)]
[SunderRpcProvider(ProviderId = "docker.presentation")]
internal sealed partial class ManagementService(IDockerExecutionPresentationClient operations)
{
    [SunderRpcMethod]
    public async ValueTask<DockerExecutionOperationResponse> ExecuteAsync(DockerExecutionOperationRequest request, SunderRpcInvocationContext context, CancellationToken token = default)
    {
        var result = await operations.ExecuteAsync(new(request.Kind switch
            {
                DockerOperation.GetSettings => DockerExecutionOperationKind.GetSettings,
                DockerOperation.SaveSettings => DockerExecutionOperationKind.SaveSettings,
                DockerOperation.AddImage => DockerExecutionOperationKind.AddImage,
                DockerOperation.DeleteImage => DockerExecutionOperationKind.DeleteImage,
                DockerOperation.RefreshImage => DockerExecutionOperationKind.RefreshImage,
                DockerOperation.RefreshImages => DockerExecutionOperationKind.RefreshImages,
                DockerOperation.PullImage => DockerExecutionOperationKind.PullImage,
                DockerOperation.TestDocker => DockerExecutionOperationKind.TestDocker,
                _ => throw new InvalidDataException("The Docker execution operation is invalid."),
            },
            request.TimeoutSeconds.IsPresent ? request.TimeoutSeconds.Value.Value : null,
            request.DockerCliPath.IsPresent ? request.DockerCliPath.Value.Value : null,
            request.ImageReference.IsPresent ? request.ImageReference.Value.Value : null), token).ConfigureAwait(false);
        return new()
        {
            TimeoutSeconds = result.TimeoutSeconds is { } timeout ? new TimeoutText { Value = timeout } : default(SunderRpcOptional<TimeoutText>),
            DockerCliPath = result.DockerCliPath is { } path ? new ExecutablePath { Value = path } : default(SunderRpcOptional<ExecutablePath>),
            ImageCatalog = result.Images is { } images ? new ContentReference { Value = await ImageCatalogContent.Write(context, images, token).ConfigureAwait(false) } : default(SunderRpcOptional<ContentReference>),
            Success = result.Success,
            Message = result.Message is { } message ? new OperationMessage { Value = message } : default(SunderRpcOptional<OperationMessage>),
            CatalogRevision = result.CatalogRevision is { } revision ? new CatalogRevision { Value = revision } : default(SunderRpcOptional<CatalogRevision>),
            Error = result.Error is { } error ? new DockerExecutionOperationError
            {
                Code = new() { Value = error.Code }, Message = new() { Value = error.Message },
                IsTransient = error.IsTransient, CorrelationId = new() { Value = error.CorrelationId },
            } : default(SunderRpcOptional<DockerExecutionOperationError>),
        };
    }
}

internal enum DockerOperation
{
    [JsonStringEnumMemberName("get-settings")] GetSettings,
    [JsonStringEnumMemberName("save-settings")] SaveSettings,
    [JsonStringEnumMemberName("add-image")] AddImage,
    [JsonStringEnumMemberName("delete-image")] DeleteImage,
    [JsonStringEnumMemberName("refresh-image")] RefreshImage,
    [JsonStringEnumMemberName("refresh-images")] RefreshImages,
    [JsonStringEnumMemberName("pull-image")] PullImage,
    [JsonStringEnumMemberName("test-docker")] TestDocker,
}
[SunderRpcTransparent] internal sealed class TimeoutText { public required string Value { get; init; } }
[SunderRpcTransparent] internal sealed class ExecutablePath { public required string Value { get; init; } }
[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)] internal sealed class ImageReference { public required string Value { get; init; } }
[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)] internal sealed class OperationMessage { public required string Value { get; init; } }
[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)] internal sealed class DiagnosticCode { public required string Value { get; init; } }
[SunderRpcTransparent, SunderRpcFacet(Minimum = "0")] internal sealed class CatalogRevision { public required long Value { get; init; } }
[SunderRpcTransparent] internal sealed class ContentReference { public required SunderRpcContentReference Value { get; init; } }
internal sealed class DockerExecutionOperationError
{
    public required DiagnosticCode Code { get; init; }
    public required OperationMessage Message { get; init; }
    public required bool IsTransient { get; init; }
    public required DiagnosticCode CorrelationId { get; init; }
}
internal sealed class DockerExecutionOperationRequest
{
    public required DockerOperation Kind { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<TimeoutText> TimeoutSeconds { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<ExecutablePath> DockerCliPath { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<ImageReference> ImageReference { get; init; }
}
internal sealed class DockerExecutionOperationResponse
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<TimeoutText> TimeoutSeconds { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<ExecutablePath> DockerCliPath { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<ContentReference> ImageCatalog { get; init; }
    public required bool Success { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<OperationMessage> Message { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<CatalogRevision> CatalogRevision { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<DockerExecutionOperationError> Error { get; init; }
}
