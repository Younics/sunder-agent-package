namespace Sunder.Agent.Execution.Common;

// Executor-owned proof, never reconstructed from a path string or caller JSON.
// A retained handle pins every recorded identity until the owning operation ends.
internal sealed class LocalCreatedDirectoryAuthority : IDisposable
{
    private readonly object _gate = new();
    private readonly List<CreatedDirectory> _directories = [];
    private readonly List<string> _cleanupFailures = [];
    private bool _disposed;
    internal bool IsDisposed { get { lock (_gate) return _disposed; } }
    internal IReadOnlyList<string> CleanupFailures { get { lock (_gate) return _cleanupFailures.ToArray(); } }

    internal LocalSecureHandle CreateChild(ILocalSecureFileSystemPlatform platform, LocalSecureHandle parent, string name, ulong volume)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Another approved sibling may have published this ancestor since
            // the caller probed it. Reuse only the executor's retained identity.
            if (platform.TryOpenChild(parent, name, volume, requireDirectory: true, out var existing))
            {
                if (OwnsChild(parent, existing!)) return existing!;
                existing!.Dispose();
                throw new LocalSecureApprovalChangedException(Path.Combine(parent.FullPath, name));
            }
            if (_directories.Count >= 512) throw new InvalidOperationException("An operation may create at most 512 parent directories.");
            platform.CreateDirectory(parent, name);
            var created = platform.OpenChild(parent, name, volume, requireDirectory: true);
            try { Record(platform, parent, created); return created; }
            catch { created.Dispose(); throw; }
        }
    }

    private void Record(ILocalSecureFileSystemPlatform platform, LocalSecureHandle parent, LocalSecureHandle created)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_directories.Count >= 512) throw new InvalidOperationException("An operation may create at most 512 parent directories.");
            var retainedParent = parent.Retain();
            try { _directories.Add(new(platform, retainedParent, created.Retain())); }
            catch { retainedParent.Dispose(); throw; }
        }
    }

    internal bool OwnsChild(LocalSecureHandle parent, LocalSecureHandle child)
    {
        lock (_gate) return !_disposed && _directories.Any(directory => directory.Parent.Identity == parent.Identity
            && directory.Child.Identity == child.Identity && HostSecurePathEngine.PathComparer.Equals(directory.Child.FullPath, child.FullPath));
    }

    internal bool AllowsExtension(string originalFingerprint, LocalSecureApprovalLease current)
    {
        lock (_gate)
        {
            if (_disposed || current.Binding.Exists) return false;
            var nodes = current.RetainedPathNodes;
            for (var prefixLength = nodes.Count - 1; prefixLength > 0; prefixLength--)
            {
                if (HostResourceClaim.FingerprintIdentityChain(nodes.Take(prefixLength).Select(node => node.Identity)) != originalFingerprint) continue;
                for (var index = prefixLength; index < nodes.Count; index++)
                {
                    var parentIdentity = nodes[index - 1].Identity;
                    var node = nodes[index];
                    if (!_directories.Any(directory => directory.Parent.Identity == parentIdentity && directory.Child.Identity == node.Identity
                        && HostSecurePathEngine.PathComparer.Equals(directory.Child.FullPath, node.Path))) return false;
                }
                return true;
            }
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var directory in _directories.AsEnumerable().Reverse())
            {
                try
                {
                    var name = Path.GetFileName(directory.Child.FullPath);
                    if (!directory.Platform.TryOpenChild(directory.Parent, name, directory.Parent.Identity.Volume, true, out var current)) continue;
                    using (current)
                    {
                        if (current!.Identity != directory.Child.Identity)
                        {
                            _cleanupFailures.Add($"Created directory was replaced and was not removed: {directory.Child.FullPath}");
                            continue;
                        }
                        // Never recurse or remove a directory somebody populated.
                        if (directory.Platform.EnumerateNames(current).Any()) continue;
                        using var reservation = directory.Platform.ReserveMutationSlots(directory.Parent, 1, CancellationToken.None);
                        directory.Platform.DeleteEntry(directory.Parent, name, true, directory.Child,
                            candidate => !directory.Platform.EnumerateNames(candidate).Any(), reservation,
                            static () => { }, static () => { }, CancellationToken.None);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    _cleanupFailures.Add($"Created directory cleanup could not be confirmed: {directory.Child.FullPath}: {exception.Message}");
                }
                finally { directory.Child.Dispose(); directory.Parent.Dispose(); }
            }
            _directories.Clear();
        }
    }

    private sealed record CreatedDirectory(ILocalSecureFileSystemPlatform Platform, LocalSecureHandle Parent, LocalSecureHandle Child);
}
