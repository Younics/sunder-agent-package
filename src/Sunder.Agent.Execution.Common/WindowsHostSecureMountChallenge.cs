using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sunder.Agent.Execution.Common;

// The approved root and host-owned challenge directory stay pinned throughout.
// Disposable witnesses share DELETE only after all host creation/write work is
// finished. Container-created entries are observation handles, never adopted as
// host cleanup/mutation authority.
internal sealed class WindowsHostSecureMountChallenge : IHostSecureMountChallenge
{
    internal const string DeletionDirectoryName = "deletion-witness";
    internal const string DeletionFileName = "host-file";
    private readonly LocalSecureRoot _root;
    private readonly LocalSecureMutationReservation _reservation;
    private LocalSecureHandle? _directory, _hostChallenge;
    private SafeFileHandle? _deletionDirectory, _deletionFile, _responseDirectory, _responseFile;
    private bool _completed, _disposed;

    private WindowsHostSecureMountChallenge(LocalSecureRoot root, LocalSecureMutationReservation reservation)
    {
        _root = root;
        _reservation = reservation;
        DirectoryName = HostSecurePathEngine.CreateChallengeName();
    }

    public string DirectoryName { get; }
    public IReadOnlyList<string> ContainerFilesToRemove =>
        [HostSecureMountChallenge.ContainerDirectoryName + "/" + HostSecureMountChallenge.ContainerResponseFileName,
         DeletionDirectoryName + "/" + DeletionFileName];
    public IReadOnlyList<string> ContainerDirectoriesToRemove =>
        [HostSecureMountChallenge.ContainerDirectoryName, DeletionDirectoryName];

    internal static WindowsHostSecureMountChallenge Create(LocalSecureRoot root, string value, CancellationToken token)
    {
        var challenge = new WindowsHostSecureMountChallenge(root, root.Platform.ReserveMutationSlots(root.Handle, 2, token));
        try
        {
            token.ThrowIfCancellationRequested();
            var directory = challenge._directory = root.Platform.CreateDirectory(root.Handle, challenge.DirectoryName);
            var hostFile = challenge._hostChallenge = root.Platform.CreateExclusiveFile(directory, HostSecureMountChallenge.HostChallengeFileName, 0x180);
            RandomAccess.Write(hostFile.Handle, Encoding.ASCII.GetBytes(value), 0);
            root.Platform.Flush(hostFile);
            challenge._deletionDirectory = LocalWindowsNative.OpenRelative(directory.Handle, DeletionDirectoryName,
                LocalWindowsNative.GenericRead | LocalWindowsNative.GenericWrite | LocalWindowsNative.DeleteAccess,
                requireDirectory: true, create: true, directory.CaseSensitiveDirectory, directory.FullPath,
                LocalWindowsNative.ShareRead | LocalWindowsNative.ShareWrite);
            challenge.Validate(challenge._deletionDirectory, LocalSecureNodeKind.Directory);
            challenge._deletionFile = LocalWindowsNative.CreatePrivateFile(challenge._deletionDirectory, DeletionFileName,
                directory.CaseSensitiveDirectory, directory.FullPath, LocalWindowsNative.ShareRead | LocalWindowsNative.ShareWrite);
            challenge.Validate(challenge._deletionFile, LocalSecureNodeKind.RegularFile);
            RandomAccess.Write(challenge._deletionFile, Encoding.ASCII.GetBytes(value), 0);
            LocalWindowsNative.Flush(challenge._deletionFile, directory.FullPath);
            LocalWindowsNative.Flush(challenge._deletionDirectory, directory.FullPath);
            root.Platform.Flush(directory);
            // No descendant creation/write may follow the directory handoff.
            // Reopen these exact owned objects with sharing, never close and
            // reselect a name. Only the file retains write access for sanitization.
            challenge.ShareDeletionWitness(ref challenge._deletionFile, writable: true);
            challenge.ShareDeletionWitness(ref challenge._deletionDirectory, writable: false);
            token.ThrowIfCancellationRequested();
            return challenge;
        }
        catch (Exception operationError)
        {
            try { challenge.Dispose(); }
            catch (Exception recoveryError)
            { throw new LocalSecureMutationRecoveryException(root.Handle.FullPath, new AggregateException(operationError, recoveryError)); }
            throw;
        }
    }

    public void VerifyContainerResponse(string expectedResponse)
    {
        var directory = RequireDirectory();
        if (_responseDirectory is not null || expectedResponse.Length is < 32 or > 256 || !expectedResponse.All(Uri.IsHexDigit))
            throw new LocalSecurePathException("Invalid or repeated Docker mount identity response verification.");
        _responseDirectory = LocalWindowsNative.OpenRelative(directory.Handle, HostSecureMountChallenge.ContainerDirectoryName,
            LocalWindowsNative.GenericRead, requireDirectory: true, create: false, directory.CaseSensitiveDirectory,
            directory.FullPath, LocalWindowsNative.ShareAll);
        Validate(_responseDirectory, LocalSecureNodeKind.Directory);
        _responseFile = LocalWindowsNative.OpenRelative(_responseDirectory, HostSecureMountChallenge.ContainerResponseFileName,
            LocalWindowsNative.GenericRead, requireDirectory: false, create: false,
            LocalWindowsNative.IsCaseSensitiveDirectory(_responseDirectory, directory.FullPath), directory.FullPath, LocalWindowsNative.ShareAll);
        Validate(_responseFile, LocalSecureNodeKind.RegularFile);
        var length = RandomAccess.GetLength(_responseFile);
        if (length != expectedResponse.Length)
            throw new LocalSecurePathException("The Docker mount identity challenge response did not match its bound.");
        var bytes = new byte[expectedResponse.Length];
        using (var stream = new FileStream(LocalWindowsNative.Duplicate(_responseFile), FileAccess.Read))
            stream.ReadExactly(bytes);
        if (!string.Equals(Encoding.ASCII.GetString(bytes), expectedResponse, StringComparison.Ordinal))
            throw new LocalSecurePathException("The Docker mount identity challenge response did not match.");
    }

