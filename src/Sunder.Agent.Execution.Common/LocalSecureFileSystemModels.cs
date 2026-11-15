using Microsoft.Win32.SafeHandles;

namespace Sunder.Agent.Execution.Common;

internal enum LocalSecureNodeKind
{
    RegularFile,
    Directory,
}

internal readonly record struct LocalSecureIdentity(
    ulong Volume,
    ulong FileId,
    ulong FileIdHigh = 0)
{
    public override string ToString() => $"{Volume:x16}:{FileIdHigh:x16}:{FileId:x16}";
}

internal sealed record LocalResourceBinding(
    string FullPath,
    string AnchorPath,
    LocalSecureIdentity AnchorIdentity,
    LocalSecureIdentity? TargetIdentity,
    LocalSecureNodeKind? TargetKind,
    bool Exists,
    bool CaseSensitivePath = false,
    bool TargetIsAuthorityRoot = false);

internal sealed class LocalSecureHandle(
    SafeFileHandle handle,
    string fullPath,
    LocalSecureNodeKind kind,
    LocalSecureIdentity identity,
    long length,
    uint linkCount,
    uint mode,
    uint userId,
    uint groupId,
    Func<SafeFileHandle, SafeFileHandle> duplicateHandle,
    bool caseSensitiveDirectory = false,
    bool mutationExclusive = false,
    Func<SafeFileHandle, SafeFileHandle>? duplicateForTraversal = null) : IDisposable
{
    public SafeFileHandle Handle { get; } = handle;

    public string FullPath { get; } = fullPath;

    public LocalSecureNodeKind Kind { get; } = kind;

    public LocalSecureIdentity Identity { get; } = identity;

    public long Length { get; } = length;

    public uint LinkCount { get; } = linkCount;

    public uint Mode { get; } = mode;

    public uint UserId { get; } = userId;

    public uint GroupId { get; } = groupId;

    public bool CaseSensitiveDirectory { get; } = caseSensitiveDirectory;

    // Windows mutation handles deny competing delete (and regular-file write)
    // opens. A retained approval handle is identity evidence, not that lock.
    public bool MutationExclusive { get; } = mutationExclusive;

    public SafeFileHandle DuplicateHandle() => duplicateHandle(Handle);

    public LocalSecureHandle Retain()
    {
        var copy = DuplicateHandle();
        return new LocalSecureHandle(copy, FullPath, Kind, Identity, Length, LinkCount, Mode, UserId, GroupId,
            duplicateHandle, CaseSensitiveDirectory, MutationExclusive, duplicateForTraversal);
    }

    internal LocalSecureHandle RetainForTraversal()
    {
        if (duplicateForTraversal is null)
            return Retain();

        var copy = duplicateForTraversal(Handle);
        return new LocalSecureHandle(copy, FullPath, Kind, Identity, Length, LinkCount, Mode, UserId, GroupId,
            duplicateHandle, CaseSensitiveDirectory, mutationExclusive: false);
    }

    public void Dispose() => Handle.Dispose();
}

internal sealed class LocalSecureMutationReservation(
    IReadOnlyList<string> quarantineNames,
    IDisposable? coordinatorLease = null) : IDisposable
{
    private int _nextName;
    private IDisposable? _coordinatorLease = coordinatorLease;

    public string TakeQuarantineName()
    {
        var index = _nextName++;
        return index < quarantineNames.Count
            ? quarantineNames[index]
            : throw new LocalSecurePathException("A secure mutation exceeded its reserved quarantine slots.");
    }

    public void Dispose() => Interlocked.Exchange(ref _coordinatorLease, null)?.Dispose();
}

internal interface ILocalSecureFileSystemPlatform
{
    LocalSecureHandle OpenSystemRoot(string pathRoot);

    LocalSecureHandle OpenChild(
        LocalSecureHandle parent,
        string name,
        ulong expectedVolume,
        bool requireDirectory,
        bool writable = false);

    bool TryOpenChild(
        LocalSecureHandle parent,
        string name,
        ulong expectedVolume,
        bool requireDirectory,
        out LocalSecureHandle? child,
        bool writable = false);

    LocalSecureHandle CreateDirectory(LocalSecureHandle parent, string name);

    LocalSecureHandle CreateExclusiveFile(
        LocalSecureHandle parent,
        string name,
        uint unixMode);

    // User files inherit ordinary creation policy. Internal challenge/staging
    // files continue to use the separate, explicitly private creation API above.
    LocalSecureHandle CreateUserFile(LocalSecureHandle parent, string name);

    // Opens without truncation, then checks the retained approved identity.
    // Content writes do not require delete or security-policy modification rights.
    LocalSecureHandle OpenFileForContentUpdate(
        LocalSecureHandle parent, string name, LocalSecureHandle expectedTarget);

    // Refresh metadata through the actual object, never by reopening its pathname.
    LocalSecureHandle RetainContentTarget(LocalSecureHandle target);

    void Flush(LocalSecureHandle handle);

    bool IsUnlinked(LocalSecureHandle handle);

    IEnumerable<string> EnumerateNames(LocalSecureHandle directory);

    LocalSecureMutationReservation ReserveMutationSlots(
        LocalSecureHandle parent,
        int slotCount,
        CancellationToken cancellationToken);

    void DeleteEntry(
        LocalSecureHandle parent,
        string name,
        bool directory,
        LocalSecureHandle expectedTarget,
        Func<LocalSecureHandle, bool>? finalTargetValidator,
        LocalSecureMutationReservation reservation,
        Action beforeMutationSyscall,
        Action afterQuarantine,
        CancellationToken cancellationToken);

}

internal interface ILocalSecureFileSystemHooks
{
    void OnCheckpoint(LocalSecureFileSystemCheckpoint checkpoint, string parentPath, string? entryName);
}

internal enum LocalSecureFileSystemCheckpoint
{
    BeforeOpen,
    BeforeUnlink,
    BeforeUnlinkSyscall,
    AfterValidationBeforeDelete,
    AfterDeleteBeforeAuthorityCapture,
    BeforeContentUpdateOpen,
    BeforeContentWrite,
    AfterContentWriteChunk,
    BeforeContentLength,
    AfterContentLength,
    AfterContentFileSync,
    AfterCreatedFileParentSync,
    BeforeContentReceipt,
}

internal class LocalSecurePathException(string message, Exception? innerException = null)
    : IOException(message, innerException);

internal sealed class LocalSecurePathNotFoundException(string path)
    : LocalSecurePathException($"Path does not exist: {path}");

internal sealed class LocalSecureSymbolicLinkException(string path)
    : LocalSecurePathException($"Symbolic links and reparse points are not followed by strict structured operations: {path}");

internal sealed class LocalSecureFileAlreadyExistsException(string path)
    : LocalSecurePathException($"File already exists: {path}");

internal sealed class LocalSecureApprovalChangedException(string path)
    : LocalSecurePathException($"The approved resource identity changed before access: {path}");

internal sealed class LocalSecureContentChangedException(string path)
    : LocalSecurePathException($"The file content changed before mutation: {path}");

internal sealed class LocalSecureIncompleteMutationException(string path, Exception innerException)
    : LocalSecurePathException(
        $"The file mutation did not complete; the exact target may remain empty or partially modified: {path}", innerException);

internal sealed class LocalSecureMutationRecoveryException(string path, Exception innerException)
    : LocalSecurePathException(
        $"Strict filesystem mutation recovery could not be completed for: {path}",
        innerException);
