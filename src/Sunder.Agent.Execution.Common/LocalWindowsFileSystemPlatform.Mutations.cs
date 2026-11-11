namespace Sunder.Agent.Execution.Common;

internal sealed partial class LocalWindowsFileSystemPlatform
{
    public LocalSecureMutationReservation ReserveMutationSlots(
        LocalSecureHandle parent, int slotCount, CancellationToken cancellationToken)
    {
        RequireDirectory(parent);
        cancellationToken.ThrowIfCancellationRequested();
        if (slotCount < 0 || slotCount > HostSecurePathEngine.MaxQuarantineEntriesPerDirectory)
            throw new LocalSecurePathException("A secure mutation requested an invalid quarantine reservation.");
        IDisposable? lease = HostMutationCoordinator.Enter(parent.Identity, cancellationToken);
        try
        {
            var scanned = 0;
            var reserved = 0;
            foreach (var name in EnumerateNames(parent))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++scanned > HostSecurePathEngine.MaxQuarantineAccountingEntries)
                    throw new LocalSecurePathException($"Secure mutation quarantine accounting exceeds the {HostSecurePathEngine.MaxQuarantineAccountingEntries}-entry scan limit.");
                if (HostSecurePathEngine.IsReservedName(name)) reserved++;
            }
            if (reserved > HostSecurePathEngine.MaxQuarantineEntriesPerDirectory - slotCount)
                throw new LocalSecurePathException($"Secure mutation quarantine exceeds {HostSecurePathEngine.MaxQuarantineEntriesPerDirectory} retained entries.");
            // Prove parent durability/access before creating or moving any entry.
            Flush(parent);
            var reservation = new LocalSecureMutationReservation(
                Enumerable.Range(0, slotCount).Select(static _ => HostSecurePathEngine.CreateQuarantineName()).ToArray(), lease);
            lease = null;
            return reservation;
        }
        finally { lease?.Dispose(); }
    }

    public LocalSecureTemporaryFile CreateTemporaryFile(
        LocalSecureHandle parent, string name, LocalSecureMutationReservation reservation)
    {
        using var created = CreateExclusiveFile(parent, name, 0x180);
        var handle = created.DuplicateHandle();
        return new LocalSecureTemporaryFile(name, created.FullPath, handle, created.Identity, hasDirectoryEntry: true,
            () => LocalWindowsNative.Duplicate(handle));
    }

    public void PublishTemporaryFile(
        LocalSecureHandle parent,
        LocalSecureTemporaryFile temporaryFile,
        string targetName,
        bool overwrite,
        LocalSecureTargetExpectation expectation,
        LocalSecureHandle? expectedTarget,
        Func<LocalSecureHandle, bool>? finalTargetValidator,
        LocalSecureMutationReservation reservation,
        Action beforeMutationSyscall,
        Action afterQuarantineBeforeMetadata,
        Action beforePostQuarantineSync,
        Action afterValidationBeforePublish,
        Action afterNamedTemporaryCopyChunk,
        Action beforeNamedTemporaryPublish,
        Action afterPublishedFileSync,
        Action afterPublishedParentSync,
        Action beforePostPublicationRestore,
        CancellationToken cancellationToken)
    {
        ValidateName(targetName);
        RequireDirectory(parent);
        var path = Path.Combine(parent.FullPath, targetName);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTemporaryFile(parent, temporaryFile);
        if (expectation.Exists
            ? !overwrite || expectedTarget is null || expectation.Identity is null || expectedTarget.Identity != expectation.Identity
            : expectedTarget is not null)
            throw new LocalSecureApprovalChangedException(path);
        beforeMutationSyscall();
        cancellationToken.ThrowIfCancellationRequested();

        WindowsQuarantine? original = null;
        LocalWindowsNative.FileSecuritySnapshot? security = null;
        LocalSecureHandle? published = null;
        try
        {
            if (expectation.Exists)
            {
                original = BeginQuarantine(parent, targetName, expectedTarget!, reservation);
                if (original.Handle.Kind != LocalSecureNodeKind.RegularFile)
                    throw new LocalSecureApprovalChangedException(path);
                afterQuarantineBeforeMetadata();
                security = LocalWindowsNative.CaptureSecurity(original.Handle.Handle, path);
                security.RequireSameOwner(temporaryFile.Handle, path);
                LocalWindowsNative.CopyBasicMetadata(original.Handle.Handle, temporaryFile.Handle, path, temporaryFile.FullPath);
                beforePostQuarantineSync();
                LocalWindowsNative.Flush(temporaryFile.Handle, temporaryFile.FullPath);
                if (finalTargetValidator is not null && !finalTargetValidator(original.Handle))
                    throw new LocalSecureContentChangedException(path);
            }
            else if (TryOpenChild(parent, targetName, parent.Identity.Volume, requireDirectory: false, out var unexpected))
            {
                unexpected!.Dispose();
                throw expectation.ApprovalBound
                    ? new LocalSecureApprovalChangedException(path)
                    : new LocalSecureFileAlreadyExistsException(path);
            }

            // No cancellation beyond reversible quarantine. Neither destination
            // validation nor a source-name probe can authorize replacing by name.
            afterValidationBeforePublish();
            beforeNamedTemporaryPublish();
            ValidateTemporaryFile(parent, temporaryFile);
            try { LocalWindowsNative.RenameNoReplace(temporaryFile.Handle, parent.Handle, targetName, path); }
            catch (LocalSecureFileAlreadyExistsException) when (expectation.ApprovalBound)
            { throw new LocalSecureApprovalChangedException(path); }
            temporaryFile.WasPublished = true;
            temporaryFile.MarkDirectoryEntryRemoved();

            // Keep the protected temporary DACL until the exact-handle rename has
            // published the file. Broad original read permissions must never expose
            // staged secret bytes under a discoverable temporary name.
            security?.ApplyDacl(temporaryFile.Handle, path);
            LocalWindowsNative.Flush(temporaryFile.Handle, path);
            afterPublishedFileSync();
            Flush(parent);
            afterPublishedParentSync();

            // Retain the actual published object for compensation, without carrying
            // its exclusive creation access into a later unrelated operation.
            var retained = LocalWindowsNative.Reopen(temporaryFile.Handle,
                LocalWindowsNative.GenericRead | LocalWindowsNative.ReadControl,
                LocalWindowsNative.ShareAll, path);
            try { published = CreateHandle(retained, path, requireDirectory: false); }
            catch { retained.Dispose(); throw; }
            if (published.Identity != temporaryFile.Identity) throw new LocalSecureApprovalChangedException(path);
            temporaryFile.RetainPublishedTarget(published);
            published = null;
            original?.Commit();
        }
        catch (Exception operationException)
        {
            try
            {
                if (temporaryFile.WasPublished)
                {
                    // Source-handle cleanup cannot truncate/delete an attacker's
                    // replacement. No target-name lookup is cleanup authority.
                    RemoveTemporaryFile(parent, temporaryFile, reservation);
                    if (original is not null) beforePostPublicationRestore();
                }
                original?.Rollback();
            }
            catch (Exception recoveryException)
            {
                original?.Abandon();
                throw new LocalSecureMutationRecoveryException(path, new AggregateException(operationException, recoveryException));
            }
            throw;
        }
        finally
        {
            published?.Dispose();
            security?.Dispose();
            original?.Dispose();
        }
    }

    public void DeleteEntry(
        LocalSecureHandle parent, string name, bool directory, LocalSecureHandle expectedTarget,
        Func<LocalSecureHandle, bool>? finalTargetValidator, LocalSecureMutationReservation reservation,
        Action beforeMutationSyscall, Action afterQuarantine, CancellationToken cancellationToken)
    {
        ValidateName(name);
        if (expectedTarget.Kind != (directory ? LocalSecureNodeKind.Directory : LocalSecureNodeKind.RegularFile))
            throw new LocalSecureApprovalChangedException(Path.Combine(parent.FullPath, name));
        cancellationToken.ThrowIfCancellationRequested();
        beforeMutationSyscall();
        cancellationToken.ThrowIfCancellationRequested();
        using var quarantine = BeginQuarantine(parent, name, expectedTarget, reservation);
        try
        {
            afterQuarantine();
            if (finalTargetValidator is not null && !finalTargetValidator(quarantine.Handle))
                throw new LocalSecureContentChangedException(Path.Combine(parent.FullPath, name));
            // Like Unix, a committed original is retained under a bounded reserved
            // name, not destroyed. Recursive contents may include recovery entries.
            quarantine.Commit();
        }
        catch (Exception operationException)
        {
            try { quarantine.Rollback(); }
            catch (Exception recoveryException)
            {
                quarantine.Abandon();
                throw new LocalSecureMutationRecoveryException(Path.Combine(parent.FullPath, name),
                    new AggregateException(operationException, recoveryException));
            }
            throw;
        }
    }

    public void DeleteTemporaryFile(
        LocalSecureHandle parent, LocalSecureTemporaryFile temporaryFile, LocalSecureMutationReservation reservation)
    {
        try { RemoveTemporaryFile(parent, temporaryFile, reservation); }
        catch (Exception exception)
        { throw new LocalSecureMutationRecoveryException(temporaryFile.FullPath, exception); }
    }

    private void RemoveTemporaryFile(LocalSecureHandle parent, LocalSecureTemporaryFile temporaryFile,
        LocalSecureMutationReservation reservation)
    {
        var metadata = LocalWindowsNative.ReadMetadata(temporaryFile.Handle, temporaryFile.FullPath);
        if (metadata.Kind != LocalSecureNodeKind.RegularFile || metadata.Volume != parent.Identity.Volume
            || new LocalSecureIdentity(metadata.Volume, metadata.FileId, metadata.FileIdHigh) != temporaryFile.Identity)
            throw new LocalSecureApprovalChangedException(temporaryFile.FullPath);
        // Creation provenance plus the retained handle is cleanup authority even
        // if somebody has linked our own staging inode elsewhere. Sanitize that
        // owned inode, never reopen/truncate either the temporary or alias name.
        using (var stream = new FileStream(temporaryFile.DuplicateHandle(), FileAccess.Write))
        {
            stream.SetLength(0);
            stream.Flush(flushToDisk: true);
        }
        LocalWindowsNative.Flush(temporaryFile.Handle, temporaryFile.FullPath);
        if (LocalWindowsNative.ReadMetadata(temporaryFile.Handle, temporaryFile.FullPath).Length != 0)
            throw new LocalSecurePathException("A retained temporary entry could not be truncated.");
        if (!temporaryFile.RetiredForCleanup)
        {
            // POSIX disposition retires a Windows name only when its file object
            // closes. The caller can still own stream duplicates here. A handle
            // rename retires the visible target now, so original restoration does
            // not depend on those duplicates closing. Keep the sanitized recovery
            // entry under the same bounded, durable quarantine policy as Unix.
            LocalWindowsNative.RenameNoReplace(temporaryFile.Handle, parent.Handle,
                reservation.TakeQuarantineName(), temporaryFile.FullPath);
            temporaryFile.MarkRetiredForCleanup();
        }
        Flush(parent);
    }

    private static void ValidateTemporaryFile(LocalSecureHandle parent, LocalSecureTemporaryFile temporaryFile)
    {
        var current = LocalWindowsNative.ReadMetadata(temporaryFile.Handle, temporaryFile.FullPath);
        if (current.Kind != LocalSecureNodeKind.RegularFile || current.Volume != parent.Identity.Volume
            || new LocalSecureIdentity(current.Volume, current.FileId, current.FileIdHigh) != temporaryFile.Identity
            || current.LinkCount != 1 || !temporaryFile.HasDirectoryEntry)
            throw new LocalSecureApprovalChangedException(temporaryFile.FullPath);
    }

    private LocalSecureHandle LockEntry(LocalSecureHandle parent, string name, LocalSecureHandle? expected = null)
    {
        ValidateName(name);
        RequireDirectory(parent);
        if (expected?.MutationExclusive == true)
        {
            using var named = OpenChild(parent, name, parent.Identity.Volume, requireDirectory: false);
            if (named.Identity != expected.Identity || named.Kind != expected.Kind)
                throw new LocalSecureApprovalChangedException(named.FullPath);
            RequireIdentity(expected, LocalWindowsNative.ReadMetadata(expected.Handle, expected.FullPath));
            return expected.Retain();
        }
        var path = Path.Combine(parent.FullPath, name);
        // Deny competing renames/deletes. Regular files also deny content writers;
        // directories allow child creation, but all mutations still use exact handles.
        using var probe = expected is null ? OpenChild(parent, name, parent.Identity.Volume, requireDirectory: false) : null;
        var kind = expected?.Kind ?? probe!.Kind;
        var native = LocalWindowsNative.OpenRelative(parent.Handle, name,
            LocalWindowsNative.GenericRead | LocalWindowsNative.ReadControl | LocalWindowsNative.DeleteAccess,
            requireDirectory: kind == LocalSecureNodeKind.Directory, create: false, parent.CaseSensitiveDirectory, path,
            kind == LocalSecureNodeKind.Directory ? LocalWindowsNative.ShareRead | LocalWindowsNative.ShareWrite : LocalWindowsNative.ShareRead);
        LocalSecureHandle? locked = null;
        try
        {
            locked = CreateHandle(native, path, requireDirectory: kind == LocalSecureNodeKind.Directory, mutationExclusive: true);
            RequireVolume(parent, locked);
            if (expected is not null && (locked.Identity != expected.Identity || locked.Kind != expected.Kind))
                throw new LocalSecureApprovalChangedException(path);
            return locked;
        }
        catch { if (locked is null) native.Dispose(); else locked.Dispose(); throw; }
    }

    private WindowsQuarantine BeginQuarantine(
        LocalSecureHandle parent, string name, LocalSecureHandle expected, LocalSecureMutationReservation reservation)
    {
        var locked = LockEntry(parent, name, expected);
        WindowsQuarantine? transaction = null;
        try
        {
            var quarantineName = reservation.TakeQuarantineName();
            LocalWindowsNative.RenameNoReplace(locked.Handle, parent.Handle, quarantineName, locked.FullPath);
            transaction = new WindowsQuarantine(this, parent, name, locked, reservation);
            Flush(parent);
            RequireIdentity(locked, LocalWindowsNative.ReadMetadata(locked.Handle, locked.FullPath));
            return transaction;
        }
        catch (Exception operationException)
        {
            if (transaction is null) { locked.Dispose(); throw; }
            try { transaction.Rollback(); }
            catch (Exception recoveryException)
            {
                transaction.Abandon();
                throw new LocalSecureMutationRecoveryException(locked.FullPath, new AggregateException(operationException, recoveryException));
            }
            finally { transaction.Dispose(); }
            throw;
        }
    }

    private sealed class WindowsQuarantine(
        LocalWindowsFileSystemPlatform owner, LocalSecureHandle parent, string targetName,
        LocalSecureHandle handle, LocalSecureMutationReservation reservation) : IDisposable
    {
        private int _state;
        public LocalSecureHandle Handle { get; } = handle;

        public void Commit()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
                throw new InvalidOperationException("The secure quarantine transaction is already complete.");
        }

        public void Rollback()
        {
            if (Volatile.Read(ref _state) == 2) return;
            if (Volatile.Read(ref _state) != 0)
                throw new InvalidOperationException("The secure quarantine transaction cannot be rolled back.");
            try
            {
                RequireIdentity(Handle, LocalWindowsNative.ReadMetadata(Handle.Handle, Handle.FullPath));
                try { LocalWindowsNative.RenameNoReplace(Handle.Handle, parent.Handle, targetName, Handle.FullPath); }
                catch (LocalSecureFileAlreadyExistsException)
                {
                    // Preserve a proven conflict, then make exactly one no-replace
                    // restore attempt. Never overwrite a subsequent new arrival.
                    using var conflict = owner.LockEntry(parent, targetName);
                    LocalWindowsNative.RenameNoReplace(conflict.Handle, parent.Handle, reservation.TakeQuarantineName(), conflict.FullPath);
                    owner.Flush(parent);
                    LocalWindowsNative.RenameNoReplace(Handle.Handle, parent.Handle, targetName, Handle.FullPath);
                }
                owner.Flush(parent);
                using var restored = owner.OpenChild(parent, targetName, parent.Identity.Volume, requireDirectory: false);
                if (restored.Identity != Handle.Identity || restored.Kind != Handle.Kind)
                    throw new LocalSecureApprovalChangedException(restored.FullPath);
                Interlocked.Exchange(ref _state, 2);
            }
            catch (Exception exception)
            { throw new LocalSecureMutationRecoveryException(Path.Combine(parent.FullPath, targetName), exception); }
        }

        public void Abandon() => Interlocked.CompareExchange(ref _state, 3, 0);

        public void Dispose()
        {
            try { if (Volatile.Read(ref _state) == 0) Rollback(); }
            finally { Handle.Dispose(); }
        }
    }
}
