using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Agent.Builder;

[SunderPackageCodeRole(SunderPackageCodeRole.App)]
internal sealed record BuilderApplySelection(BuilderProjectInspection Project, BuilderDevelopmentSession? Owner, string Folder, string Identity)
{
    internal BuilderDevelopmentSession? AppliedOwner { get; set; } = Owner;
    internal bool InitialLoadAttempted { get; set; }
}

public sealed partial class BuilderPackageOperations
{
    internal static IReadOnlyList<string> BuiltOutputs(BuilderProjectInspection project)
        => new[] { BuilderProjectDriverOutputs.DevelopmentFolder(project, "Debug"), BuilderProjectDriverOutputs.DevelopmentFolder(project, "Release") }
            .Distinct(BuilderPathService.PathComparer).Where(folder => File.Exists(Path.Combine(folder, "manifest", "sunder-package.json"))).ToArray();

    internal async Task<BuilderApplySelection> SelectApplyAsync(BuilderProjectInspection project, string? folder, CancellationToken token)
    {
        using var operation = Begin(token);
        var gate = Gate(project.EntryPoint);
        if (!await gate.WaitAsync(0, operation.Token).ConfigureAwait(false))
            throw new InvalidOperationException("A project operation is still running. Observe it before selecting another apply; no mutation was admitted.");
        try
        {
            var owner = GetSession(project.EntryPoint);
            if (owner is not null)
            {
                RequireMutable(owner);
                if (owner.SourcePackageId != project.PackageId || folder is not null && !BuilderPathService.PathComparer.Equals(folder, owner.DevelopmentFolder))
                    throw new InvalidOperationException("The existing Maker owner selects different output/identity. Unload it explicitly before changing that selection.");
                folder = owner.DevelopmentFolder;
            }
            if (application.HasUnconfirmedJob(project.ProjectFolder)) throw new InvalidOperationException("Resolve the existing project job before applying built output.");
            var candidates = folder is null ? BuiltOutputs(project) : [folder];
            if (candidates.Count == 0) throw new InvalidDataException("build_required: Build this project with Shell before applying it.");
            if (candidates.Count != 1) throw new InvalidDataException("output_required: Query outputs and select one; multiple built outputs exist.");
            folder = candidates[0];
            var identity = await BuilderProjectDriverOutputs.ValidateDevelopmentAsync(folder, project, BuilderToolchainPlatform.Current.DotnetRid, false, operation.Token).ConfigureAwait(false);
            return new(project, owner, folder, identity);
        }
        finally { gate.Release(); }
    }

    internal async Task<(PackageDevelopmentRevision Revision, string Effect)> ApplyBuiltAsync(BuilderApplySelection selected, string id, CancellationToken token)
    {
        using var operation = Begin(token);
        var gate = Gate(selected.Project.EntryPoint);
        await gate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            var owner = GetSession(selected.Project.EntryPoint);
            if (!ReferenceEquals(owner, selected.Owner)) throw new InvalidOperationException("The selected development owner changed before admission could execute. No replacement owner was acquired.");
            var identity = await BuilderProjectDriverOutputs.ValidateDevelopmentAsync(selected.Folder, selected.Project, BuilderToolchainPlatform.Current.DotnetRid, false, operation.Token).ConfigureAwait(false);
            if (identity != selected.Identity) throw new InvalidDataException("Built output changed after selection; no different revision was applied.");
            var effect = owner is null ? "loaded" : "reloaded";
            if (owner is null)
            {
                owner = new(selected.Project, application, development, _lifetime.Token, Notify);
                selected.AppliedOwner = owner;
                _sessions[selected.Project.EntryPoint] = owner;
                try
                {
                    selected.InitialLoadAttempted = true;
                    await owner.LoadAsync(selected.Folder, operation.Token).ConfigureAwait(false);
                    _logPackages[selected.Project.EntryPoint] = selected.Project.PackageId;
                }
                finally { if (!owner.HasHandle) await ReleaseCoreAsync(owner).ConfigureAwait(false); }
            }
            else
            {
                RequireMutable(owner);
                var status = await owner.Handle.GetStatusAsync(operation.Token).ConfigureAwait(false);
                if (IsCurrentlyApplied(status, identity)) return (status.AppliedRevision!, "unchanged");
                owner.PendingHotreload = new() { Id = id, Operation = "reload", State = "succeeded", Project = selected.Project,
                    Message = "Validated already-built output.", Output = string.Empty, DevelopmentFolder = selected.Folder, DevelopmentContentIdentity = identity };
                Notify(owner);
                return (await ApplyPendingHotreloadAsync(owner, operation.Token).ConfigureAwait(false), effect);
            }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
            wait.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                while (true)
                {
                    var status = await owner.Handle.GetStatusAsync(wait.Token).ConfigureAwait(false);
                    if (IsCurrentlyApplied(status, identity)) return (status.AppliedRevision!, effect);
                    if (status.RuntimeReadiness == PackageDevelopmentRuntimeReadiness.Failed || status.AppReadiness == PackageDevelopmentAppReadiness.Quarantined)
                        throw new InvalidOperationException(status.ErrorMessage ?? "The selected package activation failed.");
                    await Task.Delay(100, wait.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!operation.IsCancellationRequested) { throw new TimeoutException("Output was loaded but its applied Runtime/App revision is not confirmed. Observe the original operation."); }
        }
        finally { gate.Release(); }
    }

