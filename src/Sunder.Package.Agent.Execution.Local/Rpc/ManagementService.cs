using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Execution.Local;
using static System.Text.Json.Serialization.JsonIgnoreCondition;

namespace Sunder.Package.Agent.Execution.Local.Rpc;

[SunderRpcService(SunderRpcExposure.Package, ContractId = "sunder.package.agent.execution.local.presentation", ServiceId = "presentation", IncludeSchemaDialect = true)]
[SunderRpcProvider(ProviderId = "local.presentation")]
internal sealed partial class ManagementService(ILocalExecutionPresentationClient operations)
{
    [SunderRpcMethod]
    public async ValueTask<LocalExecutionOperationResponse> ExecuteAsync(LocalExecutionOperationRequest request, CancellationToken token = default)
    {
        var result = await operations.ExecuteAsync(new(
            request.Kind switch
            {
                LocalOperation.GetSettings => LocalExecutionOperationKind.GetSettings,
                LocalOperation.SaveSettings => LocalExecutionOperationKind.SaveSettings,
                LocalOperation.SaveShells => LocalExecutionOperationKind.SaveShells,
                _ => throw new InvalidDataException("The local execution operation is invalid."),
            },
            request.TimeoutSeconds.IsPresent ? request.TimeoutSeconds.Value.Value : null,
            request.Shells.IsPresent ? request.Shells.Value.Value.Select(shell => new Domain.LocalShellDefinition(
                shell.ShellId.Value, shell.DisplayName.Value, shell.ExecutablePath.Value, shell.SyntaxKind.Value, shell.IsDetected)).ToArray() : null,
            request.ExpectedShellCatalogRevision.IsPresent ? request.ExpectedShellCatalogRevision.Value.Value : null), token).ConfigureAwait(false);
        return new()
        {
            TimeoutSeconds = result.TimeoutSeconds is { } timeout ? new TimeoutText { Value = timeout } : default(SunderRpcOptional<TimeoutText>),
            DetectedShells = Shells(result.DetectedShells),
            CustomShells = Shells(result.CustomShells),
            ShellCatalogRevision = result.ShellCatalogRevision is { } revision ? new CatalogRevision { Value = revision } : default(SunderRpcOptional<CatalogRevision>),
            Message = result.Message is { } message ? new OperationMessage { Value = message } : default(SunderRpcOptional<OperationMessage>),
            Success = result.Success,
            Error = result.Error is { } error ? new LocalExecutionOperationError
            {
                Code = new() { Value = error.Code }, Message = new() { Value = error.Message }, IsTransient = error.IsTransient,
                CorrelationId = error.CorrelationId is { } correlation ? new DiagnosticCode { Value = correlation } : default(SunderRpcOptional<DiagnosticCode>),
            } : default(SunderRpcOptional<LocalExecutionOperationError>),
        };
    }

    private static SunderRpcOptional<ShellCatalog> Shells(IReadOnlyList<Domain.LocalShellDefinition>? shells)
        => shells is null ? default(SunderRpcOptional<ShellCatalog>) : new ShellCatalog
        {
            Value = shells.Select(shell => new LocalShellDefinition
            {
                ShellId = new() { Value = shell.ShellId }, DisplayName = new() { Value = shell.DisplayName },
                ExecutablePath = new() { Value = shell.ExecutablePath }, SyntaxKind = new() { Value = shell.SyntaxKind }, IsDetected = shell.IsDetected,
            }).ToArray(),
        };
}

internal enum LocalOperation
{
    [JsonStringEnumMemberName("get-settings")] GetSettings,
    [JsonStringEnumMemberName("save-settings")] SaveSettings,
    [JsonStringEnumMemberName("save-shells")] SaveShells,
}
[SunderRpcTransparent] internal sealed class TimeoutText { public required string Value { get; init; } }
[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)] internal sealed class ShellId { public required string Value { get; init; } }
[SunderRpcTransparent] internal sealed class DisplayName { public required string Value { get; init; } }
[SunderRpcTransparent] internal sealed class ExecutablePath { public required string Value { get; init; } }
[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)] internal sealed class ShellSyntax { public required string Value { get; init; } }
[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)] internal sealed class OperationMessage { public required string Value { get; init; } }
[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)] internal sealed class DiagnosticCode { public required string Value { get; init; } }
[SunderRpcTransparent, SunderRpcFacet(Minimum = "0")] internal sealed class CatalogRevision { public required long Value { get; init; } }
internal sealed class LocalShellDefinition
{
    public required ShellId ShellId { get; init; }
    public required DisplayName DisplayName { get; init; }
    public required ExecutablePath ExecutablePath { get; init; }
    public required ShellSyntax SyntaxKind { get; init; }
    public required bool IsDetected { get; init; }
}
[SunderRpcTransparent] internal sealed class ShellCatalog { public required IReadOnlyList<LocalShellDefinition> Value { get; init; } }
internal sealed class LocalExecutionOperationError
{
    public required DiagnosticCode Code { get; init; }
    public required OperationMessage Message { get; init; }
    public required bool IsTransient { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<DiagnosticCode> CorrelationId { get; init; }
}
internal sealed class LocalExecutionOperationRequest
{
    public required LocalOperation Kind { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<TimeoutText> TimeoutSeconds { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<ShellCatalog> Shells { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<CatalogRevision> ExpectedShellCatalogRevision { get; init; }
}
internal sealed class LocalExecutionOperationResponse
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<TimeoutText> TimeoutSeconds { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<ShellCatalog> DetectedShells { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<ShellCatalog> CustomShells { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<CatalogRevision> ShellCatalogRevision { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<OperationMessage> Message { get; init; }
    public required bool Success { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<LocalExecutionOperationError> Error { get; init; }
}
