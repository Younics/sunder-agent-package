namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderProjectDriver
{
    private async Task WatchDotnetAsync(BuilderProjectInspection project, IBuilderProjectDriverSelection selection,
        IBuilderJobReporter reporter, CancellationToken token)
    {
        // One job owns the subscription, one finite child at a time and the SDK lease.
        // MSBuild owns both candidate composition and last-good output replacement.
        var staging = Path.Combine(project.ProjectFolder, "obj", ".sunder-builder-watch-" + Guid.NewGuid().ToString("N"));
        BuilderProjectSource.EnsureSafePath(project.ProjectFolder, staging);
        Directory.CreateDirectory(staging);
        try
        {
            var revisionFile = Path.Combine(staging, "revision");
            using var changes = new BuilderSourceChangeMonitor(project.ProjectFolder,
                [options.CacheRootPath, selection.Installation.RootPath], revisionFile);
            var watchProperties = new[] { "-p:SunderPackageDevelopmentStagingRoot=" + EscapeProperty(staging),
                "-p:SunderPackageDevelopmentRevisionFile=" + EscapeProperty(revisionFile) };
            // Fail before compiling if a mismatched Build package cannot defer publication.
            // Restore also imports the Build targets for a genuinely cold local folder.
            var restoredRevision = changes.Revision;
            await RunAsync(selection, ["msbuild", project.EntryPoint, "-restore", "-t:_ValidateSunderDevelopmentWatch", "--disable-build-servers",
                "-p:Configuration=Debug", "-p:RestoreDisableParallel=true", .. BuildProperties(project, "Debug"), .. watchProperties], project.ProjectFolder, reporter, token).ConfigureAwait(false);
            var firstAttempt = true;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var inputs = await DiscoverDotnetInputsAsync(project, selection, token).ConfigureAwait(false);
                changes.ObserveInputs(inputs.Roots, inputs.Files);
                var revision = changes.Revision;
                var restore = !firstAttempt || revision != restoredRevision;
                firstAttempt = false;
                reporter.Update(new() { LastSourceChangeAtUtc = changes.LastChangeAtUtc });
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                var changed = changes.WaitForRevisionAsync(revision, attempt.Token);
                var expandedInputs = false;
                var building = BuildAndPublishAsync();
                try
                {
                    if (await Task.WhenAny(building, changed).ConfigureAwait(false) == changed)
                    {
                        // Invalidate publication before cancelling. Drain this exact child
                        // before a successor can write or release the SDK/project leases.
                        try
                        {
                            await changed.ConfigureAwait(false);
                            reporter.Report($"Source changed during build; discarding revision {revision}: {changes.LastChangePath}\n");
                        }
                        finally { await attempt.CancelAsync().ConfigureAwait(false); }
                    }
                    await building.ConfigureAwait(false);
                }
                catch (OperationCanceledException error) when (attempt.IsCancellationRequested && error.CancellationToken == attempt.Token)
                {
                    if (token.IsCancellationRequested) throw new OperationCanceledException(error.Message, error, token);
                    // Source supersession is not user cancellation or a terminal job outcome.
                    await changed.ConfigureAwait(false); // watcher faults must not be hidden
                }
                catch (BuilderCommandFailedException error) when (!token.IsCancellationRequested)
                {
                    changes.ReportIfCurrent(revision, () => reporter.Update(new() { Phase = BuilderJobPhase.BuildFailed,
                        Message = error.Message + " Watching for the next source edit; the last good package is retained." }));
                }
                finally
                {
                    await attempt.CancelAsync().ConfigureAwait(false);
                    // Observe both operations even if the watcher failed while the child drained.
                    try { await building.ConfigureAwait(false); }
                    catch (OperationCanceledException error) when (attempt.IsCancellationRequested && error.CancellationToken == attempt.Token) { }
                    catch (BuilderCommandFailedException) { }
                    try { await changed.ConfigureAwait(false); }
                    catch (OperationCanceledException error) when (attempt.IsCancellationRequested && error.CancellationToken == attempt.Token) { }
                }
                token.ThrowIfCancellationRequested();
                if (changes.Revision == revision && expandedInputs) continue;
                await changes.WaitForChangeAsync(revision, token).ConfigureAwait(false);

                async Task BuildAndPublishAsync()
                {
                    var candidate = await BuildAsync(project, selection, reporter, requireApp: false, attempt.Token, restore: restore, watchStagingRoot: staging).ConfigureAwait(false);
                    var expectedIndex = await ReadWatchIndexAsync(project, Path.Combine(candidate, "manifest", "content-index.json"), attempt.Token).ConfigureAwait(false);
                    var after = await DiscoverDotnetInputsAsync(project, selection, attempt.Token).ConfigureAwait(false);
                    // Cold evaluation may expose linked inputs that were not observed yet.
                    // Rebuild under the expanded subscription without publishing this attempt.
                    expandedInputs = changes.ObserveInputs(after.Roots, after.Files);
                    if (expandedInputs || changes.Revision != revision) return;
                    await RunAsync(selection, ["msbuild", project.EntryPoint, "-t:_PublishSunderDevelopmentWatch", "--disable-build-servers",
                        "-p:Configuration=Debug", .. BuildProperties(project, "Debug"), .. watchProperties,
                        "-p:SunderPackageDevelopmentRevision=" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                        project.ProjectFolder, reporter, attempt.Token, recoverableBuild: true).ConfigureAwait(false);
                    var development = BuilderProjectDriverOutputs.DevelopmentFolder(project, "Debug");
                    var indexPath = Path.Combine(development, "manifest", "content-index.json");
                    var publishedIndex = await ReadWatchIndexAsync(project, indexPath, attempt.Token).ConfigureAwait(false);
                    if (!expectedIndex.AsSpan().SequenceEqual(publishedIndex))
                        throw new InvalidDataException("The published development content identity differs from this build's complete candidate.");
                    await BuilderProjectDriverOutputs.ValidateDevelopmentAsync(development, project, rid, requireApp: false, attempt.Token).ConfigureAwait(false);
                    var validatedIndex = await ReadWatchIndexAsync(project, indexPath, attempt.Token).ConfigureAwait(false);
                    if (!expectedIndex.AsSpan().SequenceEqual(validatedIndex))
                        throw new InvalidDataException("The published development content identity changed during validation.");
                    var inspected = await discovery.InspectAsync(project.EntryPoint, attempt.Token).ConfigureAwait(false);
                    changes.ReportIfCurrent(revision, () =>
                    {
                        attempt.Token.ThrowIfCancellationRequested();
                        reporter.Update(new() { Phase = BuilderJobPhase.BuildReady, LastSuccessfulBuildAtUtc = DateTimeOffset.UtcNow,
                            Project = inspected, DevelopmentFolder = development });
                        reporter.Update(new() { Phase = BuilderJobPhase.Watching, LastSourceChangeAtUtc = changes.LastChangeAtUtc,
                            Message = "Watching source changes. Keep Builder connected; stop watch before other project mutations." });
                    });
                }
            }
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }

    private static async Task<byte[]> ReadWatchIndexAsync(BuilderProjectInspection project, string path, CancellationToken token)
    {
        BuilderProjectSource.EnsureSafePath(project.ProjectFolder, path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (file.Length is < 1 or > 1024 * 1024) throw new InvalidDataException("The development content index exceeds its size bound.");
        var bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (file.ReadByte() != -1) throw new InvalidDataException("The development content index changed while being read.");
        return bytes;
    }
}