    internal async Task<PackageDevelopmentRevision?> ObserveApplyAsync(BuilderApplySelection selection, string operationId, CancellationToken token)
    {
        using var operation = Begin(token);
        var gate = Gate(selection.Project.EntryPoint);
        await gate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            var owner = GetSession(selection.Project.EntryPoint);
            if (owner is null || owner.ReleaseStarted || !ReferenceEquals(owner, selection.AppliedOwner)
                || !BuilderPathService.PathComparer.Equals(owner.DevelopmentFolder, selection.Folder)) return null;
            var status = await owner.Handle.GetStatusAsync(operation.Token).ConfigureAwait(false);
            if (!IsCurrentlyApplied(status, selection.Identity) || status.AppliedRevision is not { } revision) return null;
            if (owner.ReloadCommittedRevision is { } committed && (revision.RuntimeInstanceId != committed.RuntimeInstanceId || revision.SessionGeneration < committed.SessionGeneration)) return null;
            if (owner.PendingHotreload?.Id == operationId) { owner.PendingHotreload = null; owner.ReloadCommittedRevision = null; Notify(owner); }
            return revision;
        }
        finally { gate.Release(); }
    }

    private static bool IsCurrentlyApplied(PackageDevelopmentStatus status, string identity)
        => status.IsLoaded && status.HostState == PackageDevelopmentHostState.Ready
            && status.RuntimeReadiness == PackageDevelopmentRuntimeReadiness.Ready
            && status.AppReadiness is PackageDevelopmentAppReadiness.Activated or PackageDevelopmentAppReadiness.NotApplicable
            && status.AppliedRevision?.SourceContentIdentity == identity;

    internal Task<IReadOnlyList<PackageDevelopmentView>> GetViewsAsync(BuilderProjectInspection project, CancellationToken token)
        => WithViewSessionAsync(project, (session, operation) => session.GetViewsAsync(operation), token);
    internal Task<PackageDevelopmentViewPage> QueryViewAsync(BuilderProjectInspection project, string view, PackageDevelopmentViewQuery query, CancellationToken token, string? owner = null)
        => WithViewSessionAsync(project, (session, operation) => session.QueryViewAsync(view, query, operation), token, owner);
    internal Task<Sunder.Sdk.Services.SunderContent> CaptureViewAsync(BuilderProjectInspection project, string view, PackageDevelopmentViewQuery scope, int edge, CancellationToken token, string owner)
        => WithViewSessionAsync(project, (session, operation) => session.CaptureViewAsync(view, scope, edge, operation), token, owner);
    internal async Task OpenViewAsync(BuilderProjectInspection project, string view, CancellationToken token, string owner)
        => await WithViewSessionAsync(project, async (session, operation) => { await session.OpenViewAsync(view, operation).ConfigureAwait(false); return true; }, token, owner).ConfigureAwait(false);
    internal Task<PackageDevelopmentViewInputResult> InputViewAsync(BuilderProjectInspection project, PackageDevelopmentViewInput input, string owner, CancellationToken token)
        => WithViewSessionAsync(project, (session, operation) => session.InteractViewAsync(input, operation), token, owner);
}
