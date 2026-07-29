namespace Sunder.Agent.Execution.Common;

internal sealed class LocalWindowsFileSystemPlatform : ILocalSecureFileSystemPlatform
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
        if (writable)
        {
            desiredAccess |= LocalWindowsNative.DeleteAccess;
        }
        var handle = LocalWindowsNative.OpenRelative(
            parent.Handle,
            name,
            desiredAccess,
            requireDirectory,
            create: false,
            caseSensitive: LocalWindowsNative.IsCaseSensitiveDirectory(parent.Handle, parent.FullPath),
            path);
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

    public void CreateDirectory(LocalSecureHandle parent, string name)
        => throw MutationUnavailable();

    public LocalSecureHandle CreateExclusiveFile(
        LocalSecureHandle parent,
        string name,
        uint unixMode)
        => throw MutationUnavailable();

    public void Flush(LocalSecureHandle handle)
        => throw MutationUnavailable();

    public bool IsUnlinked(LocalSecureHandle handle)
        => throw MutationUnavailable();

    public IEnumerable<string> EnumerateNames(LocalSecureHandle directory)
    {
        RequireDirectory(directory);
        return LocalWindowsNative.EnumerateNames(directory.Handle, directory.FullPath);
    }

    public LocalSecureMutationReservation ReserveMutationSlots(
        LocalSecureHandle parent,
        int slotCount,
        CancellationToken cancellationToken)
        => throw MutationUnavailable();

    public LocalSecureTemporaryFile CreateTemporaryFile(
        LocalSecureHandle parent,
        string name,
        LocalSecureMutationReservation reservation)
        => throw MutationUnavailable();

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
        => throw MutationUnavailable();

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
        => throw MutationUnavailable();

    public void DeleteTemporaryFile(
        LocalSecureHandle parent,
        LocalSecureTemporaryFile temporaryFile,
        LocalSecureMutationReservation reservation)
        => throw MutationUnavailable();

    private static LocalSecureHandle OpenPath(string path, bool requireDirectory, bool writable)
    {
        var desiredAccess = LocalWindowsNative.GenericRead
                            | LocalWindowsNative.ReadAttributes
                            | LocalWindowsNative.ReadControl;
        if (writable)
        {
            desiredAccess |= LocalWindowsNative.DeleteAccess;
        }
        var handle = LocalWindowsNative.OpenHandle(path, desiredAccess, LocalWindowsNative.OpenExisting);
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
        bool requireDirectory)
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
                                    && LocalWindowsNative.IsCaseSensitiveDirectory(handle, path));
    }

    private static PlatformNotSupportedException MutationUnavailable()
        => new(FileOperation.StrictPlatformMutationUnavailableMessage);

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
