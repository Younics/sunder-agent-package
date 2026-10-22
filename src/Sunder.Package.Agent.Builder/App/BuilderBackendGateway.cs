using Sunder.Generated.Packages.Younics_Sunder_Package_Maker_Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Builder;

/// <summary>Production App adapter over the generated owner-only Builder contract.</summary>
public sealed class BuilderBackendGateway(
    IServiceProviders<IBuilderRuntimeService> runtimes,
    IPackageWorkerExecutionObservations? observations = null) : IBuilderBackend
{
    public async Task<IReadOnlyList<BuilderProjectInspection>> DiscoverAsync(string folder, CancellationToken cancellationToken = default)
        => await BuilderContent.ReadAsync<BuilderProjectInspection[]>(
            (await ResolveAsync(cancellationToken).ConfigureAwait(false)).Service.DiscoverAsync(folder, cancellationToken), cancellationToken).ConfigureAwait(false);

    public async Task<BuilderProjectInspection> InspectAsync(string entryPoint, CancellationToken cancellationToken = default)
        => await BuilderContent.ReadAsync<BuilderProjectInspection>(
            (await ResolveAsync(cancellationToken).ConfigureAwait(false)).Service.InspectAsync(entryPoint, cancellationToken), cancellationToken).ConfigureAwait(false);

    public async Task<BuilderToolchainStatus> GetToolchainsAsync(BuilderProjectKind kind, bool checkUpdates, CancellationToken cancellationToken = default)
        => await BuilderContent.ReadAsync<BuilderToolchainStatus>((await ResolveAsync(cancellationToken).ConfigureAwait(false)).Service.GetToolchainsAsync(
            kind == BuilderProjectKind.Dotnet ? "dotnet" : "node", checkUpdates, cancellationToken), cancellationToken).ConfigureAwait(false);

    public async Task<IBuilderJobExecution> CaptureExecutionAsync(CancellationToken cancellationToken = default)
    {
        // Source-only discovery does not need this observation service. Every job,
        // including source mutations, requires exact execution capture before Start.
        if (observations is null)
            throw new NotSupportedException("Builder jobs require a Host with worker execution observations. Source inspection does not use this feature.");
        var captured = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        var backendId = await captured.Service.GetBackendIdAsync(cancellationToken).ConfigureAwait(false);
        if (!Guid.TryParseExact(backendId, "N", out _)) throw new InvalidDataException("Builder returned an invalid backend identity.");
        var reference = await observations.CaptureAsync(captured, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new CapturedExecution(captured, backendId, reference, observations, this);
    }

    public Task<PackageWorkerExecutionObservation> ObserveExecutionAsync(PackageWorkerExecutionReference reference, CancellationToken cancellationToken = default)
        => (observations ?? throw new NotSupportedException("Builder jobs require Host execution observations."))
            .ObserveAsync(reference, cancellationToken);

    public async Task<BuilderRecordedJobRecovery?> RecoverRecordedJobAsync(BuilderJobReservation reservation, CancellationToken cancellationToken = default)
    {
        var catalog = await runtimes.ListAsync(cancellationToken).ConfigureAwait(false);
        var matches = catalog.Providers.Where(provider => provider.State == ServiceProviderState.Active
            && provider.PackageId == BuilderViewModel.BuilderPackageId
            && reservation.ProviderIdentity.HasSameExecutionCandidate(Identity(provider))).Take(2).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1) throw new SunderRpcException(new(SunderRpcErrorKind.Protocol,
            "agent.builder.ambiguous-recovery", "More than one exact original Builder provider was returned."));
        try
        {
            // A candidate is NOT binding equivalence. SessionGeneration (and the
            // SDK-owned opaque endpoint) may refresh. Only current SDK capture of
            // the exact original Host nonce can bridge it to the same execution.
            var fresh = matches[0];
            var backendId = await fresh.Service.GetBackendIdAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(backendId, "N", out _)) throw new InvalidDataException("Builder returned an invalid recovery backend identity.");
            if (backendId != reservation.BackendId) return null;
            var reference = await (observations ?? throw new NotSupportedException("Host execution observations are unavailable."))
                .CaptureAsync(fresh, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (reference != reservation.ExecutionReference) return null;
            BuilderJobSnapshot? snapshot;
            try
            {
                snapshot = await BuilderContent.ReadAsync<BuilderJobSnapshot>(fresh.Service.GetJobAsync(
                    reservation.JobId, reservation.BackendId, cancellationToken), cancellationToken).ConfigureAwait(false);
                BuilderJobJournal.ValidateSnapshot(reservation, snapshot);
                cancellationToken.ThrowIfCancellationRequested();
                if (snapshot.State == "running")
                {
                    // The only recovery mutation is an idempotent stop of this
                    // original job after all three independent fences matched.
                    await fresh.Service.CancelJobAsync(reservation.JobId, reservation.BackendId, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    snapshot = await BuilderContent.ReadAsync<BuilderJobSnapshot>(fresh.Service.GetJobAsync(
                        reservation.JobId, reservation.BackendId, cancellationToken), cancellationToken).ConfigureAwait(false);
                    BuilderJobJournal.ValidateSnapshot(reservation, snapshot);
                }
            }
            catch (SunderRpcException error) when (error.Error.Kind == SunderRpcErrorKind.Domain
                && error.Error.Code == "agent.builder.job-not-found") { snapshot = null; }
            cancellationToken.ThrowIfCancellationRequested();
            return new(snapshot, Identity(fresh), reference, backendId);
        }
        catch (SunderRpcException error) when (error.Error.Kind == SunderRpcErrorKind.Domain
            && error.Error.Code is "agent.builder.job-not-found" or "agent.builder.backend-changed") { return null; }
        catch (SunderRpcException error) when (error.Error.Kind == SunderRpcErrorKind.StaleEndpoint
            && error.Error.Code != "rpc.caller-retired") { return null; }
    }

    private static BuilderJobProviderIdentity Identity(ServiceProvider<IBuilderRuntimeService> provider)
        => new(provider.PackageId, provider.PackageVersion, provider.ProviderId, provider.ContractId, provider.ContractVersion,
            provider.DescriptorSha256, provider.AbiSha256, provider.ActivationId, provider.ActivationEpoch, provider.SessionGeneration);

    private async Task<ServiceProvider<IBuilderRuntimeService>> ResolveAsync(CancellationToken token)
    {
        var catalog = await runtimes.ListAsync(token).ConfigureAwait(false);
        var matches = catalog.Providers.Where(provider => provider.State == ServiceProviderState.Active
            && provider.PackageId == BuilderViewModel.BuilderPackageId).Take(2).ToArray();
        if (matches.Length != 1)
            throw new SunderRpcException(new(SunderRpcErrorKind.Unavailable,
                matches.Length == 0 ? "rpc.provider-not-found" : "rpc.provider-ambiguous",
                "One exact local Builder worker provider is required."));
        return matches[0];
    }

    private async Task<ServiceProvider<IBuilderRuntimeService>> RebindOriginalExecutionAsync(
        BuilderJobProviderIdentity original, string backendId, PackageWorkerExecutionReference reference, CancellationToken token)
    {
        var fresh = await ResolveAsync(token).ConfigureAwait(false);
        if (!original.HasSameExecutionCandidate(Identity(fresh))) throw ExecutionChanged();
        var currentBackend = await fresh.Service.GetBackendIdAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (currentBackend != backendId) throw ExecutionChanged();
        var currentReference = await (observations ?? throw new NotSupportedException("Host execution observations are unavailable."))
            .CaptureAsync(fresh, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (currentReference != reference) throw ExecutionChanged();
        return fresh;
    }

    private static SunderRpcException ExecutionChanged()
        => new(new(SunderRpcErrorKind.StaleEndpoint, "agent.builder.execution-changed",
            "The current Builder binding does not prove access to the original job execution."));

    private sealed class CapturedExecution(
        ServiceProvider<IBuilderRuntimeService> captured,
        string backendId,
        PackageWorkerExecutionReference reference,
        IPackageWorkerExecutionObservations observations,
        BuilderBackendGateway gateway) : IBuilderJobExecution
    {
        private readonly ServiceProvider<IBuilderRuntimeService> _start = captured;
        private ServiceProvider<IBuilderRuntimeService> _current = captured;
        private readonly SemaphoreSlim _rebind = new(1, 1);
        private BuilderJobRequest? _attempted;
        private Exception? _localNoDispatch;
        private Exception? _trustedStartRejection;
        public string BackendId { get; } = backendId;
        public BuilderJobProviderIdentity ProviderIdentity { get; } = Identity(captured);
        public PackageWorkerExecutionReference Reference { get; } = reference;

        public async Task<string> StartJobAsync(BuilderJobRequest request, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(request.Id) || request.BackendId != BackendId)
                throw new InvalidDataException("The Builder request does not belong to its captured execution.");
            if (Interlocked.CompareExchange(ref _attempted, request, null) is not null)
                throw new InvalidOperationException("A captured Builder execution permits only one Start attempt. Reconcile the original job instead.");
            string json;
            IBuilderRuntimeService service;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                json = BuilderContent.Serialize(request);
                service = _start.Service;
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception error) { _localNoDispatch = error; throw; }
            try { return await service.StartJobAsync(json, cancellationToken).ConfigureAwait(false); }
            catch (Exception error)
            {
                // Only the exact generated Start call can supply this proof.
                // Get/Cancel/content operations must not release Start admission.
                if (SunderRpcException.HasTrustedNoDispatchEvidence(error)) _trustedStartRejection = error;
                throw;
            }
        }

        public async Task<BuilderJobSnapshot> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
        {
            RequireJob(jobId);
            return await ReadOrStopAsync(service => BuilderContent.ReadAsync<BuilderJobSnapshot>(
                service.GetJobAsync(jobId, BackendId, cancellationToken), cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);
        }

        public async Task CancelJobAsync(string jobId, CancellationToken cancellationToken = default)
        {
            RequireJob(jobId);
            await ReadOrStopAsync(async service =>
            {
                await service.CancelJobAsync(jobId, BackendId, cancellationToken).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        private async Task<T> ReadOrStopAsync<T>(Func<IBuilderRuntimeService, Task<T>> operation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var binding = Volatile.Read(ref _current);
            try { return await operation(binding.Service).ConfigureAwait(false); }
            catch (SunderRpcException error) when (!token.IsCancellationRequested && CanRefreshReadStop(binding, error))
            {
                await _rebind.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (ReferenceEquals(binding, _current))
                    {
                        // A new receiver capability, never a revived reference or
                        // successor Worker. All original fences must match before
                        // a read or idempotent stop can reach the fresh binding.
                        var fresh = await gateway.RebindOriginalExecutionAsync(ProviderIdentity, BackendId, Reference, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        Volatile.Write(ref _current, fresh);
                    }
                    binding = _current;
                }
                finally { _rebind.Release(); }
                token.ThrowIfCancellationRequested();
                // One attempt only. Existing application/reconciliation budgets
                // own further reads. Start never enters this path.
                return await operation(binding.Service).ConfigureAwait(false);
            }
        }

        private static bool CanRefreshReadStop(ServiceProvider<IBuilderRuntimeService> binding, SunderRpcException error)
        {
            if (error.Error is { Kind: SunderRpcErrorKind.Unavailable, Code: "rpc.app-session.retired" }
                or { Kind: SunderRpcErrorKind.StaleEndpoint, Code: "rpc.caller-retired" or "rpc.stale-endpoint" or "rpc.endpoint.stale" })
                return true;
            // A finite busy/unavailable request does not retire a live binding.
            // Catalog discontinuity may do so permanently: only a fresh, fully
            // proved original-execution capability can then continue read/stop.
            // Caller/domain/security cancellation and an ended receiver are never
            // normalized into reconnect, even when the old token is cancelled.
            return error.Error.Kind is SunderRpcErrorKind.Unavailable or SunderRpcErrorKind.ResourceExhausted or SunderRpcErrorKind.StaleEndpoint
                && error.Error.Code is not ("rpc.worker-execution.unsupported" or "rpc.binding.lifetime-disposed")
                && binding.RetirementToken.IsCancellationRequested;
        }

        public Task<PackageWorkerExecutionObservation> ObserveCleanupAsync(CancellationToken cancellationToken = default)
            // Observe through the current authorized SDK service, not the old provider or a Host-id-selected route.
            => observations.ObserveAsync(Reference, cancellationToken);

        public bool IsKnownNotStarted(Exception error)
            => ReferenceEquals(_localNoDispatch, error)
                || ReferenceEquals(_trustedStartRejection, error) && SunderRpcException.HasTrustedNoDispatchEvidence(error)
                || BuilderStartFailureAuthority.IsReviewedPreAcceptance(error);

        private void RequireJob(string jobId)
        {
            if (Volatile.Read(ref _attempted)?.Id != jobId)
                throw new InvalidOperationException("This Builder execution owns a different job identity.");
        }
    }
}
