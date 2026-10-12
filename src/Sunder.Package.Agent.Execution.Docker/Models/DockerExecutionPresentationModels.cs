namespace Sunder.Package.Agent.Execution.Docker;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record DockerExecutionOperationResponse(
    string? TimeoutSeconds = null,
    string? DockerCliPath = null,
    IReadOnlyList<DockerImageDefinition>? Images = null,
    bool Success = true,
    string? Message = null,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")] long? CatalogRevision = null,
    DockerExecutionOperationError? Error = null);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record DockerExecutionOperationError(
    string Code,
    string Message,
    bool IsTransient,
    string CorrelationId);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record DockerImageDefinition(
    [property: Sunder.Sdk.Services.SunderValueConstraints(MinLength = 1, MaxLength = 512)]
    string ImageReference,
    DockerImageStatus Status,
    DateTimeOffset? LastPulledAtUtc,
    string? LastMessage);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record DockerImageReadiness(
    bool IsReady,
    string Message,
    DockerImageDefinition? Image);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record DockerImagePullResult(
    bool Success,
    string Message,
    DockerImageDefinition Image);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public enum DockerImageStatus
{
    NotPulled = 0,
    Ready = 1,
    Pulling = 2,
    Failed = 3,
    NeedsAttention = 4,
}
