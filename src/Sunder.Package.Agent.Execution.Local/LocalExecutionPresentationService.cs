using Sunder.Agent.Execution.Common;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Execution.Local;

[SunderService(Key = "management", ContractId = "younics.ai.agent.execution.local.presentation", ServiceId = "presentation")]
[SunderServiceProvider(ProviderId = "local.presentation")]
internal sealed partial class LocalExecutionPresentationService(IPackageContext packageContext, LocalShellCatalogService shellCatalog)
{
    private const int MaximumShellCount = 32;
    private const int MaximumPathLength = 1024;

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public async ValueTask<LocalExecutionOperationResponse> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await shellCatalog.GetSnapshotAsync(cancellationToken);
            return new(
                TimeoutSeconds: await packageContext.Settings.GetValueAsync(LocalExecutionConfiguration.TimeoutKey, cancellationToken)
                    ?? LocalExecutionConfiguration.DefaultTimeoutSeconds,
                DetectedShells: snapshot.DetectedShells,
                CustomShells: snapshot.CustomShells,
                ShellCatalogRevision: snapshot.Revision);
        }
        catch (LocalExecutionDomainException exception) { return Failure(exception); }
    }

    public async ValueTask<LocalExecutionOperationResponse> SaveSettingsAsync(string? timeoutSeconds, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!BoundedValue.TryParseInt32(timeoutSeconds, minimum: 1,
                    maximum: BoundedProcessRunner.MaximumTimeoutSeconds, out var parsedTimeout))
                throw new LocalExecutionDomainException("local.timeout.invalid",
                    $"Local shell timeout must be between 1 and {BoundedProcessRunner.MaximumTimeoutSeconds} seconds.");
            await packageContext.Settings.SetValueAsync(LocalExecutionConfiguration.TimeoutKey, parsedTimeout.ToString(), cancellationToken);
            return new(TimeoutSeconds: parsedTimeout.ToString(), Message: "Local execution settings saved.");
        }
        catch (LocalExecutionDomainException exception) { return Failure(exception); }
    }

    public async ValueTask<LocalExecutionOperationResponse> SaveShellsAsync(IReadOnlyList<LocalShellDefinition>? shells,
        [SunderValueConstraints(Minimum = "0")] long? expectedShellCatalogRevision, CancellationToken cancellationToken = default)
    {
        try
        {
            if (shells is null)
                throw new LocalExecutionDomainException("local.shell-catalog.shells-required", "Custom shell values are required to save shell settings.");
            if (shells.Count > MaximumShellCount)
                throw new LocalExecutionDomainException("local.shell-catalog.too-many", $"At most {MaximumShellCount} custom shells may be configured.");
            foreach (var shell in shells)
            {
                var path = shell.ExecutablePath?.Trim() ?? string.Empty;
                if (path.Length == 0 || path.Length > MaximumPathLength || !Path.IsPathFullyQualified(path))
                    throw new LocalExecutionDomainException("local.shell.path-invalid",
                        "Custom shell paths must be bounded absolute paths selected from the local filesystem.");
                if (!File.Exists(path))
                    throw new LocalExecutionDomainException("local.shell.not-found", "A selected custom shell executable no longer exists.");
            }
            if (expectedShellCatalogRevision is not { } expectedRevision || expectedRevision < 0)
                throw new LocalExecutionDomainException("local.shell-catalog.revision-required", "A shell catalog revision is required to save shell settings.");
            var snapshot = await shellCatalog.SaveCustomShellsAsync(shells, expectedRevision, cancellationToken);
            return new(DetectedShells: snapshot.DetectedShells, CustomShells: snapshot.CustomShells,
                ShellCatalogRevision: snapshot.Revision, Message: "Shell settings saved.");
        }
        catch (LocalExecutionDomainException exception) { return Failure(exception); }
    }

    private static LocalExecutionOperationResponse Failure(LocalExecutionDomainException exception)
        => new(Success: false, Error: new(exception.Code, exception.Message, exception.IsTransient));
}
