namespace Sunder.Agent.Execution.Common;

internal sealed class LocalSecurePathSession : IDisposable
{
    private readonly LocalSecureRoot _root;
    private readonly bool _ownsRoot;
    private readonly IReadOnlyList<LocalSecureHandle> _descendants;
    private readonly LocalResourceBinding? _expectedBinding;
    private readonly ILocalSecureFileSystemHooks? _hooks;
    private readonly LocalSecureHandle? _retainedTarget;
    private int _postMutationAuthorityTaken;

    private LocalSecurePathSession(
        LocalSecureRoot root,
        bool ownsRoot,
        IReadOnlyList<LocalSecureHandle> descendants,
        LocalSecureHandle parent,
        string fullPath,
        string? targetName,
        LocalResourceBinding? expectedBinding,
        ILocalSecureFileSystemHooks? hooks,
        LocalSecureHandle? retainedTarget = null)
    {
        _root = root;
        _ownsRoot = ownsRoot;
        _descendants = descendants;
        Parent = parent;
        FullPath = fullPath;
        TargetName = targetName;
        _expectedBinding = expectedBinding;
        _hooks = hooks;
        _retainedTarget = retainedTarget;
    }

    public LocalSecureHandle Parent { get; }

    public string FullPath { get; }

    public string? TargetName { get; }

    public bool IsRootTarget => TargetName is null;

    public static LocalSecurePathSession CreateOwned(
        LocalSecureRoot root,
        string fullPath,
        bool createParents,
        LocalResourceBinding? expectedBinding,
        ILocalSecureFileSystemHooks? hooks,
        CancellationToken cancellationToken)
        => Create(root, ownsRoot: true, fullPath, createParents, expectedBinding, hooks, cancellationToken);

    public static LocalSecurePathSession CreateBorrowed(
        LocalSecureRoot root,
        string fullPath,
        bool createParents,
        ILocalSecureFileSystemHooks? hooks,
        CancellationToken cancellationToken)
        => Create(root, ownsRoot: false, fullPath, createParents, expectedBinding: null, hooks, cancellationToken);