    public void VerifyContainerCleanup()
    {
        var directory = RequireDirectory();
        if (_responseDirectory is null || _responseFile is null || _deletionDirectory is null || _deletionFile is null)
            throw new LocalSecurePathException("The Docker mount identity response has not been verified.");
        foreach (var handle in new[] { _responseFile, _responseDirectory, _deletionFile, _deletionDirectory })
        {
            if (!LocalWindowsNative.IsUnlinked(handle, directory.FullPath))
                throw new LocalSecurePathException("The Docker daemon did not remove an exact mount identity deletion witness.");
        }
        // A shell exit code or missing pathname is not deletion evidence. Only
        // after all four retained objects are unlinked may the host clean its own
        // pinned objects, with independent retained observations of their unlink.
        _root.Platform.Flush(directory);
        ZeroOwned(_hostChallenge!.Handle, directory.FullPath);
        DeleteOwned(ref _hostChallenge);
        _root.Platform.Flush(directory);
        DeleteOwned(ref _directory);
        _root.Platform.Flush(_root.Handle);
        _completed = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var failures = new List<Exception>();
        try
        {
            if (!_completed)
            {
                try { if (_hostChallenge is not null) ZeroOwned(_hostChallenge.Handle, _root.Handle.FullPath); }
                catch (Exception error) { failures.Add(error); }
                try { if (_deletionFile is not null) ZeroOwned(_deletionFile, _root.Handle.FullPath); }
                catch (Exception error) { failures.Add(error); }
                // Retire probe children before moving our owned directory.
                // Windows can refuse an ancestor rename with live descendant
                // file objects. Never release the root or owned-directory pin.
                _hostChallenge?.Dispose();
                _hostChallenge = null;
                _deletionFile?.Dispose();
                _deletionFile = null;
                _deletionDirectory?.Dispose();
                _deletionDirectory = null;
                _responseFile?.Dispose();
                _responseFile = null;
                _responseDirectory?.Dispose();
                _responseDirectory = null;
                if (_directory is not null)
                {
                    try
                    {
                        _root.Platform.DeleteEntry(_root.Handle, DirectoryName, directory: true, _directory,
                            finalTargetValidator: null, _reservation, static () => { }, static () => { }, CancellationToken.None);
                    }
                    catch (Exception error) { failures.Add(error); }
                }
            }
        }
        finally
        {
            _responseFile?.Dispose();
            _responseDirectory?.Dispose();
            _deletionFile?.Dispose();
            _deletionDirectory?.Dispose();
            _hostChallenge?.Dispose();
            _directory?.Dispose();
            _reservation.Dispose();
        }
        if (failures.Count != 0)
            throw new LocalSecureMutationRecoveryException(_root.Handle.FullPath, new AggregateException(failures));
    }

    private LocalSecureHandle RequireDirectory()
    {
        ObjectDisposedException.ThrowIf(_disposed || _completed, this);
        return _directory ?? throw new LocalSecurePathException("The host mount identity challenge directory is unavailable.");
    }

    private void Validate(SafeFileHandle handle, LocalSecureNodeKind kind)
    {
        var metadata = LocalWindowsNative.ReadMetadata(handle, _root.Handle.FullPath);
        if (metadata.Volume != _root.Volume || metadata.Kind != kind || (kind == LocalSecureNodeKind.RegularFile && metadata.LinkCount != 1))
            throw new LocalSecurePathException("A Docker mount identity challenge entry changed kind, volume, or link policy.");
    }

    private void ShareDeletionWitness(ref SafeFileHandle? owned, bool writable)
    {
        var original = owned ?? throw new LocalSecurePathException("A mount identity deletion witness is unavailable.");
        var observation = LocalWindowsNative.Reopen(original,
            LocalWindowsNative.GenericRead | (writable ? LocalWindowsNative.GenericWrite : 0),
            LocalWindowsNative.ShareAll, _root.Handle.FullPath);
        owned = observation;
        original.Dispose();
    }

    private static void ZeroOwned(SafeFileHandle handle, string path)
    {
        if (LocalWindowsNative.IsUnlinked(handle, path)) return;
        using var stream = new FileStream(LocalWindowsNative.Duplicate(handle), FileAccess.Write);
        stream.SetLength(0);
        stream.Flush(flushToDisk: true);
        LocalWindowsNative.Flush(handle, path);
        if (RandomAccess.GetLength(handle) != 0)
            throw new LocalSecurePathException("A host-owned mount identity challenge could not be sanitized.");
    }

    private static void DeleteOwned(ref LocalSecureHandle? owned)
    {
        var handle = owned ?? throw new LocalSecurePathException("A host-owned mount identity challenge was already retired.");
        using var observation = LocalWindowsNative.Reopen(handle.Handle, LocalWindowsNative.GenericRead,
            LocalWindowsNative.ShareAll, handle.FullPath);
        LocalWindowsNative.Delete(handle.Handle, handle.FullPath);
        handle.Dispose();
        owned = null;
        if (!LocalWindowsNative.IsUnlinked(observation, handle.FullPath))
            throw new LocalSecurePathException("A host-owned mount identity challenge was not verifiably unlinked.");
    }
}
