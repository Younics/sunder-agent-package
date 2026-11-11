namespace Sunder.Agent.Execution.Common;

internal sealed partial class LocalWindowsFileSystemPlatform : ILocalSecureFileSystemPlatform
{
    public LocalSecureHandle OpenSystemRoot(string pathRoot)
    {
        if (pathRoot.Length != 3
            || !char.IsAsciiLetter(pathRoot[0])
            || pathRoot[1] != ':'
            || pathRoot[2] != Path.DirectorySeparatorChar)
        {
            throw new PlatformNotSupportedException(
                $"Strict Local Windows filesystem operations require a local drive root, not '{pathRoot}'.");
        }
        return OpenPath(pathRoot, requireDirectory: true, writable: false);
    }

    public LocalSecureHandle OpenChild(
        LocalSecureHandle parent,
        string name,
        ulong expectedVolume,
        bool requireDirectory,
        bool writable = false)
    {
        ValidateName(name);
        var path = Path.Combine(parent.FullPath, name);
        var desiredAccess = LocalWindowsNative.GenericRead
                            | LocalWindowsNative.ReadAttributes
                            | LocalWindowsNative.ReadControl;
        var handle = LocalWindowsNative.OpenRelative(
            parent.Handle,
            name,
            desiredAccess,
            requireDirectory,
            create: false,
            caseSensitive: LocalWindowsNative.IsCaseSensitiveDirectory(parent.Handle, parent.FullPath),
            path,
            // Approval handles retain identity. Mutation access is acquired with
            // a separate exclusive handle and checked against that identity.
            requireDirectory
                ? LocalWindowsNative.ShareRead | LocalWindowsNative.ShareWrite
                : LocalWindowsNative.ShareAll);
        LocalSecureHandle child;
        try
        {
            child = CreateHandle(handle, path, requireDirectory);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
        if (child.Identity.Volume != expectedVolume)
        {
            child.Dispose();
            throw new LocalSecurePathException($"Path crosses a Windows volume boundary: {path}");
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

    public LocalSecureHandle CreateDirectory(LocalSecureHandle parent, string name)
    {
        ValidateName(name);
        RequireDirectory(parent);
        var path = Path.Combine(parent.FullPath, name);
        var handle = LocalWindowsNative.OpenRelative(parent.Handle, name,
            LocalWindowsNative.GenericRead | LocalWindowsNative.GenericWrite | LocalWindowsNative.DeleteAccess,
            requireDirectory: true, create: true, parent.CaseSensitiveDirectory, path,
            LocalWindowsNative.ShareRead | LocalWindowsNative.ShareWrite);
        LocalSecureHandle? created = null;
        try
        {
            created = CreateHandle(handle, path, requireDirectory: true, mutationExclusive: true);
            RequireVolume(parent, created);
            Flush(created);
            Flush(parent);
            var read = LocalWindowsNative.Reopen(handle, LocalWindowsNative.GenericRead | LocalWindowsNative.ReadControl,
                LocalWindowsNative.ShareAll, path);
            try
            {
                var retained = CreateHandle(read, path, requireDirectory: true);
                RequireIdentity(retained, LocalWindowsNative.ReadMetadata(handle, path));
                return retained;
            }
            catch { read.Dispose(); throw; }
        }
        catch (Exception operationException)
        {
            try
            {
                // Only our creation handle is authority for failed-create cleanup.
                LocalWindowsNative.Delete(handle, path);
                if (created is null) handle.Dispose(); else created.Dispose();
                Flush(parent);
            }
            catch (Exception recoveryException)
            {
                throw new LocalSecureMutationRecoveryException(path, new AggregateException(operationException, recoveryException));
            }
            throw;
        }
        finally { if (created is null) handle.Dispose(); else created.Dispose(); }
    }

    public LocalSecureHandle CreateExclusiveFile(
        LocalSecureHandle parent,
        string name,
        uint unixMode)
    {
        ValidateName(name);
        RequireDirectory(parent);
        var path = Path.Combine(parent.FullPath, name);
        var handle = LocalWindowsNative.CreatePrivateFile(parent.Handle, name, parent.CaseSensitiveDirectory, path);
        try
        {
            var created = CreateHandle(handle, path, requireDirectory: false, mutationExclusive: true);
            RequireVolume(parent, created);
            return created;
        }
        catch (Exception operationException)
        {
            // No bytes have been written, and this is still the exclusive created
            // file handle. Never reopen its name to infer cleanup authority.
            try
            {
                LocalWindowsNative.Delete(handle, path);
                handle.Dispose();
                Flush(parent);
            }
            catch (Exception recoveryException)
            {
                throw new LocalSecureMutationRecoveryException(path, new AggregateException(operationException, recoveryException));
            }
            finally { handle.Dispose(); }
            throw;
        }
    }

    public void Flush(LocalSecureHandle handle)
    {
        if (handle.Kind == LocalSecureNodeKind.RegularFile)
        {
            LocalWindowsNative.Flush(handle.Handle, handle.FullPath);
            return;
        }
        // ReOpenFile changes access on this exact object, not on FullPath. A
        // directory read/approval handle need not carry write permissions.
        using var writable = LocalWindowsNative.Reopen(handle.Handle,
            LocalWindowsNative.GenericWrite | LocalWindowsNative.ReadAttributes,
            LocalWindowsNative.ShareAll, handle.FullPath);
        RequireIdentity(handle, LocalWindowsNative.ReadMetadata(writable, handle.FullPath));
        LocalWindowsNative.Flush(writable, handle.FullPath);
    }

    public bool IsUnlinked(LocalSecureHandle handle)
        => LocalWindowsNative.IsUnlinked(handle.Handle, handle.FullPath);

    public IEnumerable<string> EnumerateNames(LocalSecureHandle directory)
    {
        RequireDirectory(directory);
        return LocalWindowsNative.EnumerateNames(directory.Handle, directory.FullPath);
    }

    private static LocalSecureHandle OpenPath(string path, bool requireDirectory, bool writable)
    {
        var desiredAccess = LocalWindowsNative.GenericRead
                            | LocalWindowsNative.ReadAttributes
                            | LocalWindowsNative.ReadControl;
        if (writable)
        {
            desiredAccess |= LocalWindowsNative.DeleteAccess;
        }
        var handle = LocalWindowsNative.OpenHandle(path, desiredAccess, LocalWindowsNative.OpenExisting,
            LocalWindowsNative.ShareRead | LocalWindowsNative.ShareWrite);
        if (handle.IsInvalid)
        {
            var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error is 2 or 3)
            {
                throw new LocalSecurePathNotFoundException(path);
            }
            throw LocalWindowsNative.Error("CreateFileW", path, error);
        }

        try
        {
            return CreateHandle(handle, path, requireDirectory);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static LocalSecureHandle CreateHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        string path,
        bool requireDirectory,
        bool mutationExclusive = false)
    {
        var metadata = LocalWindowsNative.ReadMetadata(handle, path);
        if (requireDirectory && metadata.Kind != LocalSecureNodeKind.Directory)
        {
            throw new LocalSecurePathException($"Path is not a directory: {path}");
        }
        if (metadata.Kind == LocalSecureNodeKind.RegularFile && metadata.LinkCount != 1)
        {
            throw new LocalSecurePathException($"Hard-linked regular files are not supported by strict structured operations: {path}");
        }
        return new LocalSecureHandle(
            handle,
            path,
            metadata.Kind,
            new LocalSecureIdentity(metadata.Volume, metadata.FileId, metadata.FileIdHigh),
            metadata.Length,
            metadata.LinkCount,
            mode: 0,
            userId: 0,
            groupId: 0,
            () => LocalWindowsNative.Duplicate(handle),
            caseSensitiveDirectory: metadata.Kind == LocalSecureNodeKind.Directory
                                    && LocalWindowsNative.IsCaseSensitiveDirectory(handle, path),
            mutationExclusive);
    }

    private static void RequireVolume(LocalSecureHandle parent, LocalSecureHandle child)
    {
        if (child.Identity.Volume != parent.Identity.Volume)
            throw new LocalSecurePathException($"Path crosses a Windows volume boundary: {child.FullPath}");
    }

    private static void RequireIdentity(LocalSecureHandle expected, WindowsMetadata actual)
    {
        if (new LocalSecureIdentity(actual.Volume, actual.FileId, actual.FileIdHigh) != expected.Identity
            || actual.Kind != expected.Kind
            || (actual.Kind == LocalSecureNodeKind.RegularFile && actual.LinkCount != 1))
            throw new LocalSecureApprovalChangedException(expected.FullPath);
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
            || name.IndexOfAny(['/', '\\', '\0', ':']) >= 0)
        {
            throw new LocalSecurePathException("A secure Windows filesystem path contained an invalid child name.");
        }
    }
}
