namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderProjectDriver
{
    private async Task WatchDotnetAsync(BuilderProjectInspection project, IBuilderProjectDriverSelection selection,
        IBuilderJobReporter reporter, CancellationToken token)
    {
        // One job owns the source subscription, finite compiler children and SDK lease. There is
        // no dotnet-watch initial build and no reusable build server outside that lifetime.
        using var changes = new BuilderSourceChangeMonitor(project.ProjectFolder, [options.CacheRootPath, selection.Installation.RootPath]);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var revision = changes.Revision;
            reporter.Update(new() { LastSourceChangeAtUtc = changes.LastChangeAtUtc });
            var inputs = await DiscoverDotnetInputsAsync(project, selection, token).ConfigureAwait(false);
            changes.ObserveInputs(inputs.Roots, inputs.Files);
            var development = await BuildAsync(project, selection, reporter, requireApp: false, token).ConfigureAwait(false);
            var after = await DiscoverDotnetInputsAsync(project, selection, token).ConfigureAwait(false);
            // Cold evaluation can discover linked inputs which were not subscribed during
            // compilation. Close that first-build window with one build under the new graph;
            // generated restore/compiler products are not notification inputs.
            if (changes.ObserveInputs(after.Roots, after.Files)) continue;
            if (changes.Revision != revision)
            {
                // This build already has a queued successor. Do not briefly
                // advertise idle before joining the same debounced source work.
                await changes.WaitForChangeAsync(revision, token).ConfigureAwait(false);
                continue;
            }
            reporter.Update(new() { Phase = BuilderJobPhase.Watching, LastSourceChangeAtUtc = changes.LastChangeAtUtc,
                DevelopmentFolder = development, Message = "Watching source changes. Keep Builder connected; stop watch before other project mutations." });
            // A failed build ends this job. App recovery already retains the last good session
            // and waits for a source edit before retrying; cancellation always drains the process.
            await changes.WaitForChangeAsync(revision, token).ConfigureAwait(false);
        }
    }
}
