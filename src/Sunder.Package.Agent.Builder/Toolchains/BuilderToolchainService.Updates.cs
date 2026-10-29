namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderToolchainService
{
    /// <summary>Replaces previously installed managed tools after a Sunder update. Does not install optional tools for the first time.</summary>
    public async Task UpgradeManagedAsync(BuilderProjectKind kind, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var installed = await ListInstalledAsync(kind, cancellationToken).ConfigureAwait(false);
        if (!installed.Any(item => item.IsManaged && !IsCurrent(item))) return;
        foreach (var installation in installed.Where(item => item.IsManaged && !IsCurrent(item) && item.Version == RequiredVersion(kind)))
        {
            progress?.Report($"Waiting for active {kind} {installation.Version} users before replacing its npm tools.");
            // An npm-only update replaces the same Node directory. Wait for its users
            // before staging that replacement; distinct SDK/Node versions stay side by side until promotion.
            while (true)
            {
                using (var operation = await LockAsync(kind, installation.Version, cancellationToken).ConfigureAwait(false))
                {
                    try { using var mutation = operation.BeginMutation(); break; }
                    catch (BuilderToolchainException error) when (error.Phase == "selection" && error.CanRetry) { }
                }
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
        await EnsureAsync(kind, progress: progress, cancellationToken: cancellationToken).ConfigureAwait(false);
        // Existing builds retain their cross-process use leases. Only this activation-owned
        // update waits; new builds can already use the verified replacement.
        while (!await RemoveSupersededAsync(kind, progress, cancellationToken).ConfigureAwait(false))
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BuilderToolchainInstallation> FinishSelectionAsync(BuilderToolchainInstallation installation,
        IProgress<string>? progress, CancellationToken token)
    {
        await RemoveSupersededAsync(installation.Kind, progress, token).ConfigureAwait(false);
        return installation;
    }

    private async Task<bool> RemoveSupersededAsync(BuilderProjectKind kind, IProgress<string>? progress, CancellationToken token)
    {
        var parent = InstallParent(kind);
        BuilderToolchainFiles.CheckPath(root, parent);
        if (!Directory.Exists(parent)) return true;
        var complete = true;
        foreach (var directory in Directory.EnumerateDirectories(parent).ToArray())
        {
            var version = Path.GetFileName(directory);
            if (!BuilderToolchainPolicy.IsKnownVersion(kind, version) || version == RequiredVersion(kind)) continue;
            try { await RemoveAsync(InstallationAt(kind, version, directory), progress, token).ConfigureAwait(false); }
            catch (BuilderToolchainException error) when (error.Phase == "selection" && error.CanRetry)
            {
                complete = false;
                progress?.Report($"Removal of superseded {kind} {version} is waiting for its active build.");
            }
        }
        return complete;
    }
}
