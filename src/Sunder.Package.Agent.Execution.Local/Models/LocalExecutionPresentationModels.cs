namespace Sunder.Package.Agent.Execution.Local;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record LocalExecutionOperationResponse(
    string? TimeoutSeconds = null,
    IReadOnlyList<LocalShellDefinition>? DetectedShells = null,
    IReadOnlyList<LocalShellDefinition>? CustomShells = null,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")] long? ShellCatalogRevision = null,
    string? Message = null,
    bool Success = true,
    LocalExecutionOperationError? Error = null);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record LocalExecutionOperationError(
    string Code,
    string Message,
    bool IsTransient = false,
    string? CorrelationId = null);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record LocalShellDefinition(
    string ShellId,
    string DisplayName,
    string ExecutablePath,
    string SyntaxKind,
    bool IsDetected);
