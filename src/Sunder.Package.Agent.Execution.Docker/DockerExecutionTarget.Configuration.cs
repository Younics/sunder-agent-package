using Sunder.Agent.Execution.Common;
using Sunder.Sdk.Storage;

namespace Sunder.Package.Agent.Execution.Docker;

public sealed partial class DockerExecutionTarget
{
    private async Task<string> ResolveImageIdentityAsync(
        string imageReference,
        CancellationToken cancellationToken)
    {
        var inspect = await RunDockerAsync(
            ["image", "inspect", "--format", "{{.Id}}", imageReference],
            cancellationToken);
        var identity = inspect.Output.Trim();
        if (inspect.ExitCode != 0
            || !identity.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            || identity.Length != "sha256:".Length + 64
            || !identity["sha256:".Length..].All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(AppendDockerOutput(
                $"Docker image '{imageReference}' does not have a resolvable immutable image id.",
                inspect.Output));
        }

        var normalizedIdentity = identity.ToLowerInvariant();
        _verifiedImageIdentities[imageReference] = normalizedIdentity;
        await _packageContext.Storage.State.SetValueAsync(
            BuildImageIdentityKey(imageReference),
            normalizedIdentity,
            cancellationToken).ConfigureAwait(false);
        return normalizedIdentity;
    }

    private async Task<DockerExecutionConfigurationSnapshot> CaptureCurrentConfigurationAsync(
        string bindingId,
        CancellationToken cancellationToken,
        DockerExecutionWorkspaceConfig? workspaceConfig = null)
    {
        var snapshot = await _configService.GetConfigurationSnapshotAsync(
            bindingId,
            cancellationToken,
            workspaceConfig);
        var cli = await _dockerCliRunner.ResolveExecutableAsync(cancellationToken);
        var imageReference = snapshot.WorkspaceConfig.ImageReference;
        string? imageIdentity = null;
        if (imageReference is not null
            && !_verifiedImageIdentities.TryGetValue(imageReference, out imageIdentity))
        {
            var stored = await _packageContext.Storage.State.GetValueAsync(
                BuildImageIdentityKey(imageReference),
                cancellationToken).ConfigureAwait(false);
            if (IsValidImageIdentity(stored))
            {
                imageIdentity = stored!.ToLowerInvariant();
                _verifiedImageIdentities[imageReference] = imageIdentity;
            }
        }

        return snapshot with
        {
            DockerCliPath = cli.ExecutablePath,
            DockerCliResolution = cli.ExecutablePath
                                  ?? string.Join('\n', cli.CheckedLocations),
            ImageIdentity = imageIdentity,
        };
    }

    private static string BuildImageIdentityKey(string imageReference)
        => PackageStorageKeyFactory.Create("image-identities", 1, imageReference);

    private static bool IsValidImageIdentity(string? identity)
        => identity is not null
           && identity.Length == "sha256:".Length + 64
           && identity.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
           && identity["sha256:".Length..].All(Uri.IsHexDigit);

    private sealed class DockerStructuredOperationLease(
        DockerContainerLifecycleService.DockerContainerLease container,
        IReadOnlyList<DockerVerifiedMount> mounts) : IDisposable
    {
        private readonly HashSet<LocalSecureRoot> _detachedRoots = [];
        private DockerContainerLifecycleService.DockerContainerLease? _container = container;

        public string ContainerName => _container?.ContainerName
                                       ?? throw new ObjectDisposedException(nameof(DockerStructuredOperationLease));

        public IReadOnlyList<DockerVerifiedMount> Mounts { get; } = mounts;

        public LocalSecureRoot GetMountRoot(DockerExecutionMount mount)
            => FindMount(mount).Root;

        public LocalSecureRoot TakeMountRoot(DockerExecutionMount mount)
        {
            var root = FindMount(mount).Root;
            if (!_detachedRoots.Add(root))
            {
                throw new InvalidOperationException(
                    $"Docker mount authority for '{mount.ContainerPath}' was already transferred.");
            }
            return root;
        }

        public void Dispose()
        {
            foreach (var mount in Mounts)
            {
                if (!_detachedRoots.Contains(mount.Root))
                {
                    mount.Root.Dispose();
                }
            }
            Interlocked.Exchange(ref _container, null)?.Dispose();
        }

        private DockerVerifiedMount FindMount(DockerExecutionMount mount)
            => Mounts.Single(candidate =>
                string.Equals(candidate.Mount.ContainerPath, mount.ContainerPath, StringComparison.Ordinal));
    }
}
