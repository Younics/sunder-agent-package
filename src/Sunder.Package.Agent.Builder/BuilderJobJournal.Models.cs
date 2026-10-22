using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Builder;

public interface IBuilderJobJournal
{
    Task<BuilderJobSnapshot> RunOwnedAsync(Func<BuilderJobScope, Task<BuilderJobSnapshot>> run,
        CancellationToken cancellationToken = default);
    Task<string> AdmitAndStartAsync(BuilderJobAdmission admission, IBuilderBackend backend,
        Func<CancellationToken, Task<IBuilderJobExecution>> capture, CancellationToken cancellationToken = default, BuilderJobScope? scope = null);
    Task<BuilderJobStopResult> ReconcileAsync(string jobId, IBuilderBackend backend, CancellationToken cancellationToken = default, BuilderJobScope? scope = null);
    Task<BuilderJobStopResult?> ReadCompletedAsync(BuilderJobReservation reservation, CancellationToken cancellationToken = default, BuilderJobScope? scope = null);
    Task<BuilderJobStopResult> AbortKnownNotStartedAsync(BuilderJobAdmission admission, CancellationToken cancellationToken = default, BuilderJobScope? scope = null);
    Task<BuilderJobStopResult> CompleteAsync(BuilderJobReservation reservation, BuilderJobStopResult result, CancellationToken cancellationToken = default, BuilderJobScope? scope = null);
    Task<BuilderJobStopResult> CompleteOwnedAsync(BuilderJobAdmission admission, BuilderJobStopResult result, CancellationToken cancellationToken = default, BuilderJobScope? scope = null);
    Task SavePartialAsync(BuilderJobReservation reservation, BuilderJobSnapshot snapshot, CancellationToken cancellationToken = default, BuilderJobScope? scope = null);
    Task SaveFailureAsync(BuilderJobReservation reservation, string failure, CancellationToken cancellationToken = default, BuilderJobScope? scope = null);
}

/// <summary>One invocation's admission state. No caller may start work outside the journal's held gate.</summary>
public sealed class BuilderJobAdmission(BuilderJobRequest request)
{
    private BuilderJobJournal? _owner;
    public BuilderJobRequest Request { get; internal set; } = request;
    public IBuilderJobExecution? Execution { get; internal set; }
    public BuilderJobReservation? Reservation { get; internal set; }
    public bool PendingWriteAttempted { get; internal set; }
    public bool StartAttempted { get; internal set; }
    public BuilderJobStopResult? StopResult { get; internal set; }
    internal BuilderJobJournal? Owner => Volatile.Read(ref _owner);
    internal bool TryBind(BuilderJobJournal owner) => Interlocked.CompareExchange(ref _owner, owner, null) is null;
    internal bool BoundaryClosed { get; set; }
    internal bool HasPositiveNoStartProof { get; set; }
    internal Exception? Failure { get; set; }
    internal BuilderJournalTerminalReceipt? TerminalReceipt { get; set; }
}

// Ephemeral original-owner evidence, recorded only after exact persisted ownership
// validation and before a terminal completion write can lose acknowledgement.
internal sealed record BuilderJournalTerminalReceipt(BuilderJobReservation Reservation, long BeforeRevision, BuilderJobStopResult Result);

/// <summary>Immutable physical exclusion scope captured before admission; do not re-resolve persisted roots.</summary>
public sealed record BuilderJobExclusionScope([property: JsonRequired] string? PhysicalProjectRoot, [property: JsonRequired] bool SdkGlobal)
{
    internal static BuilderJobExclusionScope Create(BuilderJobRequest request)
    {
        var folder = request.ProjectFolder ?? request.Expected?.ProjectFolder
            ?? (request.EntryPoint is { } entry ? Path.GetDirectoryName(entry) : null);
        if (folder is null) return new(null, true); // Missing project scope is conservative, not an exemption.
        if (!Path.IsPathFullyQualified(folder)) throw new InvalidDataException("Builder admission requires an absolute project folder.");
        return new(Path.TrimEndingDirectorySeparator(new BuilderPathService().CanonicalEntryPoint(folder)),
            request.Operation.EndsWith("-sdk", StringComparison.Ordinal));
    }

