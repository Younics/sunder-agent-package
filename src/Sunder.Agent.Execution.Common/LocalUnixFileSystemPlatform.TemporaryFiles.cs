using Microsoft.Win32.SafeHandles;

namespace Sunder.Agent.Execution.Common;

internal sealed partial class LocalUnixFileSystemPlatform
{
    public LocalSecureTemporaryFile CreateTemporaryFile(
        LocalSecureHandle parent,
        string name,
        LocalSecureMutationReservation reservation)
        => CreateTemporaryFile(parent, name, reservation, preferAnonymous: OperatingSystem.IsLinux());

    private LocalSecureTemporaryFile CreateTemporaryFile(
        LocalSecureHandle parent,
        string name,
        LocalSecureMutationReservation reservation,
        bool preferAnonymous)
    {
        ValidateName(name);
        var anonymous = preferAnonymous;
        var descriptor = LocalUnixNative.OpenAt(
            parent.Handle.DangerousGetHandle().ToInt32(),
            anonymous ? "." : name,
            LocalUnixNative.ReadWrite
            | LocalUnixNative.CloseOnExecFlag
             | (anonymous
                 ? LocalUnixNative.TemporaryFileFlag
                 : LocalUnixNative.CreateFlag | LocalUnixNative.ExclusiveFlag | LocalUnixNative.NoFollowFlag),
            Convert.ToUInt32("600", 8),
            invalidArgumentIsOperationError: anonymous);
        if (descriptor < 0)
        {
            var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            if (anonymous && CanFallBackToNamedTemporaryFile(error))
            {
                anonymous = false;
                descriptor = LocalUnixNative.OpenAt(
                    parent.Handle.DangerousGetHandle().ToInt32(),
                    name,
                    LocalUnixNative.ReadWrite
                    | LocalUnixNative.CloseOnExecFlag
                    | LocalUnixNative.CreateFlag
                    | LocalUnixNative.ExclusiveFlag
                    | LocalUnixNative.NoFollowFlag,
                    Convert.ToUInt32("600", 8));
                if (descriptor >= 0)
                {
                    error = 0;
                }
                else
                {
                    error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                }
            }
            if (descriptor < 0 && !anonymous && error == 17)
            {
                throw new LocalSecureFileAlreadyExistsException(Path.Combine(parent.FullPath, name));
            }
            if (descriptor < 0)
            {
                throw LocalUnixNative.Error("openat temporary file", error);
            }
        }

        var path = Path.Combine(parent.FullPath, name);
        LocalSecureIdentity? createdIdentity = null;
        try
        {
            var metadata = LocalUnixNative.ReadMetadata(descriptor);
            if (LocalUnixNative.GetNodeKind(metadata, path) != LocalSecureNodeKind.RegularFile
                || metadata.Device != parent.Identity.Volume
                || metadata.LinkCount != (anonymous ? 0UL : 1UL))
            {
                throw new LocalSecurePathException($"The temporary write target is not a regular file on the expected device: {path}");
            }
            createdIdentity = new LocalSecureIdentity(metadata.Device, metadata.Inode);
            LocalUnixNative.SetMode(descriptor, Convert.ToUInt32("600", 8));
            return new LocalSecureTemporaryFile(
                name,
                path,
                new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true),
                createdIdentity.Value,
                hasDirectoryEntry: !anonymous,
                () => new SafeFileHandle(new IntPtr(LocalUnixNative.Duplicate(descriptor)), ownsHandle: true));
        }
        catch (Exception operationException)
        {
            try
            {
                if (!anonymous && createdIdentity is not null)
                {
                    using var quarantine = BeginQuarantine(
                        parent,
                        name,
                        createdIdentity.Value,
                        directory: false,
                        reservation,
                        writable: true);
                    SanitizeAndCommitTemporaryFile(quarantine);
                }
            }
            catch (Exception recoveryException)
            {
                throw new LocalSecureMutationRecoveryException(
                    path,
                    new AggregateException(operationException, recoveryException));
            }
            finally
            {
                new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true).Dispose();
            }
            throw;
        }
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
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTemporaryFile(parent, temporaryFile);
        if (expectation.Exists)
        {
            if (!overwrite
                || expectedTarget is null
                || expectation.Identity is null
                || expectedTarget.Identity != expectation.Identity)
            {
                throw new LocalSecureApprovalChangedException(Path.Combine(parent.FullPath, targetName));
            }
        }
        else if (expectedTarget is not null)
        {
            throw new LocalSecureApprovalChangedException(Path.Combine(parent.FullPath, targetName));
        }

        var parentDescriptor = parent.Handle.DangerousGetHandle().ToInt32();
        cancellationToken.ThrowIfCancellationRequested();
        beforeMutationSyscall();
        cancellationToken.ThrowIfCancellationRequested();
        LocalSecureQuarantineTransaction? quarantine = null;
        LocalSecureQuarantineTransaction? temporaryQuarantine = null;
        LocalSecureHandle? published = null;
        LocalSecureTemporaryFile? namedPublicationFallback = null;
        Exception? operationFailure = null;
        try
        {
            if (OperatingSystem.IsMacOS() && temporaryFile.HasDirectoryEntry)
            {
                temporaryQuarantine = BeginQuarantine(
                    parent,
                    temporaryFile.Name,
                    temporaryFile.Identity,
                    directory: false,
                    reservation,
                    writable: true);
            }
            if (expectation.Exists)
            {
                quarantine = BeginQuarantine(
                    parent,
                    targetName,
                    expectation.Identity!.Value,
                    directory: false,
                    reservation);
                if (quarantine.Handle.Identity != expectation.Identity
                    || quarantine.Handle.Kind != LocalSecureNodeKind.RegularFile)
                {
                    throw new LocalSecureApprovalChangedException(Path.Combine(parent.FullPath, targetName));
                }
                afterQuarantineBeforeMetadata();
                PreserveMetadata(temporaryFile, quarantine.Handle);
                beforePostQuarantineSync();
                LocalUnixNative.Sync(temporaryFile.Handle.DangerousGetHandle().ToInt32());
                if (finalTargetValidator is not null && !finalTargetValidator(quarantine.Handle))
                {
                    throw new LocalSecureContentChangedException(Path.Combine(parent.FullPath, targetName));
                }
            }
            else
            {
                if (TryOpenChild(parent, targetName, parent.Identity.Volume, requireDirectory: false, out var unexpected))
                {
                    unexpected!.Dispose();
                    throw expectation.ApprovalBound
                        ? new LocalSecureApprovalChangedException(Path.Combine(parent.FullPath, targetName))
                        : new LocalSecureFileAlreadyExistsException(Path.Combine(parent.FullPath, targetName));
                }
            }

            afterValidationBeforePublish();
            var publicationFile = temporaryFile;
            try
            {
                if (OperatingSystem.IsLinux() && temporaryFile.HasDirectoryEntry)
                {
                    beforeNamedTemporaryPublish();
                    PublishNamedTemporaryFileNoReplace(parent, temporaryFile, targetName);
                }
                else
                {
                    try
                    {
                        LocalUnixNative.PublishHandleNoReplace(
                            temporaryFile.Handle.DangerousGetHandle().ToInt32(),
                            parentDescriptor,
                            targetName);
                    }
                    catch (LocalUnixAnonymousPublishUnavailableException) when (OperatingSystem.IsLinux())
                    {
                        namedPublicationFallback = CreateTemporaryFile(
                            parent,
                            HostSecurePathEngine.CreateTemporaryName(),
                            reservation,
                            preferAnonymous: false);
                        CopyTemporaryFile(
                            temporaryFile,
                            namedPublicationFallback,
                            parent.Identity.Volume,
                            afterNamedTemporaryCopyChunk);
                        publicationFile = namedPublicationFallback;
                        beforeNamedTemporaryPublish();
                        PublishNamedTemporaryFileNoReplace(parent, publicationFile, targetName);
                    }
                }
            }
            catch (LocalSecureFileAlreadyExistsException) when (expectation.ApprovalBound)
            {
                throw new LocalSecureApprovalChangedException(Path.Combine(parent.FullPath, targetName));
            }
            publicationFile.WasPublished = true;
            temporaryFile.WasPublished = true;

            published = OpenChild(
                parent,
                targetName,
                parent.Identity.Volume,
                requireDirectory: false,
                writable: true);
            if (OperatingSystem.IsLinux() && published.Identity != publicationFile.Identity)
            {
                throw new LocalSecureApprovalChangedException(Path.Combine(parent.FullPath, targetName));
            }
            LocalUnixNative.Sync(published.Handle.DangerousGetHandle().ToInt32());
            afterPublishedFileSync();
            LocalUnixNative.Sync(parentDescriptor);
            afterPublishedParentSync();
            quarantine?.Commit();
            temporaryFile.RetainPublishedTarget(published);
            published = null;
        }
        catch (Exception operationException)
        {
            operationFailure = operationException;
            try
            {
                if (temporaryFile.WasPublished)
                {
                    if (published is null)
                    {
                        throw new LocalSecurePathException(
                            $"The published target could not be retained for recovery: {Path.Combine(parent.FullPath, targetName)}");
                    }
                    RollbackPublishedFile(
                        parent,
                        targetName,
                        published,
                        quarantine,
                        reservation,
                        beforePostPublicationRestore);
                }
                else
                {
                    quarantine?.Rollback();
                }
            }
            catch (Exception recoveryException)
            {
                quarantine?.Abandon();
                throw new LocalSecureMutationRecoveryException(
                    Path.Combine(parent.FullPath, targetName),
                    new AggregateException(operationException, recoveryException));
            }
            throw;
        }
        finally
        {
            Exception? cleanupFailure = null;
            if (published is not null)
            {
                try
                {
                    if (operationFailure is not null)
                    {
                        SanitizeTemporaryFile(published);
                    }
                }
                catch (Exception ex)
                {
                    cleanupFailure = ex;
                }
                finally
                {
                    published.Dispose();
                }
            }
            try
            {
                quarantine?.Dispose();
            }
            catch (Exception ex)
            {
                cleanupFailure = cleanupFailure is null
                    ? ex
                    : new AggregateException(cleanupFailure, ex);
            }
            if (temporaryQuarantine is not null)
            {
                try
                {
                    SanitizeAndCommitTemporaryFile(temporaryQuarantine);
                }
                catch (Exception ex)
                {
                    cleanupFailure = cleanupFailure is null
                        ? ex
                        : new AggregateException(cleanupFailure, ex);
                }
                try
                {
                    SanitizeTemporaryFile(temporaryFile);
                }
                catch (Exception ex)
                {
                    cleanupFailure = cleanupFailure is null
                        ? ex
                        : new AggregateException(cleanupFailure, ex);
                }
                finally
                {
                    temporaryFile.MarkDirectoryEntryRemoved();
                    temporaryQuarantine.Dispose();
                }
            }
            if (operationFailure is not null && temporaryFile.WasPublished)
            {
                try
                {
                    SanitizeTemporaryFile(temporaryFile);
                }
                catch (Exception ex)
                {
                    cleanupFailure = cleanupFailure is null
                        ? ex
                        : new AggregateException(cleanupFailure, ex);
                }
            }
            if (!temporaryFile.WasPublished && temporaryQuarantine is null)
            {
                try
                {
                    DeleteTemporaryFile(
                        parent,
                        temporaryFile,
                        reservation);
                }
                catch (Exception ex)
                {
                    cleanupFailure = cleanupFailure is null
                        ? ex
                        : new AggregateException(cleanupFailure, ex);
                }
            }
            if (namedPublicationFallback is not null)
            {
                try
                {
                    if (!namedPublicationFallback.WasPublished)
                    {
                        DeleteTemporaryFile(
                            parent,
                            namedPublicationFallback,
                            reservation);
                    }
                    else if (operationFailure is not null)
                    {
                        SanitizeTemporaryFile(namedPublicationFallback);
                    }
                }
                catch (Exception ex)
                {
                    cleanupFailure = cleanupFailure is null
                        ? ex
                        : new AggregateException(cleanupFailure, ex);
                }
                finally
                {
                    namedPublicationFallback.Dispose();
                }
            }
            if (cleanupFailure is not null)
            {
                throw new LocalSecureMutationRecoveryException(
                    Path.Combine(parent.FullPath, targetName),
                    operationFailure is null
                        ? cleanupFailure
                        : new AggregateException(operationFailure, cleanupFailure));
            }
        }
    }

    private void RollbackPublishedFile(
        LocalSecureHandle parent,
        string targetName,
        LocalSecureHandle published,
        LocalSecureQuarantineTransaction? original,
        LocalSecureMutationReservation reservation,
        Action beforeOriginalRestore)
    {
        using var failedPublication = BeginQuarantine(
            parent,
            targetName,
            published.Identity,
            directory: false,
            reservation,
            writable: true);
        SanitizeAndCommitTemporaryFile(failedPublication);
        if (original is not null)
        {
            beforeOriginalRestore();
            original.Rollback();
        }
        else
        {
            LocalUnixNative.Sync(parent.Handle.DangerousGetHandle().ToInt32());
        }
    }

    private static void SanitizeAndCommitTemporaryFile(LocalSecureQuarantineTransaction quarantine)
    {
        try
        {
            SanitizeTemporaryFile(quarantine.Handle);
            quarantine.Commit();
        }
        catch
        {
            quarantine.Abandon();
            throw;
        }
    }

    private static void SanitizeTemporaryFile(LocalSecureHandle temporaryFile)
    {
        if (temporaryFile.Kind != LocalSecureNodeKind.RegularFile)
        {
            throw new LocalSecurePathException("A retained temporary entry is not a regular file.");
        }

        using var stream = new FileStream(temporaryFile.DuplicateHandle(), FileAccess.Write);
        stream.SetLength(0);
        stream.Flush(flushToDisk: true);
        var descriptor = temporaryFile.Handle.DangerousGetHandle().ToInt32();
        LocalUnixNative.Sync(descriptor);
        if (LocalUnixNative.ReadMetadata(descriptor).Length != 0)
        {
            throw new LocalSecurePathException("A retained temporary entry could not be truncated.");
        }
    }

    private static void SanitizeTemporaryFile(LocalSecureTemporaryFile temporaryFile)
    {
        var descriptor = temporaryFile.Handle.DangerousGetHandle().ToInt32();
        var metadata = LocalUnixNative.ReadMetadata(descriptor);
        if (LocalUnixNative.GetNodeKind(metadata, temporaryFile.FullPath) != LocalSecureNodeKind.RegularFile
            || new LocalSecureIdentity(metadata.Device, metadata.Inode) != temporaryFile.Identity)
        {
            throw new LocalSecureApprovalChangedException(temporaryFile.FullPath);
        }

        using var stream = new FileStream(temporaryFile.DuplicateHandle(), FileAccess.Write);
        stream.SetLength(0);
        stream.Flush(flushToDisk: true);
        LocalUnixNative.Sync(descriptor);
        if (LocalUnixNative.ReadMetadata(descriptor).Length != 0)
        {
            throw new LocalSecurePathException("A retained temporary entry could not be truncated.");
        }
    }

    public void DeleteTemporaryFile(
        LocalSecureHandle parent,
        LocalSecureTemporaryFile temporaryFile,
        LocalSecureMutationReservation reservation)
    {
        ValidateName(temporaryFile.Name);
        var path = temporaryFile.FullPath;
        Exception? cleanupFailure = null;
        if (temporaryFile.HasDirectoryEntry)
        {
            try
            {
                using var quarantined = BeginQuarantine(
                    parent,
                    temporaryFile.Name,
                    temporaryFile.Identity,
                    directory: false,
                    reservation,
                    writable: true);
                SanitizeAndCommitTemporaryFile(quarantined);
                temporaryFile.MarkDirectoryEntryRemoved();
            }
            catch (LocalSecurePathNotFoundException)
            {
                temporaryFile.MarkDirectoryEntryRemoved();
            }
            catch (Exception ex)
            {
                cleanupFailure = cleanupFailure is null
                    ? ex
                    : new AggregateException(cleanupFailure, ex);
            }
        }

        try
        {
            SanitizeTemporaryFile(temporaryFile);
        }
        catch (Exception ex)
        {
            cleanupFailure = cleanupFailure is null
                ? ex
                : new AggregateException(cleanupFailure, ex);
        }

        if (cleanupFailure is LocalSecureMutationRecoveryException recoveryException)
        {
            throw recoveryException;
        }
        if (cleanupFailure is not null)
        {
            throw new LocalSecureMutationRecoveryException(path, cleanupFailure);
        }
    }

    private void PublishNamedTemporaryFileNoReplace(
        LocalSecureHandle parent,
        LocalSecureTemporaryFile temporaryFile,
        string targetName)
    {
        using var namedEntry = OpenChild(
            parent,
            temporaryFile.Name,
            parent.Identity.Volume,
            requireDirectory: false);
        if (namedEntry.Identity != temporaryFile.Identity
            || namedEntry.Kind != LocalSecureNodeKind.RegularFile)
        {
            throw new LocalSecureApprovalChangedException(temporaryFile.FullPath);
        }

        var parentDescriptor = parent.Handle.DangerousGetHandle().ToInt32();
        LocalUnixNative.RenameNoReplace(
            parentDescriptor,
            temporaryFile.Name,
            parentDescriptor,
            targetName);
    }

    private static void CopyTemporaryFile(
        LocalSecureTemporaryFile source,
        LocalSecureTemporaryFile destination,
        ulong expectedVolume,
        Action afterCopyChunk)
    {
        var sourceMetadata = LocalUnixNative.ReadMetadata(source.Handle.DangerousGetHandle().ToInt32());
        if (LocalUnixNative.GetNodeKind(sourceMetadata, source.FullPath) != LocalSecureNodeKind.RegularFile
            || sourceMetadata.Device != expectedVolume)
        {
            throw new LocalSecureApprovalChangedException(source.FullPath);
        }

        var buffer = new byte[64 * 1024];
        long offset = 0;
        while (offset < sourceMetadata.Length)
        {
            var count = RandomAccess.Read(
                source.Handle,
                buffer.AsSpan(0, (int)Math.Min(buffer.Length, sourceMetadata.Length - offset)),
                offset);
            if (count == 0)
            {
                throw new LocalSecurePathException("The anonymous temporary file changed while preparing its named fallback.");
            }
            RandomAccess.Write(destination.Handle, buffer.AsSpan(0, count), offset);
            afterCopyChunk();
            offset += count;
        }

        var destinationDescriptor = destination.Handle.DangerousGetHandle().ToInt32();
        var destinationMetadata = LocalUnixNative.ReadMetadata(destinationDescriptor);
        if (destinationMetadata.UserId != sourceMetadata.UserId
            || destinationMetadata.GroupId != sourceMetadata.GroupId)
        {
            LocalUnixNative.SetOwner(destinationDescriptor, sourceMetadata.UserId, sourceMetadata.GroupId);
        }
        LocalUnixNative.SetMode(destinationDescriptor, sourceMetadata.Mode & 0x0fff);
        LocalUnixNative.Sync(destinationDescriptor);

        destinationMetadata = LocalUnixNative.ReadMetadata(destinationDescriptor);
        if (LocalUnixNative.GetNodeKind(destinationMetadata, destination.FullPath) != LocalSecureNodeKind.RegularFile
            || destinationMetadata.Device != expectedVolume
            || destinationMetadata.Length != sourceMetadata.Length
            || destinationMetadata.LinkCount != 1)
        {
            throw new LocalSecureApprovalChangedException(destination.FullPath);
        }
    }

    private static bool CanFallBackToNamedTemporaryFile(int error)
        => error is 1 or 2 or 13 or 21 or 22 or 95;

    private void ValidateTemporaryFile(LocalSecureHandle parent, LocalSecureTemporaryFile temporaryFile)
    {
        var metadata = LocalUnixNative.ReadMetadata(temporaryFile.Handle.DangerousGetHandle().ToInt32());
        if (LocalUnixNative.GetNodeKind(metadata, temporaryFile.FullPath) != LocalSecureNodeKind.RegularFile
            || metadata.Device != parent.Identity.Volume
            || new LocalSecureIdentity(metadata.Device, metadata.Inode) != temporaryFile.Identity
            || metadata.LinkCount != (temporaryFile.HasDirectoryEntry ? 1UL : 0UL))
        {
            throw new LocalSecureApprovalChangedException(temporaryFile.FullPath);
        }
    }

    private static void PreserveMetadata(LocalSecureTemporaryFile temporaryFile, LocalSecureHandle expectedTarget)
    {
        var descriptor = temporaryFile.Handle.DangerousGetHandle().ToInt32();
        var metadata = LocalUnixNative.ReadMetadata(descriptor);
        if (metadata.UserId != expectedTarget.UserId || metadata.GroupId != expectedTarget.GroupId)
        {
            LocalUnixNative.SetOwner(descriptor, expectedTarget.UserId, expectedTarget.GroupId);
        }
        LocalUnixNative.SetMode(descriptor, expectedTarget.Mode & 0x0fff);
    }
}