    public static LocalSecurePathSession CreateApproved(
        LocalSecureRoot root,
        IReadOnlyList<LocalSecureHandle> retainedAncestors,
        LocalSecureHandle? retainedTarget,
        LocalResourceBinding binding,
        bool createParents,
        bool allowMissingSuffix,
        ILocalSecureFileSystemHooks? hooks,
        CancellationToken cancellationToken)
    {
        var parent = retainedAncestors.Count == 0 ? root.Handle : retainedAncestors[^1];
        if (parent.Identity != binding.AnchorIdentity)
        {
            DisposeApproved(root, retainedAncestors, retainedTarget);
            throw new LocalSecureApprovalChangedException(binding.FullPath);
        }

        if (binding.TargetIsAuthorityRoot)
        {
            if (retainedAncestors.Count != 0
                || !binding.Exists
                || binding.TargetIdentity != root.Handle.Identity
                || binding.TargetKind != root.Handle.Kind
                || retainedTarget is not null)
            {
                DisposeApproved(root, retainedAncestors, retainedTarget);
                throw new LocalSecureApprovalChangedException(binding.FullPath);
            }
            return new LocalSecurePathSession(
                root,
                ownsRoot: true,
                retainedAncestors,
                root.Handle,
                binding.FullPath,
                targetName: null,
                binding,
                hooks);
        }

        var finalSegments = HostSecurePathEngine.GetRelativeSegments(parent.FullPath, binding.FullPath);
        if (finalSegments.Count == 0
            || (binding.Exists && finalSegments.Count != 1)
            || (!binding.Exists && !allowMissingSuffix && finalSegments.Count != 1)
            || binding.TargetIsAuthorityRoot
            || binding.Exists != (retainedTarget is not null)
            || (retainedTarget is not null
                && (retainedTarget.Identity != binding.TargetIdentity
                    || retainedTarget.Kind != binding.TargetKind)))
        {
            DisposeApproved(root, retainedAncestors, retainedTarget);
            throw new LocalSecureApprovalChangedException(binding.FullPath);
        }

        if (!binding.Exists && finalSegments.Count > 1)
        {
            if (!createParents)
            {
                DisposeApproved(root, retainedAncestors, retainedTarget);
                throw new LocalSecurePathNotFoundException(Path.Combine(parent.FullPath, finalSegments[0]));
            }

            var descendants = retainedAncestors.ToList();
            var current = parent;
            try
            {
                foreach (var segment in finalSegments.Take(finalSegments.Count - 1))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.BeforeOpen, current.FullPath, segment);
                    if (root.Platform.TryOpenChild(
                            current,
                            segment,
                            root.Volume,
                            requireDirectory: true,
                            out var unexpectedlyExisting))
                    {
                        unexpectedlyExisting!.Dispose();
                        throw new LocalSecureApprovalChangedException(binding.FullPath);
                    }
                    root.Platform.CreateDirectory(current, segment);
                    var created = root.Platform.OpenChild(
                        current,
                        segment,
                        root.Volume,
                        requireDirectory: true);
                    descendants.Add(created);
                    current = created;
                }
                return new LocalSecurePathSession(
                    root,
                    ownsRoot: true,
                    descendants,
                    current,
                    binding.FullPath,
                    finalSegments[^1],
                    binding,
                    hooks,
                    retainedTarget);
            }
            catch
            {
                for (var index = descendants.Count - 1; index >= retainedAncestors.Count; index--)
                {
                    descendants[index].Dispose();
                }
                DisposeApproved(root, retainedAncestors, retainedTarget);
                throw;
            }
        }

        return new LocalSecurePathSession(
            root,
            ownsRoot: true,
            retainedAncestors,
            parent,
            binding.FullPath,
            finalSegments[0],
            binding,
            hooks,
            retainedTarget);
    }

    public LocalSecureOpenedTarget? TryOpenTarget(bool writable = false)
    {
        if (_retainedTarget is not null)
        {
            ValidateExpectedIdentity(_retainedTarget);
            return new LocalSecureOpenedTarget(_retainedTarget, ownsHandle: false);
        }
        if (TargetName is null)
        {
            ValidateExpectedIdentity(Parent);
            return new LocalSecureOpenedTarget(Parent, ownsHandle: false);
        }

        _hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.BeforeOpen, Parent.FullPath, TargetName);
        if (!_root.Platform.TryOpenChild(
                Parent,
                TargetName,
                _root.Volume,
                requireDirectory: false,
                out var target,
                writable))
        {
            if (_expectedBinding?.Exists == true)
            {
                throw new LocalSecureApprovalChangedException(FullPath);
            }
            return null;
        }

        try
        {
            ValidateExpectedIdentity(target!);
            return new LocalSecureOpenedTarget(target!, ownsHandle: true);
        }
        catch
        {
            target!.Dispose();
            throw;
        }
    }

    public LocalSecureOpenedTarget OpenTarget(bool writable = false)
        => TryOpenTarget(writable) ?? throw new LocalSecurePathNotFoundException(FullPath);

    public LocalSecureHandle OpenChild(
        LocalSecureHandle directory,
        string name,
        bool requireDirectory = false,
        bool writable = false)
    {
        _hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.BeforeOpen, directory.FullPath, name);
        return _root.Platform.OpenChild(directory, name, _root.Volume, requireDirectory, writable);
    }

    public IEnumerable<string> EnumerateNames(LocalSecureHandle directory)
        => _root.Platform.EnumerateNames(directory)
            .Where(name => !HostSecurePathEngine.IsReservedName(name));

    public IEnumerable<string> EnumerateRawNames(LocalSecureHandle directory)
        => _root.Platform.EnumerateNames(directory);

    public LocalSecureTemporaryFile CreateTemporaryFile(LocalSecureMutationReservation reservation)
    {
        if (TargetName is null)
        {
            throw new LocalSecurePathException("A configured root cannot be replaced by a structured write.");
        }
        var name = HostSecurePathEngine.CreateTemporaryName();
        return _root.Platform.CreateTemporaryFile(Parent, name, reservation);
    }

    public LocalSecureMutationReservation ReserveMutationSlots(
        LocalSecureHandle parent,
        int slotCount,
        CancellationToken cancellationToken)
        => _root.Platform.ReserveMutationSlots(parent, slotCount, cancellationToken);

    public void PublishTemporaryFile(
        LocalSecureTemporaryFile temporaryFile,
        bool overwrite,
        LocalSecureTargetExpectation expectation,
        LocalSecureHandle? expectedTarget,
        Func<LocalSecureHandle, bool>? finalTargetValidator,
        LocalSecureMutationReservation reservation,
        CancellationToken cancellationToken)
    {
        if (TargetName is null)
        {
            throw new LocalSecurePathException("A configured root cannot be replaced by a structured write.");
        }
        _root.Platform.PublishTemporaryFile(
            Parent,
            temporaryFile,
            TargetName,
            overwrite,
            expectation,
            expectedTarget,
            finalTargetValidator,
            reservation,
            () => _hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.BeforePublishSyscall, Parent.FullPath, TargetName),
            () => _hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.AfterQuarantineBeforeMetadata, Parent.FullPath, TargetName),
            () => _hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.BeforePostQuarantineSync, Parent.FullPath, TargetName),
            () => _hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.AfterValidationBeforePublish, Parent.FullPath, TargetName),
            () => _hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.AfterNamedTemporaryCopyChunk, Parent.FullPath, TargetName),
            () => _hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.BeforeNamedTemporaryPublish, Parent.FullPath, TargetName),
            () => _hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.AfterPublishedFileSync, Parent.FullPath, TargetName),
            () => _hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.AfterPublishedParentSync, Parent.FullPath, TargetName),
            () => _hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.BeforePostPublicationRestore, Parent.FullPath, TargetName),
            cancellationToken);
    }

    public void PrepareToPublish()
    {
        if (TargetName is null)
        {
            throw new LocalSecurePathException("A configured root cannot be replaced by a structured write.");
        }
        _hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.BeforePublish, Parent.FullPath, TargetName);
    }

    public void DeleteTarget(
        LocalSecureHandle expectedTarget,
        Func<LocalSecureHandle, bool>? finalTargetValidator = null,
        bool runCheckpoint = true,
        LocalSecureMutationReservation? reservation = null,
        CancellationToken cancellationToken = default)
    {
        if (TargetName is null)
        {
            throw new LocalSecurePathException("A configured or approved secure root cannot be deleted.");
        }
        if (runCheckpoint)
        {
            PrepareToDeleteTarget();
        }
        _root.Platform.DeleteEntry(
            Parent,
            TargetName,
            expectedTarget.Kind == LocalSecureNodeKind.Directory,
            expectedTarget,
            finalTargetValidator,
            reservation ?? _root.Platform.ReserveMutationSlots(Parent, 1, cancellationToken),
            () => _hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.BeforeUnlinkSyscall, Parent.FullPath, TargetName),
            () => _hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.AfterValidationBeforeDelete, Parent.FullPath, TargetName),
            cancellationToken);
    }

    public void PrepareToDeleteTarget()
    {
        if (TargetName is null)
        {
            throw new LocalSecurePathException("A configured or approved secure root cannot be deleted.");
        }
        _hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.BeforeUnlink, Parent.FullPath, TargetName);
    }

    public void DeleteChild(
        LocalSecureHandle parent,
        string name,
        LocalSecureHandle expectedTarget,
        Func<LocalSecureHandle, bool>? finalTargetValidator,
        LocalSecureMutationReservation reservation,
        CancellationToken cancellationToken)
    {
        _hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.BeforeUnlink, parent.FullPath, name);
        _root.Platform.DeleteEntry(
            parent,
            name,
            expectedTarget.Kind == LocalSecureNodeKind.Directory,
            expectedTarget,
            finalTargetValidator,
            reservation,
            () => _hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.BeforeUnlinkSyscall, parent.FullPath, name),
            () => _hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.AfterValidationBeforeDelete, parent.FullPath, name),
            cancellationToken);
    }

    public void DeleteTemporaryFile(
        LocalSecureTemporaryFile temporaryFile,
        LocalSecureMutationReservation reservation)
    {
        if (!temporaryFile.WasPublished)
        {
            _root.Platform.DeleteTemporaryFile(
                Parent,
                temporaryFile,
                reservation);
        }
    }

    public LocalSecureApprovalLease TakePostMutationAuthority(
        LocalSecureHandle? retainedTarget,
        LocalSecureIdentity? expectedTargetIdentity,
        LocalSecureNodeKind? expectedTargetKind)
        => TakePostMutationAuthority(
            retainedTarget,
            expectedTargetIdentity,
            expectedTargetKind,
            verifyMissingTarget: true);

    public LocalSecureApprovalLease TakeDeletedPostMutationAuthority()
    {
        if (TargetName is not null)
        {
            _hooks?.OnCheckpoint(
                LocalSecureFileSystemCheckpoint.AfterDeleteBeforeAuthorityCapture,
                Parent.FullPath,
                TargetName);
        }
        return TakePostMutationAuthority(
            retainedTarget: null,
            expectedTargetIdentity: null,
            expectedTargetKind: null,
            verifyMissingTarget: false);
    }

    private LocalSecureApprovalLease TakePostMutationAuthority(
        LocalSecureHandle? retainedTarget,
        LocalSecureIdentity? expectedTargetIdentity,
        LocalSecureNodeKind? expectedTargetKind,
        bool verifyMissingTarget)
    {
        if (!_ownsRoot || TargetName is null)
        {
            throw new LocalSecurePathException(
                "Post-mutation authority requires an owned, non-root secure path session.");
        }

        try
        {
            if (expectedTargetIdentity is { } identity)
            {
                if (retainedTarget is null
                    || retainedTarget.Identity != identity
                    || retainedTarget.Kind != expectedTargetKind)
                {
                    throw new LocalSecureApprovalChangedException(FullPath);
                }
            }
            else
            {
                if (retainedTarget is not null)
                {
                    throw new LocalSecureApprovalChangedException(FullPath);
                }
                if (verifyMissingTarget)
                {
                    _hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.BeforeOpen, Parent.FullPath, TargetName);
                    if (_root.Platform.TryOpenChild(
                            Parent,
                            TargetName,
                            _root.Volume,
                            requireDirectory: false,
                            out retainedTarget))
                    {
                        throw new LocalSecureApprovalChangedException(FullPath);
                    }
                }
            }

            var binding = new LocalResourceBinding(
                FullPath,
                Parent.FullPath,
                Parent.Identity,
                retainedTarget?.Identity,
                retainedTarget?.Kind,
                Exists: retainedTarget is not null,
                CaseSensitivePath: _expectedBinding?.CaseSensitivePath
                                   ?? OperatingSystem.IsWindows()
                                   && (_root.Handle.CaseSensitiveDirectory
                                       || _descendants.Any(static handle => handle.CaseSensitiveDirectory)),
                TargetIsAuthorityRoot: false);
            var authority = new LocalSecureApprovalLease(
                binding,
                _root,
                _descendants,
                retainedTarget);
            retainedTarget = null;
            if (Interlocked.Exchange(ref _postMutationAuthorityTaken, 1) != 0)
            {
                authority.Dispose();
                throw new InvalidOperationException("Post-mutation authority was already transferred.");
            }
            return authority;
        }
        finally
        {
            retainedTarget?.Dispose();
        }
    }

    public void Dispose()
    {
        _retainedTarget?.Dispose();
        if (Volatile.Read(ref _postMutationAuthorityTaken) != 0)
        {
            return;
        }
        for (var index = _descendants.Count - 1; index >= 0; index--)
        {
            _descendants[index].Dispose();
        }
        if (_ownsRoot)
        {
            _root.Dispose();
        }
    }

    private static LocalSecurePathSession Create(
        LocalSecureRoot root,
        bool ownsRoot,
        string fullPath,
        bool createParents,
        LocalResourceBinding? expectedBinding,
        ILocalSecureFileSystemHooks? hooks,
        CancellationToken cancellationToken)
    {
        var descendants = new List<LocalSecureHandle>();
        try
        {
            var segments = HostSecurePathEngine.GetRelativeSegments(root.Path, fullPath);
            if (segments.Count == 0)
            {
                return new LocalSecurePathSession(
                    root,
                    ownsRoot,
                    descendants,
                    root.Handle,
                    fullPath,
                    targetName: null,
                    expectedBinding,
                    hooks);
            }

            var current = root.Handle;
            if (expectedBinding?.Exists == false && segments.Count > 0)
            {
                hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.BeforeOpen, current.FullPath, segments[0]);
                if (root.Platform.TryOpenChild(
                        current,
                        segments[0],
                        root.Volume,
                        requireDirectory: segments.Count > 1,
                        out var unexpectedlyExisting))
                {
                    unexpectedlyExisting!.Dispose();
                    throw new LocalSecureApprovalChangedException(fullPath);
                }
            }
            foreach (var segment in segments.Take(segments.Count - 1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.BeforeOpen, current.FullPath, segment);
                if (!root.Platform.TryOpenChild(current, segment, root.Volume, requireDirectory: true, out var child))
                {
                    if (!createParents)
                    {
                        throw new LocalSecurePathNotFoundException(Path.Combine(current.FullPath, segment));
                    }
                    root.Platform.CreateDirectory(current, segment);
                    child = root.Platform.OpenChild(current, segment, root.Volume, requireDirectory: true);
                }
                descendants.Add(child!);
                current = child!;
            }

            return new LocalSecurePathSession(
                root,
                ownsRoot,
                descendants,
                current,
                fullPath,
                segments[^1],
                expectedBinding,
                hooks);
        }
        catch
        {
            for (var index = descendants.Count - 1; index >= 0; index--)
            {
                descendants[index].Dispose();
            }
            if (ownsRoot)
            {
                root.Dispose();
            }
            throw;
        }
    }

    private void ValidateExpectedIdentity(LocalSecureHandle target)
    {
        if (_expectedBinding is null)
        {
            return;
        }
        if (!_expectedBinding.Exists
            || _expectedBinding.TargetIdentity != target.Identity
            || _expectedBinding.TargetKind != target.Kind)
        {
            throw new LocalSecureApprovalChangedException(FullPath);
        }
    }

    private static void DisposeApproved(
        LocalSecureRoot root,
        IReadOnlyList<LocalSecureHandle> retainedAncestors,
        LocalSecureHandle? retainedTarget)
    {
        retainedTarget?.Dispose();
        for (var index = retainedAncestors.Count - 1; index >= 0; index--)
        {
            retainedAncestors[index].Dispose();
        }
        root.Dispose();
    }
}

internal sealed class LocalSecureOpenedTarget(LocalSecureHandle handle, bool ownsHandle) : IDisposable
{
    public LocalSecureHandle Handle { get; } = handle;

    public void Dispose()
    {
        if (ownsHandle)
        {
            Handle.Dispose();
        }
    }
}
