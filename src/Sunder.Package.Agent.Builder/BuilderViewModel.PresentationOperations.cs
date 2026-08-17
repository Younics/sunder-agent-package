using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderViewModel
{
    private static readonly PresentationOperation BuilderInitializationOperation = new(
        "builder.initialize-view",
        "Package Builder could not load. Its current state was preserved.");
    private static readonly PresentationOperation BuilderSetupOperation = new(
        "builder.setup",
        "Package Builder setup could not be completed. Setup state was refreshed.");
    private static readonly PresentationOperation BuilderCreateOperation = new(
        "builder.create-project",
        "The package project draft could not be created.");
    private static readonly PresentationOperation BuilderDeleteOperation = new(
        "builder.delete-project",
        "The package project could not be deleted. The project list was reconciled.");
    private static readonly PresentationOperation BuilderInitializeProjectOperation = new(
        "builder.initialize-project",
        "The package project could not be queued for initialization. Project state was refreshed.");
    private static readonly PresentationOperation BuilderBuildOperation = new(
        "builder.build-project",
        "The package build could not be queued. Project state was refreshed.");
    private static readonly PresentationOperation BuilderPublishOperation = new(
        "builder.publish-project",
        "The package publish could not be queued. Project state was refreshed.");
    private static readonly PresentationOperation BuilderPersistenceOperation = new(
        "builder.save-projects",
        "Package Builder settings could not be saved. Project state was refreshed.");

    private Task RunPresentationOperationAsync(
        PresentationOperation operation,
        Func<CancellationToken, Task> action,
        Func<CancellationToken, Task>? reconcile = null,
        CancellationToken cancellationToken = default)
        => _presentationOperations.RunAsync(
            operation,
            action,
            reconcile,
            cancellationToken);

    private Task RunPresentationOperationAsync(
        PresentationOperation operation,
        Func<CancellationToken, Task> action,
        Func<Exception, CancellationToken, Task> reconcile,
        CancellationToken cancellationToken = default)
        => _presentationOperations.RunAsync(
            operation,
            action,
            reconcile,
            cancellationToken);

    private Task ReconcileBuilderSnapshotAsync(CancellationToken cancellationToken)
        => InitializeCoreAsync(cancellationToken);
}
