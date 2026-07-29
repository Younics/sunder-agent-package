using Microsoft.Win32.SafeHandles;

namespace Sunder.Agent.Execution.Common;

internal sealed partial class LocalUnixFileSystemPlatform : ILocalSecureFileSystemPlatform
{
    public LocalSecureHandle OpenSystemRoot(string pathRoot)
    {
        if (!string.Equals(pathRoot, Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            throw new PlatformNotSupportedException($"Unsupported Unix filesystem root: {pathRoot}");
        }

        var descriptor = LocalUnixNative.Open(
            pathRoot,
            LocalUnixNative.ReadOnly
            | LocalUnixNative.DirectoryFlag
            | LocalUnixNative.NoFollowFlag
            | LocalUnixNative.CloseOnExecFlag);
        return descriptor < 0
            ? throw LocalUnixNative.Error("open root")
            : CreateHandle(descriptor, pathRoot, requireDirectory: true);
    }

    public LocalSecureHandle OpenChild(
        LocalSecureHandle parent,
        string name,
        ulong expectedVolume,
        bool requireDirectory,
        bool writable = false)
    {
        ValidateName(name);
        var flags = (writable && !requireDirectory ? LocalUnixNative.ReadWrite : LocalUnixNative.ReadOnly)
                     | LocalUnixNative.NoFollowFlag
                    | LocalUnixNative.CloseOnExecFlag
                    | LocalUnixNative.NonBlockingFlag;
        if (requireDirectory)
        {
            flags |= LocalUnixNative.DirectoryFlag;
        }

        var descriptor = LocalUnixNative.OpenAt(parent.Handle.DangerousGetHandle().ToInt32(), name, flags);
        if (descriptor < 0
            && writable
            && !requireDirectory
            && System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == 21)
        {
            flags = LocalUnixNative.ReadOnly
                    | LocalUnixNative.NoFollowFlag
                    | LocalUnixNative.CloseOnExecFlag
                    | LocalUnixNative.NonBlockingFlag;
            descriptor = LocalUnixNative.OpenAt(parent.Handle.DangerousGetHandle().ToInt32(), name, flags);
        }
        if (descriptor < 0)
        {
            var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            if (error == 2)
            {
                throw new LocalSecurePathNotFoundException(Path.Combine(parent.FullPath, name));
            }
            throw new LocalSecurePathException(
                $"Secure filesystem openat failed for '{Path.Combine(parent.FullPath, name)}': "
                + new System.ComponentModel.Win32Exception(error).Message);
        }

        var child = CreateHandle(descriptor, Path.Combine(parent.FullPath, name), requireDirectory);
        if (child.Identity.Volume != expectedVolume)
        {
            child.Dispose();
            throw new LocalSecurePathException(
                $"Path crosses a filesystem device boundary: {Path.Combine(parent.FullPath, name)}");
        }
        return child;
    }

    public bool TryOpenChild(
        LocalSecureHandle parent,
        string name,
        ulong expectedVolume,
        bool requireDirectory,
        out LocalSecureHandle? child,
        bool writable = false)
    {
        try
        {
            child = OpenChild(parent, name, expectedVolume, requireDirectory, writable);
            return true;
        }
        catch (LocalSecurePathNotFoundException)
        {
            child = null;
            return false;
        }
    }

    public void CreateDirectory(LocalSecureHandle parent, string name)
    {
        ValidateName(name);
        LocalUnixNative.MakeDirectory(parent.Handle.DangerousGetHandle().ToInt32(), name);
        LocalUnixNative.Sync(parent.Handle.DangerousGetHandle().ToInt32());
    }

    public LocalSecureHandle CreateExclusiveFile(
        LocalSecureHandle parent,
        string name,
        uint unixMode)
    {
        ValidateName(name);
        var descriptor = LocalUnixNative.OpenAt(
            parent.Handle.DangerousGetHandle().ToInt32(),
            name,
            LocalUnixNative.ReadWrite
            | LocalUnixNative.CreateFlag
            | LocalUnixNative.ExclusiveFlag
            | LocalUnixNative.NoFollowFlag
            | LocalUnixNative.CloseOnExecFlag,
            unixMode);
        if (descriptor < 0)
        {
            var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            if (error == 17)
            {
                throw new LocalSecureFileAlreadyExistsException(Path.Combine(parent.FullPath, name));
            }
            throw LocalUnixNative.Error("openat exclusive file", error);
        }
        var created = CreateHandle(descriptor, Path.Combine(parent.FullPath, name), requireDirectory: false);
        LocalUnixNative.Sync(parent.Handle.DangerousGetHandle().ToInt32());
        return created;
    }

    public void Flush(LocalSecureHandle handle)
        => LocalUnixNative.Sync(handle.Handle.DangerousGetHandle().ToInt32());

    public bool IsUnlinked(LocalSecureHandle handle)
    {
        var linkCount = LocalUnixNative.ReadMetadata(handle.Handle.DangerousGetHandle().ToInt32()).LinkCount;
        return linkCount == 0
               || (OperatingSystem.IsMacOS()
                   && handle.Kind == LocalSecureNodeKind.Directory
                   && linkCount == 1);
    }

    public IEnumerable<string> EnumerateNames(LocalSecureHandle directory)
    {
        RequireDirectory(directory);
        return LocalUnixNative.EnumerateNames(directory.Handle.DangerousGetHandle().ToInt32());
    }

    public LocalSecureMutationReservation ReserveMutationSlots(
        LocalSecureHandle parent,
        int slotCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (slotCount < 0 || slotCount > HostSecurePathEngine.MaxQuarantineEntriesPerDirectory)
        {
            throw new LocalSecurePathException("A secure mutation requested an invalid quarantine reservation.");
        }

        IDisposable? coordinatorLease = HostMutationCoordinator.Enter(parent.Identity, cancellationToken);
        try
        {
            var scanned = 0;
            var reserved = 0;
            foreach (var name in EnumerateNames(parent))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++scanned > HostSecurePathEngine.MaxQuarantineAccountingEntries)
                {
                    throw new LocalSecurePathException(
                        $"Secure mutation quarantine accounting exceeds the {HostSecurePathEngine.MaxQuarantineAccountingEntries}-entry scan limit.");
                }
                if (HostSecurePathEngine.IsReservedName(name))
                {
                    reserved++;
                }
            }
            if (reserved > HostSecurePathEngine.MaxQuarantineEntriesPerDirectory - slotCount)
            {
                throw new LocalSecurePathException(
                    $"Secure mutation quarantine exceeds {HostSecurePathEngine.MaxQuarantineEntriesPerDirectory} retained entries.");
            }

            var reservation = new LocalSecureMutationReservation(
                Enumerable.Range(0, slotCount)
                    .Select(static _ => HostSecurePathEngine.CreateQuarantineName())
                    .ToArray(),
                coordinatorLease);
            coordinatorLease = null;
            return reservation;
        }
        finally
        {
            coordinatorLease?.Dispose();
        }
    }

    public void DeleteEntry(
        LocalSecureHandle parent,
        string name,
        bool directory,
        LocalSecureHandle expectedTarget,
        Func<LocalSecureHandle, bool>? finalTargetValidator,
        LocalSecureMutationReservation reservation,
        Action beforeMutationSyscall,
        Action afterQuarantine,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        beforeMutationSyscall();
        cancellationToken.ThrowIfCancellationRequested();
        using var quarantine = BeginQuarantine(
            parent,
            name,
            expectedTarget.Identity,
            directory,
            reservation);
        try
        {
            afterQuarantine();
            if (finalTargetValidator is not null && !finalTargetValidator(quarantine.Handle))
            {
                throw new LocalSecureContentChangedException(Path.Combine(parent.FullPath, name));
            }
            quarantine.Commit();
        }
        catch (Exception operationException)
        {
            try
            {
                quarantine.Rollback();
            }
            catch (Exception recoveryException)
            {
                quarantine.Abandon();
                throw new LocalSecureMutationRecoveryException(
                    Path.Combine(parent.FullPath, name),
                    new AggregateException(operationException, recoveryException));
            }
            throw;
        }
    }

    private static LocalSecureHandle CreateHandle(int descriptor, string path, bool requireDirectory)
    {
        var safeHandle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        try
        {
            var metadata = LocalUnixNative.ReadMetadata(descriptor);
            var kind = LocalUnixNative.GetNodeKind(metadata, path);
            if (requireDirectory && kind != LocalSecureNodeKind.Directory)
            {
                throw new LocalSecurePathException($"Path is not a directory: {path}");
            }
            if (kind == LocalSecureNodeKind.RegularFile && metadata.LinkCount != 1)
            {
                throw new LocalSecurePathException($"Hard-linked regular files are not supported by strict structured operations: {path}");
            }
            return new LocalSecureHandle(
                safeHandle,
                path,
                kind,
                new LocalSecureIdentity(metadata.Device, metadata.Inode),
                metadata.Length,
                checked((uint)metadata.LinkCount),
                metadata.Mode,
                metadata.UserId,
                metadata.GroupId,
                () => new SafeFileHandle(new IntPtr(LocalUnixNative.Duplicate(descriptor)), ownsHandle: true));
        }
        catch
        {
            safeHandle.Dispose();
            throw;
        }
    }

    private LocalSecureQuarantineTransaction BeginQuarantine(
        LocalSecureHandle parent,
        string name,
        LocalSecureIdentity expectedIdentity,
        bool directory,
        LocalSecureMutationReservation reservation,
        bool writable = false)
    {
        var parentDescriptor = parent.Handle.DangerousGetHandle().ToInt32();
        var quarantineName = reservation.TakeQuarantineName();
        LocalUnixNative.RenameNoReplace(parentDescriptor, name, parentDescriptor, quarantineName);
        LocalSecureHandle? current = null;
        LocalSecureQuarantineTransaction? transaction = null;
        try
        {
            current = OpenChild(
                parent,
                quarantineName,
                parent.Identity.Volume,
                requireDirectory: directory,
                writable);
            transaction = new LocalSecureQuarantineTransaction(
                this,
                parent,
                name,
                quarantineName,
                current,
                reservation);
            current = null;
            LocalUnixNative.Sync(parentDescriptor);
            if (transaction.Handle.Identity != expectedIdentity
                || transaction.Handle.Kind != (directory ? LocalSecureNodeKind.Directory : LocalSecureNodeKind.RegularFile))
            {
                throw new LocalSecureApprovalChangedException(Path.Combine(parent.FullPath, name));
            }
            return transaction;
        }
        catch (Exception operationException)
        {
            current?.Dispose();
            try
            {
                if (transaction is not null)
                {
                    transaction.Rollback();
                }
                else
                {
                    RestoreQuarantineName(parent, name, quarantineName, reservation);
                }
            }
            catch (Exception recoveryException)
            {
                transaction?.Abandon();
                throw new LocalSecureMutationRecoveryException(
                    Path.Combine(parent.FullPath, name),
                    new AggregateException(operationException, recoveryException));
            }
            finally
            {
                transaction?.Dispose();
            }
            throw;
        }
    }

    private void RestoreQuarantineName(
        LocalSecureHandle parent,
        string targetName,
        string quarantineName,
        LocalSecureMutationReservation reservation)
    {
        var parentDescriptor = parent.Handle.DangerousGetHandle().ToInt32();
        try
        {
            try
            {
                LocalUnixNative.RenameNoReplace(parentDescriptor, quarantineName, parentDescriptor, targetName);
            }
            catch (LocalSecureFileAlreadyExistsException)
            {
                var conflictName = reservation.TakeQuarantineName();
                LocalUnixNative.RenameNoReplace(parentDescriptor, targetName, parentDescriptor, conflictName);
                LocalUnixNative.RenameNoReplace(parentDescriptor, quarantineName, parentDescriptor, targetName);
            }
            using var restored = OpenChild(parent, targetName, parent.Identity.Volume, requireDirectory: false);
            LocalUnixNative.Sync(parentDescriptor);
        }
        catch (LocalSecureMutationRecoveryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LocalSecureMutationRecoveryException(Path.Combine(parent.FullPath, targetName), ex);
        }
    }

    private sealed class LocalSecureQuarantineTransaction(
        LocalUnixFileSystemPlatform owner,
        LocalSecureHandle parent,
        string targetName,
        string quarantineName,
        LocalSecureHandle handle,
        LocalSecureMutationReservation reservation) : IDisposable
    {
        private int _state;

        public LocalSecureHandle Handle { get; } = handle;

        public void Commit()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                throw new InvalidOperationException("The secure quarantine transaction is already complete.");
            }
        }

        public void Rollback()
        {
            if (Volatile.Read(ref _state) == 2)
            {
                return;
            }
            if (Volatile.Read(ref _state) == 1)
            {
                throw new InvalidOperationException("A committed secure quarantine transaction cannot be rolled back.");
            }

            try
            {
                owner.RestoreQuarantineName(parent, targetName, quarantineName, reservation);
                using var restored = owner.OpenChild(
                    parent,
                    targetName,
                    parent.Identity.Volume,
                    requireDirectory: Handle.Kind == LocalSecureNodeKind.Directory);
                if (restored.Identity != Handle.Identity || restored.Kind != Handle.Kind)
                {
                    throw new LocalSecureApprovalChangedException(Path.Combine(parent.FullPath, targetName));
                }
                LocalUnixNative.Sync(restored.Handle.DangerousGetHandle().ToInt32());
                LocalUnixNative.Sync(parent.Handle.DangerousGetHandle().ToInt32());
                Interlocked.Exchange(ref _state, 2);
            }
            catch (LocalSecureMutationRecoveryException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new LocalSecureMutationRecoveryException(
                    Path.Combine(parent.FullPath, targetName),
                    ex);
            }
        }

        public void Abandon()
        {
            if (Volatile.Read(ref _state) == 0)
            {
                Interlocked.Exchange(ref _state, 3);
            }
        }

        public void Dispose()
        {
            try
            {
                if (Volatile.Read(ref _state) == 0)
                {
                    Rollback();
                }
            }
            finally
            {
                Handle.Dispose();
            }
        }
    }

    private static void RequireDirectory(LocalSecureHandle handle)
    {
        if (handle.Kind != LocalSecureNodeKind.Directory)
        {
            throw new LocalSecurePathException($"Path is not a directory: {handle.FullPath}");
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name)
            || name is "." or ".."
            || name.Contains('/')
            || name.Contains('\0'))
        {
            throw new LocalSecurePathException("A secure filesystem path contained an invalid child name.");
        }
    }
}
