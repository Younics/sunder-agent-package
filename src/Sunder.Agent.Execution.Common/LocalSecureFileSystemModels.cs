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
    Func<SafeFileHandle> duplicateHandle,
    bool caseSensitiveDirectory = false) : IDisposable
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

    public SafeFileHandle DuplicateHandle() => duplicateHandle();

    public void Dispose() => Handle.Dispose();
}

internal sealed class LocalSecureTemporaryFile(
    string name,
    string fullPath,
    SafeFileHandle handle,
    LocalSecureIdentity identity,
    bool hasDirectoryEntry,
    Func<SafeFileHandle> duplicateHandle) : IDisposable
{
    private LocalSecureHandle? _publishedTarget;

    public string Name { get; } = name;

    public string FullPath { get; } = fullPath;

    public SafeFileHandle Handle { get; } = handle;

    public LocalSecureIdentity Identity { get; } = identity;

    public bool HasDirectoryEntry { get; private set; } = hasDirectoryEntry;

    public SafeFileHandle DuplicateHandle() => duplicateHandle();

    public bool WasPublished { get; set; }

    public void MarkDirectoryEntryRemoved() => HasDirectoryEntry = false;

    public void RetainPublishedTarget(LocalSecureHandle target)
    {
        if (Interlocked.CompareExchange(ref _publishedTarget, target, null) is not null)
        {
            target.Dispose();
            throw new InvalidOperationException("The published target was already retained.");
        }
    }

    public LocalSecureHandle TakePublishedTarget()
        => Interlocked.Exchange(ref _publishedTarget, null)
           ?? throw new LocalSecureApprovalChangedException(FullPath);

    public void Dispose()
    {
        Interlocked.Exchange(ref _publishedTarget, null)?.Dispose();
        Handle.Dispose();
    }
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

    void CreateDirectory(LocalSecureHandle parent, string name);

    LocalSecureHandle CreateExclusiveFile(
        LocalSecureHandle parent,
        string name,
        uint unixMode);

    void Flush(LocalSecureHandle handle);

    bool IsUnlinked(LocalSecureHandle handle);

    IEnumerable<string> EnumerateNames(LocalSecureHandle directory);

    LocalSecureMutationReservation ReserveMutationSlots(
        LocalSecureHandle parent,
        int slotCount,
        CancellationToken cancellationToken);

    LocalSecureTemporaryFile CreateTemporaryFile(
        LocalSecureHandle parent,
        string name,
        LocalSecureMutationReservation reservation);

    void PublishTemporaryFile(
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

    void DeleteTemporaryFile(
        LocalSecureHandle parent,
        LocalSecureTemporaryFile temporaryFile,
        LocalSecureMutationReservation reservation);
}

internal readonly record struct LocalSecureTargetExpectation(
    bool Exists,
    LocalSecureIdentity? Identity,
    bool ApprovalBound)
{
    public static LocalSecureTargetExpectation Missing(bool approvalBound)
        => new(Exists: false, Identity: null, approvalBound);

    public static LocalSecureTargetExpectation Existing(LocalSecureIdentity identity, bool approvalBound)
        => new(Exists: true, identity, approvalBound);
}

internal interface ILocalSecureFileSystemHooks
{
    void OnCheckpoint(LocalSecureFileSystemCheckpoint checkpoint, string parentPath, string? entryName);
}

internal enum LocalSecureFileSystemCheckpoint
{
    BeforeOpen,
    BeforePublish,
    BeforePublishSyscall,
    AfterQuarantineBeforeMetadata,
    BeforePostQuarantineSync,
    AfterValidationBeforePublish,
    AfterNamedTemporaryCopyChunk,
    BeforeNamedTemporaryPublish,
    AfterPublishedFileSync,
    AfterPublishedParentSync,
    BeforePostPublicationRestore,
    BeforeUnlink,
    BeforeUnlinkSyscall,
    AfterValidationBeforeDelete,
    AfterDeleteBeforeAuthorityCapture,
}

internal class LocalSecurePathException(string message, Exception? innerException = null)
    : IOException(message, innerException);

internal sealed class LocalSecurePathNotFoundException(string path)
    : LocalSecurePathException($"Path does not exist: {path}");

internal sealed class LocalSecureFileAlreadyExistsException(string path)
    : LocalSecurePathException($"File already exists: {path}");

internal sealed class LocalSecureApprovalChangedException(string path)
    : LocalSecurePathException($"The approved resource identity changed before access: {path}");

internal sealed class LocalSecureContentChangedException(string path)
    : LocalSecurePathException($"The file content changed before mutation: {path}");

internal sealed class LocalSecureMutationRecoveryException(string path, Exception innerException)
    : LocalSecurePathException(
        $"Strict filesystem mutation recovery could not be completed for: {path}",
        innerException);
