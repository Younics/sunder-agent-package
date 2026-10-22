using System.Text.Json.Serialization;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Builder;

/// <summary>One job's exact provider and Host execution receipt, captured before its sole Start attempt.</summary>
/// <remarks>Reads and cancellation retain the original execution. A new receiver binding requires independent exact provider, backend and Host execution proof; it cannot select a successor or replay Start.</remarks>
public interface IBuilderJobExecution
{
    string BackendId { get; }
    BuilderJobProviderIdentity ProviderIdentity { get; }
    PackageWorkerExecutionReference Reference { get; }
    Task<string> StartJobAsync(BuilderJobRequest request, CancellationToken cancellationToken = default);
    Task<BuilderJobSnapshot> GetJobAsync(string jobId, CancellationToken cancellationToken = default);
    Task CancelJobAsync(string jobId, CancellationToken cancellationToken = default);
    Task<PackageWorkerExecutionObservation> ObserveCleanupAsync(CancellationToken cancellationToken = default);
    /// <summary>Positive local/validated pre-dispatch authority, never a guess from an exception kind.</summary>
    bool IsKnownNotStarted(Exception error) => false;
}

/// <summary>The ten public exact-provider identity fields; endpoints remain SDK-owned.</summary>
public sealed record BuilderJobProviderIdentity(
    [property: JsonRequired] string PackageId, [property: JsonRequired] string PackageVersion,
    [property: JsonRequired] string ProviderId, [property: JsonRequired] string ContractId,
    [property: JsonRequired] string ContractVersion, [property: JsonRequired] string DescriptorSha256,
    [property: JsonRequired] string AbiSha256, [property: JsonRequired] Guid ActivationId,
    [property: JsonRequired] long ActivationEpoch, [property: JsonRequired] long SessionGeneration)
{
    /// <summary>Immutable logical/activation fields only, not equivalence of receiver bindings.</summary>
    internal bool HasSameExecutionCandidate(BuilderJobProviderIdentity other)
        => PackageId == other.PackageId && PackageVersion == other.PackageVersion && ProviderId == other.ProviderId
            && ContractId == other.ContractId && ContractVersion == other.ContractVersion
            && DescriptorSha256 == other.DescriptorSha256 && AbiSha256 == other.AbiSha256
            && ActivationId == other.ActivationId && ActivationEpoch == other.ActivationEpoch;
}

/// <summary>Read/stop-only recovery evidence from a fresh SDK binding; it exposes no Start method.</summary>
public sealed record BuilderRecordedJobRecovery(BuilderJobSnapshot? Snapshot, BuilderJobProviderIdentity BindingIdentity,
    PackageWorkerExecutionReference ExecutionReference, string BackendId);

public enum BuilderJobStopOutcome
{
    NotStarted,
    KnownTerminal,
    CleanupConfirmedUnknownOutcome,
}

/// <summary>Positive stop authority; execution cleanup is deliberately not a package-job outcome.</summary>
public sealed class BuilderJobStopResult
{
    private BuilderJobStopResult(string jobId, BuilderJobStopOutcome outcome,
        BuilderJobSnapshot? lastSnapshot = null, PackageWorkerExecutionObservation? executionObservation = null, string? lastFailure = null)
    {
        JobId = jobId;
        Outcome = outcome;
        LastSnapshot = lastSnapshot;
        ExecutionObservation = executionObservation;
        LastFailure = lastFailure is { Length: > 2048 } failure ? failure[..2048] : lastFailure;
    }

    public string JobId { get; }
    public BuilderJobStopOutcome Outcome { get; }
    /// <summary>Retained source metadata/output, which may still describe a running job with an unknown outcome.</summary>
    public BuilderJobSnapshot? LastSnapshot { get; }
    public BuilderJobSnapshot? TerminalSnapshot => Outcome == BuilderJobStopOutcome.KnownTerminal ? LastSnapshot : null;
    public PackageWorkerExecutionObservation? ExecutionObservation { get; }
    public string? LastFailure { get; }

    internal static BuilderJobStopResult NotStarted(string jobId, string? lastFailure = null)
        => new(jobId, BuilderJobStopOutcome.NotStarted, lastFailure: lastFailure);

    internal static BuilderJobStopResult KnownTerminal(BuilderJobSnapshot snapshot, string? lastFailure = null)
    {
        if (snapshot.State is not ("succeeded" or "failed" or "cancelled"))
            throw new InvalidDataException("A running Builder snapshot cannot confirm job shutdown.");
        return new(snapshot.Id, BuilderJobStopOutcome.KnownTerminal, snapshot, lastFailure: lastFailure);
    }

    internal static BuilderJobStopResult ExecutionCleaned(string jobId, BuilderJobSnapshot? last,
        PackageWorkerExecutionObservation observation, string? lastFailure = null)
    {
        if (observation.State != PackageWorkerExecutionState.CleanupConfirmed)
            throw new InvalidDataException("Only exact Host-confirmed execution cleanup can release uncertain Builder ownership.");
        return new(jobId, BuilderJobStopOutcome.CleanupConfirmedUnknownOutcome, last, observation, lastFailure);
    }
}

/// <summary>The old execution is proven clean, but its package-job result could not be recovered.</summary>
public sealed class BuilderJobOutcomeUnknownException(BuilderJobStopResult stopResult, Exception innerException)
    : IOException("Builder confirmed cleanup of the original worker execution, but the job outcome is unknown. Inspect the saved source before retrying.", innerException)
{
    public BuilderJobStopResult StopResult { get; } = stopResult;
}