    internal bool Overlaps(BuilderJobExclusionScope other)
    {
        // SDK mutation/selection is workspace-global, including unknown cross-toolchain consumers.
        if (SdkGlobal || other.SdkGlobal) return true;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var left = PhysicalProjectRoot!;
        var right = other.PhysicalProjectRoot!;
        return string.Equals(left, right, comparison)
            || left.StartsWith(Path.EndsInDirectorySeparator(right) ? right : right + Path.DirectorySeparatorChar, comparison)
            || right.StartsWith(Path.EndsInDirectorySeparator(left) ? left : left + Path.DirectorySeparatorChar, comparison);
    }
}

public enum BuilderJobJournalPhase { PendingStart = 1 }

/// <summary>PendingStart is conservative: dispatch may have happened even without a Start acknowledgement.</summary>
public sealed record BuilderJobReservation(
    [property: JsonRequired] Guid Generation, [property: JsonRequired] string JobId,
    [property: JsonRequired] string BackendId, [property: JsonRequired] string Operation,
    [property: JsonRequired] BuilderProjectKind Kind, [property: JsonRequired] BuilderJobExclusionScope Scope,
    [property: JsonRequired] string RequestSha256, [property: JsonRequired] BuilderJobProviderIdentity ProviderIdentity,
    [property: JsonRequired] PackageWorkerExecutionReference ExecutionReference,
    [property: JsonRequired] BuilderJobJournalPhase Phase = BuilderJobJournalPhase.PendingStart)
{
    [JsonRequired]
    public bool DispatchMayHaveHappened { get; init; } = true;
    internal static BuilderJobReservation Create(BuilderJobRequest request, IBuilderJobExecution execution)
        => new(Guid.NewGuid(), request.Id!, execution.BackendId, request.Operation, request.Kind,
            BuilderJobExclusionScope.Create(request), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(BuilderContent.Serialize(request)))),
            execution.ProviderIdentity, execution.Reference);
}

internal sealed record BuilderJournalBindingBridge([property: JsonRequired] BuilderJobProviderIdentity ObservedBinding,
    [property: JsonRequired] PackageWorkerExecutionReference ExecutionReference, [property: JsonRequired] string BackendId);
internal sealed record BuilderJournalPending([property: JsonRequired] BuilderJobReservation Reservation, BuilderJobSnapshot? LastSnapshot = null,
    string? LastFailure = null, BuilderJournalBindingBridge? RecoveryBinding = null);
internal sealed record BuilderJournalCompleted([property: JsonRequired] BuilderJobReservation Reservation, [property: JsonRequired] BuilderJobStopOutcome Outcome,
    BuilderJobSnapshot? LastSnapshot, PackageWorkerExecutionObservation? Observation, string? LastFailure = null,
    BuilderJournalBindingBridge? RecoveryBinding = null)
{
    internal BuilderJobStopResult Result() => Outcome switch
    {
        BuilderJobStopOutcome.NotStarted => BuilderJobStopResult.NotStarted(Reservation.JobId, LastFailure),
        BuilderJobStopOutcome.KnownTerminal => BuilderJobStopResult.KnownTerminal(LastSnapshot!, LastFailure),
        BuilderJobStopOutcome.CleanupConfirmedUnknownOutcome => BuilderJobStopResult.ExecutionCleaned(Reservation.JobId, LastSnapshot, Observation!, LastFailure),
        _ => throw new InvalidDataException("Invalid Builder journal completion authority."),
    };
}

internal sealed record BuilderJournalDocument([property: JsonRequired] string Schema, [property: JsonRequired] int Version,
    [property: JsonRequired] long Revision, [property: JsonRequired] BuilderJournalPending[] Pending, [property: JsonRequired] BuilderJournalCompleted[] Completed)
{
    internal static BuilderJournalDocument Empty => new("sunder.builder.admission", 1, 0, [], []);
}

internal interface IBuilderJobJournalStorage
{
    Task<IAsyncDisposable> EnterAsync(CancellationToken token);
    Task<byte[]?> ReadAsync(CancellationToken token);
    Task WriteAsync(byte[] bytes, CancellationToken token);
}

internal static class BuilderStartFailureAuthority
{
    // Reviewed BuilderRuntimeService.StartJobAsync/BuilderJobManager.Start paths:
    // all of these reject before jobs.Add/launching the job driver. job-not-found here is only a
    // retired completed-ID tombstone, not GetJob's missing-result acknowledgement.
    internal static bool IsReviewedPreAcceptance(Exception error)
        => error is SunderRpcException { Error: { Kind: SunderRpcErrorKind.Domain,
            Code: "agent.builder.backend-changed" or "agent.builder.busy" or "agent.builder.invalid-request"
                or "agent.builder.request-too-large" or "agent.builder.job-not-found" } };
}
