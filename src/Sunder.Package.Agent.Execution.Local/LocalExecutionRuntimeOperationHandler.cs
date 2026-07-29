using Sunder.Agent.Execution.Common;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Execution.Local;

internal sealed class LocalExecutionRuntimeOperationHandler(
    IPackageContext packageContext,
    LocalShellCatalogService shellCatalog,
    LocalExecutionWorkspaceEditorContributor workspaceEditor)
    : IPackageRuntimeOperationHandler<LocalExecutionOperationRequest, LocalExecutionOperationResponse>
{
    private const int MaximumShellCount = 32;
    private const int MaximumPathLength = 1024;

    public async ValueTask<LocalExecutionOperationResponse> HandleAsync(
        LocalExecutionOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return request.Kind switch
            {
                LocalExecutionOperationKind.GetSettings => await GetSettingsAsync(cancellationToken),
                LocalExecutionOperationKind.SaveSettings => await SaveSettingsAsync(request, cancellationToken),
                LocalExecutionOperationKind.SaveShells => await SaveShellsAsync(request, cancellationToken),
                LocalExecutionOperationKind.GetWorkspaceEditor => await GetWorkspaceEditorAsync(request, cancellationToken),
                LocalExecutionOperationKind.SaveWorkspaceEditor => await SaveWorkspaceEditorAsync(request, cancellationToken),
                _ => throw new LocalExecutionDomainException(
                    "local.operation.unknown",
                    "The requested local execution operation is not supported."),
            };
        }
        catch (LocalExecutionDomainException exception)
        {
            return new LocalExecutionOperationResponse(
                Success: false,
                Error: new LocalExecutionOperationError(
                    exception.Code,
                    exception.Message,
                    exception.IsTransient));
        }
    }

    private async ValueTask<LocalExecutionOperationResponse> GetSettingsAsync(
        CancellationToken cancellationToken)
    {
        var snapshot = await shellCatalog.GetSnapshotAsync(cancellationToken);
        return new LocalExecutionOperationResponse(
            TimeoutSeconds: await packageContext.Settings.GetValueAsync(
                    LocalExecutionConfiguration.TimeoutKey,
                    cancellationToken)
                ?? LocalExecutionConfiguration.DefaultTimeoutSeconds,
            DetectedShells: snapshot.DetectedShells,
            CustomShells: snapshot.CustomShells,
            ShellCatalogRevision: snapshot.Revision);
    }

    private async ValueTask<LocalExecutionOperationResponse> SaveSettingsAsync(
        LocalExecutionOperationRequest request,
        CancellationToken cancellationToken)
    {
        if (!BoundedValue.TryParseInt32(
                request.TimeoutSeconds,
                minimum: 1,
                maximum: BoundedProcessRunner.MaximumTimeoutSeconds,
                out var timeoutSeconds))
        {
            throw new LocalExecutionDomainException(
                "local.timeout.invalid",
                $"Local shell timeout must be between 1 and {BoundedProcessRunner.MaximumTimeoutSeconds} seconds.");
        }

        await packageContext.Settings.SetValueAsync(
            LocalExecutionConfiguration.TimeoutKey,
            timeoutSeconds.ToString(),
            cancellationToken);
        return new(TimeoutSeconds: timeoutSeconds.ToString(), Message: "Local execution settings saved.");
    }

    private async ValueTask<LocalExecutionOperationResponse> SaveShellsAsync(
        LocalExecutionOperationRequest request,
        CancellationToken cancellationToken)
    {
        var shells = request.Shells
            ?? throw new LocalExecutionDomainException(
                "local.shell-catalog.shells-required",
                "Custom shell values are required to save shell settings.");
        if (shells.Count > MaximumShellCount)
        {
            throw new LocalExecutionDomainException(
                "local.shell-catalog.too-many",
                $"At most {MaximumShellCount} custom shells may be configured.");
        }

        foreach (var shell in shells)
        {
            var path = shell.ExecutablePath?.Trim() ?? string.Empty;
            if (path.Length == 0 || path.Length > MaximumPathLength || !Path.IsPathFullyQualified(path))
            {
                throw new LocalExecutionDomainException(
                    "local.shell.path-invalid",
                    "Custom shell paths must be bounded absolute paths selected from the local filesystem.");
            }

            if (!File.Exists(path))
            {
                throw new LocalExecutionDomainException(
                    "local.shell.not-found",
                    "A selected custom shell executable no longer exists.");
            }
        }

        if (request.ExpectedShellCatalogRevision is not { } expectedRevision || expectedRevision < 0)
        {
            throw new LocalExecutionDomainException(
                "local.shell-catalog.revision-required",
                "A shell catalog revision is required to save shell settings.");
        }

        var snapshot = await shellCatalog.SaveCustomShellsAsync(
            shells,
            expectedRevision,
            cancellationToken);
        return new(
            DetectedShells: snapshot.DetectedShells,
            CustomShells: snapshot.CustomShells,
            ShellCatalogRevision: snapshot.Revision,
            Message: "Shell settings saved.");
    }

    private async ValueTask<LocalExecutionOperationResponse> GetWorkspaceEditorAsync(
        LocalExecutionOperationRequest request,
        CancellationToken cancellationToken)
    {
        var context = request.EditorContext ?? throw new InvalidOperationException("Workspace editor context is required.");
        return new(EditorSections: await workspaceEditor.GetSectionsAsync(context, cancellationToken));
    }

    private async ValueTask<LocalExecutionOperationResponse> SaveWorkspaceEditorAsync(
        LocalExecutionOperationRequest request,
        CancellationToken cancellationToken)
    {
        var context = request.EditorContext ?? throw new InvalidOperationException("Workspace editor context is required.");
        var saveRequest = request.EditorSaveRequest ?? throw new InvalidOperationException("Workspace editor values are required.");
        return new(EditorSaveResult: await workspaceEditor.SaveSectionAsync(context, saveRequest, cancellationToken));
    }

}
