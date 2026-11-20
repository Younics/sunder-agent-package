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
            Flush(parent);
            var reservation = new LocalSecureMutationReservation(
                Enumerable.Range(0, slotCount).Select(static _ => HostSecurePathEngine.CreateQuarantineName()).ToArray(), lease);
            lease = null;
            return reservation;
        }
        finally { lease?.Dispose(); }
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

    private LocalSecureHandle LockEntry(LocalSecureHandle parent, string name, LocalSecureHandle expected)
    {
        ValidateName(name);
        RequireDirectory(parent);
        if (expected.MutationExclusive)
        {
            using var named = OpenChild(parent, name, parent.Identity.Volume, requireDirectory: false);
            if (named.Identity != expected.Identity || named.Kind != expected.Kind)
                throw new LocalSecureApprovalChangedException(named.FullPath);
            RequireIdentity(expected, LocalWindowsNative.ReadMetadata(expected.Handle, expected.FullPath));
            return expected.Retain();
        }
        var path = Path.Combine(parent.FullPath, name);
        var native = LocalWindowsNative.OpenRelative(parent.Handle, name,
            LocalWindowsNative.GenericRead | LocalWindowsNative.ReadControl | LocalWindowsNative.DeleteAccess,
            requireDirectory: expected.Kind == LocalSecureNodeKind.Directory, create: false, parent.CaseSensitiveDirectory, path,
            expected.Kind == LocalSecureNodeKind.Directory ? LocalWindowsNative.ShareRead | LocalWindowsNative.ShareWrite : LocalWindowsNative.ShareRead);
        LocalSecureHandle? locked = null;
        try
        {
            locked = CreateHandle(native, path, requireDirectory: expected.Kind == LocalSecureNodeKind.Directory, mutationExclusive: true);
            RequireVolume(parent, locked);
            if (locked.Identity != expected.Identity || locked.Kind != expected.Kind)
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
            LocalWindowsNative.RenameWithinParentNoReplace(locked.Handle, reservation.TakeQuarantineName(), locked.FullPath);
            transaction = new WindowsQuarantine(this, parent, name, locked);
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
        LocalWindowsFileSystemPlatform owner, LocalSecureHandle parent, string targetName, LocalSecureHandle handle) : IDisposable
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
                // A new arrival is not recovery authority. Leave it at its name,
                // retain our quarantined object and report recovery-required.
                LocalWindowsNative.RenameWithinParentNoReplace(Handle.Handle, targetName, Handle.FullPath);
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
