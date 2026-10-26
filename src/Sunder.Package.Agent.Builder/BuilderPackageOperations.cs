using System.Collections.Concurrent;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Agent.Builder;

/// <summary>Activation-owned package operations. Presentation and generated services borrow the same jobs and development owners.</summary>
[SunderPackageCodeRole(SunderPackageCodeRole.App)]
public sealed partial class BuilderPackageOperations(BuilderProjectApplicationService application, IPackageDevelopmentService development) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, BuilderDevelopmentSession> _sessions = new(BuilderPathService.PathComparer);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(BuilderPathService.PathComparer);
    private readonly ConcurrentDictionary<string, string> _logPackages = new(BuilderPathService.PathComparer);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private Task? _disposal;
    internal event Action<BuilderDevelopmentSession>? Changed;
    internal IReadOnlyList<BuilderDevelopmentSession> Sessions => _sessions.Values.ToArray();
    internal void Stop() => _lifetime.Cancel();
    internal BuilderDevelopmentSession? GetSession(string? entryPoint)
        => entryPoint is null ? null : _sessions.GetValueOrDefault(Path.GetFullPath(entryPoint));

    public async Task<BuilderJobSnapshot> RunJobAsync(BuilderJobRequest request, Func<BuilderJobSnapshot, Task>? changed = null,
        CancellationToken cancellationToken = default, string? projectId = null)
    {
        using var operation = Begin(cancellationToken);
        var gate = request.EntryPoint is null ? null : Gate(request.EntryPoint);
        var entered = false;
        try
        {
            if (gate is not null) { await gate.WaitAsync(operation.Token).ConfigureAwait(false); entered = true; }
            if (GetSession(request.EntryPoint) is { } session) RequireMutable(session);
            return await application.RunJobAsync(request, changed, operation.Token, projectId).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (error.CancellationToken == operation.Token && operation.IsCancellationRequested)
        { throw PublicCancellation(error, cancellationToken); }
        finally { if (entered) gate!.Release(); }
    }

    internal async Task<BuilderDevelopmentSession> LoadAsync(BuilderProjectInspection project, string folder, CancellationToken token)
    {
        using var operation = Begin(token);
        var gate = Gate(project.EntryPoint);
        var entered = false;
        try
        {
            await gate.WaitAsync(operation.Token).ConfigureAwait(false); entered = true;
            if (GetSession(project.EntryPoint) is { } current) { RequireMutable(current); return current; }
            var session = new BuilderDevelopmentSession(project, application, development, _lifetime.Token, Notify);
            _sessions[project.EntryPoint] = session;
            try
            {
                await session.LoadAsync(folder, operation.Token).ConfigureAwait(false);
                _logPackages[project.EntryPoint] = project.PackageId;
                return session;
            }
            finally { if (!session.HasHandle) await ReleaseCoreAsync(session).ConfigureAwait(false); }
        }
        catch (OperationCanceledException error) when (error.CancellationToken == operation.Token && operation.IsCancellationRequested)
        { throw PublicCancellation(error, token); }
        finally { if (entered) gate.Release(); }
    }

    internal async Task ReleaseAsync(BuilderDevelopmentSession session)
    {
        var gate = Gate(session.SourceEntryPoint);
        await gate.WaitAsync().ConfigureAwait(false);
        try { await ReleaseCoreAsync(session).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<PackageDevelopmentRevision> HotreloadAsync(BuilderProjectInspection project,
        Func<BuilderJobSnapshot, Task>? changed = null, CancellationToken cancellationToken = default, string? projectId = null)
    {
        using var operation = Begin(cancellationToken);
        var gate = Gate(project.EntryPoint);
        var entered = false;
        try
        {
            await gate.WaitAsync(operation.Token).ConfigureAwait(false); entered = true;
            var session = GetSession(project.EntryPoint) ?? throw new InvalidOperationException("Load this development package before using Hotreload Package.");
            if (session.ReleaseStarted || session.SourcePackageId != project.PackageId)
                throw new InvalidOperationException("The original development owner is stale. Reconcile Unload before loading a different package identity.");
            if (session.PendingHotreload is null)
            {
                var built = await application.RunJobAsync(new() { Operation = "build", Kind = project.Kind,
                    EntryPoint = project.EntryPoint, ProjectFolder = project.ProjectFolder, PackageId = project.PackageId,
                    DisplayName = project.DisplayName, Expected = project }, changed, operation.Token, projectId).ConfigureAwait(false);
                if (built.State != "succeeded" || built.Project?.PackageId != session.SourcePackageId
                    || string.IsNullOrWhiteSpace(built.DevelopmentFolder) || !IsIdentity(built.DevelopmentContentIdentity))
                    throw new InvalidOperationException(built.Error ?? "Hotreload requires a successful finite build of this exact package and its validated content identity.");
                session.PendingHotreload = built;
                Notify(session);
            }
            return await ApplyPendingHotreloadAsync(session, operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (error.CancellationToken == operation.Token && operation.IsCancellationRequested)
        { throw PublicCancellation(error, cancellationToken); }
        finally { if (entered) gate.Release(); }
    }

    /// <summary>Applies Shell-built output through the existing owner. No job, restore, compiler or second owner is started.</summary>
    public async Task<PackageDevelopmentRevision> ReloadBuiltOutputAsync(BuilderProjectInspection project, string operationId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(operationId, "N", out _)) throw new ArgumentException("A correlated reload operation identity is required.", nameof(operationId));
        using var operation = Begin(cancellationToken);
        var gate = Gate(project.EntryPoint);
        var entered = false;
        try
        {
            await gate.WaitAsync(operation.Token).ConfigureAwait(false); entered = true;
            var session = GetSession(project.EntryPoint) ?? throw new InvalidOperationException("Load this development package in Maker before reloading it.");
            if (session.ReleaseStarted || session.SourcePackageId != project.PackageId || session.DevelopmentFolder is null)
                throw new InvalidOperationException("The original development owner is unavailable. Reconcile Unload before another mutation.");
            if (session.PendingHotreload is null)
            {
                if (application.HasUnconfirmedJob(project.ProjectFolder))
                    throw new InvalidOperationException("Resolve the existing project job before reloading Shell-built output.");
                var identity = await BuilderProjectDriverOutputs.ValidateDevelopmentAsync(session.DevelopmentFolder, project,
                    BuilderToolchainPlatform.Current.DotnetRid, requireApp: false, operation.Token).ConfigureAwait(false);
                session.PendingHotreload = new() { Id = operationId, Operation = "reload", State = "succeeded", Project = project,
                    Message = "Validated already-built output; no build was run.", Output = string.Empty,
                    DevelopmentFolder = session.DevelopmentFolder, DevelopmentContentIdentity = identity };
                Notify(session);
            }
            return await ApplyPendingHotreloadAsync(session, operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (error.CancellationToken == operation.Token && operation.IsCancellationRequested)
        { throw PublicCancellation(error, cancellationToken); }
        finally { if (entered) gate.Release(); }
    }

    internal Task<PackageDevelopmentLogs> GetLogsAsync(BuilderProjectInspection project, PackageDevelopmentLogQuery query, CancellationToken token)
    {
        if (!_logPackages.TryGetValue(project.EntryPoint, out var packageId) || packageId != project.PackageId)
            throw new Sunder.Sdk.Rpc.SunderRpcException(new(Sunder.Sdk.Rpc.SunderRpcErrorKind.Domain, "maker.development.unavailable",
                "Load this development package in Maker once before reading its retained diagnostics."));
        return development.GetLogsAsync(packageId, query, token);
    }

    private async Task<PackageDevelopmentRevision> ApplyPendingHotreloadAsync(BuilderDevelopmentSession session, CancellationToken token)
    {
        var output = session.PendingHotreload!;
        SetReloadProgress(session, output.Id, "running", "reloading");
        try
        {
            // Explicit resolution retains the same operation and exact bytes.
            session.ReloadCommittedRevision ??= await session.Handle.ReloadAsync(output.Id, output.DevelopmentContentIdentity!, token).ConfigureAwait(false);
            var committed = session.ReloadCommittedRevision;
            if (committed.SourceContentIdentity != output.DevelopmentContentIdentity)
                throw new InvalidDataException("The reload acknowledgement did not identify the built output.");
            SetReloadProgress(session, output.Id, "running", "waiting-for-app");
            using var observation = CancellationTokenSource.CreateLinkedTokenSource(token);
            observation.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                while (true)
                {
                    var status = await session.Handle.GetStatusAsync(observation.Token).ConfigureAwait(false);
                    if (IsCurrentlyApplied(status, committed.SourceContentIdentity) && status.AppliedRevision is { } applied && applied.RuntimeInstanceId == committed.RuntimeInstanceId
                        && applied.SessionGeneration >= committed.SessionGeneration && applied.SourceContentIdentity == committed.SourceContentIdentity)
                    {
                        session.PendingHotreload = null;
                        session.ReloadCommittedRevision = null;
                        SetReloadProgress(session, output.Id, "succeeded", "applied");
                        return applied;
                    }
                    if (status.RuntimeRevision is { } actual && actual.RuntimeInstanceId != committed.RuntimeInstanceId)
                        throw new InvalidOperationException("The Runtime was replaced before this exact reload could be observed. Do not replay its mutation.");
                    if (status.RuntimeRevision is { } failed && failed.RuntimeInstanceId == committed.RuntimeInstanceId
                        && failed.SessionGeneration >= committed.SessionGeneration && failed.SourceContentIdentity == committed.SourceContentIdentity
                        && (status.RuntimeReadiness == PackageDevelopmentRuntimeReadiness.Failed || status.AppReadiness == PackageDevelopmentAppReadiness.Quarantined))
                    {
                        session.PendingHotreload = null;
                        session.ReloadCommittedRevision = null;
                        SetReloadProgress(session, output.Id, "failed", "activation-failed", status.ErrorMessage ?? "The selected Runtime/App activation failed.");
                        throw new InvalidOperationException(session.ReloadProgress!.Error);
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(100), observation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException error) when (error.CancellationToken == observation.Token && observation.IsCancellationRequested)
            {
                if (token.IsCancellationRequested) throw new OperationCanceledException(error.Message, error, token);
                throw new TimeoutException("Reload committed, but the exact Runtime/App revision is not yet applied. Observe this operation before another mutation.", error);
            }
        }
        catch (Exception error)
        {
            if (session.ReloadProgress?.State != "failed")
                SetReloadProgress(session, output.Id, "unknown", session.ReloadProgress?.Phase ?? "reloading", BuilderJobText.Safe(error.Message, 512));
            throw;
        }
    }

    private void SetReloadProgress(BuilderDevelopmentSession session, string id, string state, string phase, string? error = null)
    {
        session.ReloadProgress = new(id, state, phase, error);
        Notify(session);
    }

    private static bool IsIdentity(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private SemaphoreSlim Gate(string entryPoint) => _gates.GetOrAdd(Path.GetFullPath(entryPoint), _ => new(1, 1));
    private static void RequireMutable(BuilderDevelopmentSession session)
    {
        if (session.ReleaseStarted) throw new InvalidOperationException("This development owner is awaiting release. Reconnect and retry Unload.");
        if (session.PendingHotreload is not null) throw new InvalidOperationException("Resolve the exact pending Hotreload or Unload before starting another project mutation.");
    }
    private CancellationTokenSource Begin(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
        return CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
    }
    private static Exception PublicCancellation(OperationCanceledException error, CancellationToken caller)
        => caller.IsCancellationRequested ? new OperationCanceledException(error.Message, error, caller)
            : new ObjectDisposedException(nameof(BuilderPackageOperations), "The owning Builder activation stopped.");
    private void Notify(BuilderDevelopmentSession session) => Changed?.Invoke(session);
    private async Task ReleaseCoreAsync(BuilderDevelopmentSession session)
    {
        try { await session.ReleaseAsync().ConfigureAwait(false); }
        finally { if (session.Released) _sessions.TryRemove(new KeyValuePair<string, BuilderDevelopmentSession>(session.SourceEntryPoint, session)); }
    }
    public ValueTask DisposeAsync()
    {
        lock (_sync) return new(_disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        Stop();
        List<Exception> failures = [];
        foreach (var session in Sessions)
        {
            try { await ReleaseAsync(session).ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count != 0) throw new AggregateException("Builder development owners could not all be released.", failures);
    }
}
