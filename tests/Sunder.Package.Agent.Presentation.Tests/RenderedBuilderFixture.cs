extern alias BuilderApp;

using BuilderApp::Sunder.Package.Agent.Builder;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Presentation.Tests;

internal static class RenderedBuilderFixture
{
    internal sealed class ColdBuilderServices : IBuilderBackend, IBuilderJobJournal, IPackageDevelopmentService, IPackageLogNavigationService, IPackageAppStateUpdates, IPackageWorkerExecutionObservations
    {
        internal BuilderProjectInspection? Project { get; init; }
        public Task<string> GetBackendIdAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BuilderProjectInspection>> DiscoverAsync(string folder, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BuilderProjectInspection> InspectAsync(string entryPoint, CancellationToken cancellationToken = default)
            => Project is { } project ? Task.FromResult(project) : throw new NotSupportedException();
        public Task<BuilderToolchainStatus> GetToolchainsAsync(BuilderProjectKind kind, bool checkUpdates, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> StartJobAsync(BuilderJobRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BuilderJobSnapshot> GetJobAsync(string jobId, string backendId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelJobAsync(string jobId, string backendId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IBuilderJobExecution> CaptureExecutionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BuilderRecordedJobRecovery?> RecoverRecordedJobAsync(BuilderJobReservation reservation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        // These views must remain job/journal-cold. An accidental admission call
        // fails the existing fixture rather than receiving a production bypass.
        public Task<BuilderJobSnapshot> RunOwnedAsync(Func<BuilderJobScope, Task<BuilderJobSnapshot>> run,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> AdmitAndStartAsync(BuilderJobAdmission admission, IBuilderBackend backend,
            Func<CancellationToken, Task<IBuilderJobExecution>> capture, CancellationToken cancellationToken = default, BuilderJobScope? scope = null) => throw new NotSupportedException();
        public Task<BuilderJobStopResult> ReconcileAsync(string jobId, IBuilderBackend backend, CancellationToken cancellationToken = default, BuilderJobScope? scope = null) => throw new NotSupportedException();
        public Task<BuilderJobStopResult?> ReadCompletedAsync(BuilderJobReservation reservation, CancellationToken cancellationToken = default, BuilderJobScope? scope = null) => throw new NotSupportedException();
        public Task<BuilderJobStopResult> AbortKnownNotStartedAsync(BuilderJobAdmission admission, CancellationToken cancellationToken = default, BuilderJobScope? scope = null) => throw new NotSupportedException();
        public Task<BuilderJobStopResult> CompleteAsync(BuilderJobReservation reservation, BuilderJobStopResult result, CancellationToken cancellationToken = default, BuilderJobScope? scope = null) => throw new NotSupportedException();
        public Task<BuilderJobStopResult> CompleteOwnedAsync(BuilderJobAdmission admission, BuilderJobStopResult result, CancellationToken cancellationToken = default, BuilderJobScope? scope = null) => throw new NotSupportedException();
        public Task SavePartialAsync(BuilderJobReservation reservation, BuilderJobSnapshot snapshot, CancellationToken cancellationToken = default, BuilderJobScope? scope = null) => throw new NotSupportedException();
        public Task SaveFailureAsync(BuilderJobReservation reservation, string failure, CancellationToken cancellationToken = default, BuilderJobScope? scope = null) => throw new NotSupportedException();
        public Task<IPackageDevelopmentSession> LoadAsync(string developmentFolder, bool watch, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task OpenPackageLogsAsync(string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RunAsync(string key, Func<PackageAppStateUpdateContext, Task> update, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PackageWorkerExecutionReference> CaptureAsync<TService>(Sunder.Sdk.Services.ServiceProvider<TService> provider,
            CancellationToken cancellationToken = default) where TService : class => throw new NotSupportedException("View construction must remain execution-observation cold.");
        public Task<PackageWorkerExecutionObservation> ObserveAsync(PackageWorkerExecutionReference reference,
            CancellationToken cancellationToken = default) => throw new NotSupportedException("View construction must remain execution-observation cold.");
    }

}
