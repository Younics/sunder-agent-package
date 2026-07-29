using System.Text;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Agent.Execution.Common;

internal sealed record HostFileSystemPathContext(
    IReadOnlyList<string> ConfiguredRoots,
    string HostPath,
    string ReportedPath,
    LocalSecureApprovalLease? ApprovedAuthority = null,
    HostReportedPathStyle ReportedPathStyle = HostReportedPathStyle.Native,
    Action<LocalSecureApprovalLease>? PostMutationAuthoritySink = null);

internal enum HostReportedPathStyle
{
    Native,
    Posix,
}

internal static class HostFileSystemExecutor
{
    internal static int MutationGateCount => HostMutationCoordinator.Count;

    public static async ValueTask<AgentFileReadResult> ReadFileAsync(
        HostFileSystemPathContext pathContext,
        AgentFileReadRequest request,
        CancellationToken cancellationToken,
        ILocalSecureFileSystemHooks? hooks = null)
    {
        using var approvedAuthority = pathContext.ApprovedAuthority;
        if (!FileOperation.TryValidateRange(request.Offset, request.Limit, out var rangeError))
        {
            return AgentFileReadResult.Failure(pathContext.ReportedPath, AgentFileReadErrorCodes.InvalidRange, rangeError!);
        }

        var displayPath = pathContext.ReportedPath;
        try
        {
            using var session = HostSecurePathEngine.OpenAuthorized(
                pathContext.ConfiguredRoots,
                pathContext.HostPath,
                approvedAuthority,
                createParents: false,
                hooks,
                cancellationToken);
            using var target = session.OpenTarget();
            if (target.Handle.Kind == LocalSecureNodeKind.Directory)
            {
                return ListDirectory(session, target.Handle, displayPath, cancellationToken);
            }

            await using var stream = OpenReadStream(target.Handle);
            var openedLength = stream.Length;
            if ((request.Offset is not null || request.Limit is not null)
                && openedLength > HostFileSystemLimits.MaxRangedReadBytes)
            {
                return AgentFileReadResult.Failure(
                    displayPath,
                    AgentFileReadErrorCodes.TooLarge,
                    $"Ranged reads support at most {HostFileSystemLimits.MaxRangedReadBytes} bytes.");
            }
            if (await IsBinaryFileAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                return AgentFileReadResult.Failure(
                    displayPath,
                    AgentFileReadErrorCodes.BinaryFile,
                    $"Binary file reads are not supported: {displayPath}");
            }

            var result = request.Offset is not null || request.Limit is not null
                ? await ReadRangeAsync(stream, displayPath, request, cancellationToken).ConfigureAwait(false)
                : await ReadFullAsync(stream, displayPath, cancellationToken).ConfigureAwait(false);
            if (!result.IsError && stream.Length != openedLength)
            {
                return AgentFileReadResult.Failure(
                    displayPath,
                    AgentFileReadErrorCodes.ReadFailed,
                    $"File changed while it was being read: {displayPath}");
            }
            return result;
        }
        catch (LocalSecurePathNotFoundException)
        {
            return AgentFileReadResult.Failure(
                displayPath,
                AgentFileReadErrorCodes.FileNotFound,
                $"File not found: {displayPath}");
        }
        catch (InvalidOperationException ex)
        {
            return AgentFileReadResult.Failure(displayPath, AgentFileReadErrorCodes.OutsideConfiguredScope, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return AgentFileReadResult.Failure(
                displayPath,
                AgentFileReadErrorCodes.PathCanonicalizationFailed,
                ex.Message);
        }
    }

    public static async ValueTask<AgentFileMutationResult> WriteFileAsync(
        HostFileSystemPathContext pathContext,
        AgentFileWriteRequest request,
        CancellationToken cancellationToken,
        ILocalFileWriteFaultInjector? faultInjector = null,
        ILocalSecureFileSystemHooks? hooks = null)
    {
        try
        {
            return await WriteFileCoreAsync(
                pathContext,
                request,
                cancellationToken,
                faultInjector,
                hooks).ConfigureAwait(false);
        }
        catch (LocalSecureMutationRecoveryException)
        {
            return FileOperation.Failure(
                pathContext.ReportedPath,
                FileOperation.StrictMutationRecoveryRequiredMessage,
                FileOperation.StrictMutationRecoveryRequiredErrorCode);
        }
    }

    private static async ValueTask<AgentFileMutationResult> WriteFileCoreAsync(
        HostFileSystemPathContext pathContext,
        AgentFileWriteRequest request,
        CancellationToken cancellationToken,
        ILocalFileWriteFaultInjector? faultInjector,
        ILocalSecureFileSystemHooks? hooks)
    {
        var path = pathContext.ReportedPath;
        using var approvedAuthority = pathContext.ApprovedAuthority;
        if (!LocalSecureNative.StrictMutationsAvailable)
        {
            return FileOperation.Failure(
                path,
                FileOperation.StrictPlatformMutationUnavailableMessage,
                FileOperation.StrictPlatformMutationUnavailableErrorCode);
        }
        LocalSecureTemporaryFile? temporaryFile = null;
        LocalSecurePathSession? session = null;
        LocalSecureOpenedTarget? target = null;
        LocalSecureMutationReservation? reservation = null;
        try
        {
            session = HostSecurePathEngine.OpenAuthorized(
                pathContext.ConfiguredRoots,
                pathContext.HostPath,
                approvedAuthority,
                createParents: true,
                hooks,
                cancellationToken);
            target = session.TryOpenTarget(writable: true);
            LocalSecureTargetExpectation expectation;
            if (target is not null)
            {
                if (target.Handle.Kind != LocalSecureNodeKind.RegularFile)
                {
                    return FileOperation.Failure(
                        path,
                        "The write target is not a regular file.",
                        AgentFileReadErrorCodes.NotAFile);
                }
                if (!request.Overwrite)
                {
                    return FileOperation.Failure(path, "File already exists.", FileOperation.FileExistsErrorCode);
                }
                if (!ExpectedContentMatches(target.Handle, request.ExpectedContentHash, cancellationToken))
                {
                    return FileOperation.ContentChanged(path);
                }
                expectation = LocalSecureTargetExpectation.Existing(
                    target.Handle.Identity,
                    approvalBound: approvedAuthority is not null);
            }
            else
            {
                if (request.ExpectedContentHash is not null)
                {
                    return FileOperation.ContentChanged(path);
                }
                expectation = LocalSecureTargetExpectation.Missing(
                    approvalBound: approvedAuthority is not null);
            }

            var reservationSlots = (target is null ? 1 : 3) + 1;
            reservation = session.ReserveMutationSlots(session.Parent, reservationSlots, cancellationToken);
            temporaryFile = session.CreateTemporaryFile(reservation);
            await using (var stream = new FileStream(
                             temporaryFile.DuplicateHandle(),
                             FileAccess.Write,
                             64 * 1024,
                             isAsync: false))
            {
                await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
                {
                    await writer.WriteAsync(request.Content.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                faultInjector?.OnFaultPoint(LocalFileWriteFaultPoint.TemporaryFileFlushed);
                faultInjector?.OnFaultPoint(LocalFileWriteFaultPoint.BeforeAtomicReplace);
                session.PrepareToPublish();
                session.PublishTemporaryFile(
                    temporaryFile,
                    request.Overwrite,
                    expectation,
                    target?.Handle,
                    request.ExpectedContentHash is null
                        ? null
                        : handle => ExpectedContentMatches(handle, request.ExpectedContentHash, CancellationToken.None),
                    reservation,
                    cancellationToken);
            }

            CapturePostMutationAuthority(pathContext, session, temporaryFile);

            return FileOperation.Written(path, request.Content.Length);
        }
        catch (LocalSecureFileAlreadyExistsException)
        {
            return FileOperation.Failure(path, "File already exists.", FileOperation.FileExistsErrorCode);
        }
        catch (LocalSecureApprovalChangedException ex)
        {
            return request.ExpectedContentHash is not null
                ? FileOperation.ContentChanged(path)
                : FileOperation.Failure(path, ex.Message, AgentFileReadErrorCodes.PathCanonicalizationFailed);
        }
        catch (LocalSecureContentChangedException)
        {
            return FileOperation.ContentChanged(path);
        }
        catch (LocalSecureMutationRecoveryException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            return FileOperation.Failure(path, ex.Message, AgentFileReadErrorCodes.OutsideConfiguredScope);
        }
        catch (Exception ex) when (ex is LocalSecurePathException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return FileOperation.Failure(path, ex.Message, AgentFileReadErrorCodes.PathCanonicalizationFailed);
        }
        finally
        {
            LocalSecureMutationRecoveryException? cleanupFailure = null;
            try
            {
                if (temporaryFile is not null)
                {
                    try
                    {
                        if (!temporaryFile.WasPublished && session is not null && reservation is not null)
                        {
                            session.DeleteTemporaryFile(temporaryFile, reservation);
                        }
                    }
                    finally
                    {
                        temporaryFile.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                cleanupFailure = ex as LocalSecureMutationRecoveryException
                    ?? new LocalSecureMutationRecoveryException(path, ex);
            }
            target?.Dispose();
            session?.Dispose();
            reservation?.Dispose();
            if (cleanupFailure is not null)
            {
                throw cleanupFailure;
            }
        }
    }

    public static async ValueTask<AgentFileMutationResult> DeleteFileAsync(
        HostFileSystemPathContext pathContext,
        AgentFileDeleteRequest request,
        CancellationToken cancellationToken = default,
        ILocalSecureFileSystemHooks? hooks = null)
    {
        var path = pathContext.ReportedPath;
        using var approvedAuthority = pathContext.ApprovedAuthority;
        if (!LocalSecureNative.StrictMutationsAvailable)
        {
            return FileOperation.Failure(
                path,
                FileOperation.StrictPlatformMutationUnavailableMessage,
                FileOperation.StrictPlatformMutationUnavailableErrorCode);
        }
        try
        {
            using var session = HostSecurePathEngine.OpenAuthorized(
                pathContext.ConfiguredRoots,
                pathContext.HostPath,
                approvedAuthority,
                createParents: false,
                hooks,
                cancellationToken);
            using var target = session.OpenTarget(writable: true);
            var kind = target.Handle.Kind;
            if (kind == LocalSecureNodeKind.RegularFile)
            {
                if (!ExpectedContentMatches(target.Handle, request.ExpectedContentHash, cancellationToken))
                {
                    return FileOperation.ContentChanged(path);
                }
                using var reservation = session.ReserveMutationSlots(session.Parent, 2, cancellationToken);
                session.DeleteTarget(
                    target.Handle,
                    request.ExpectedContentHash is null
                        ? null
                        : handle => ExpectedContentMatches(handle, request.ExpectedContentHash, CancellationToken.None),
                    reservation: reservation,
                    cancellationToken: cancellationToken);
                CaptureDeletedPostMutationAuthority(pathContext, session);
                return FileOperation.FileDeleted(path);
            }

            if (request.ExpectedContentHash is not null)
            {
                return FileOperation.ContentChanged(path);
            }
            if (!request.Recursive)
            {
                return FileOperation.Failure(
                    path,
                    "Unix structured directory deletion requires Recursive=true, including for empty directories.",
                    "recursive-directory-delete-required");
            }
            using var configuredRoots = HostSecurePathEngine.OpenRootIdentitySet(
                pathContext.ConfiguredRoots,
                hooks,
                cancellationToken);
            if (configuredRoots.Contains(target.Handle.Identity))
            {
                return FileOperation.Failure(
                    path,
                    "A configured workspace root or its ancestor cannot be deleted by a structured file operation.",
                    AgentFileReadErrorCodes.OutsideConfiguredScope);
            }
            await DeleteDirectoryContentsAsync(
                session,
                target.Handle,
                new HostTraversalBudget(cancellationToken),
                depth: 0,
                cancellationToken).ConfigureAwait(false);
            using var directoryReservation = session.ReserveMutationSlots(session.Parent, 2, cancellationToken);
            session.DeleteTarget(
                target.Handle,
                reservation: directoryReservation,
                cancellationToken: cancellationToken);
            CaptureDeletedPostMutationAuthority(pathContext, session);
            return FileOperation.DirectoryDeleted(path);
        }
        catch (LocalSecurePathNotFoundException)
        {
            return FileOperation.Failure(path, "Path does not exist.", FileOperation.PathNotFoundErrorCode);
        }
        catch (LocalSecureApprovalChangedException)
        {
            return request.ExpectedContentHash is not null
                ? FileOperation.ContentChanged(path)
                : FileOperation.Failure(
                    path,
                    "The path identity changed before deletion.",
                    AgentFileReadErrorCodes.PathCanonicalizationFailed);
        }
        catch (LocalSecureContentChangedException)
        {
            return FileOperation.ContentChanged(path);
        }
        catch (LocalSecureMutationRecoveryException)
        {
            return FileOperation.Failure(
                path,
                FileOperation.StrictMutationRecoveryRequiredMessage,
                FileOperation.StrictMutationRecoveryRequiredErrorCode);
        }
        catch (InvalidOperationException ex)
        {
            return FileOperation.Failure(path, ex.Message, AgentFileReadErrorCodes.OutsideConfiguredScope);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return FileOperation.Failure(path, ex.Message, AgentFileReadErrorCodes.PathCanonicalizationFailed);
        }
    }

    private static AgentFileReadResult ListDirectory(
        LocalSecurePathSession session,
        LocalSecureHandle directory,
        string path,
        CancellationToken cancellationToken)
    {
        var budget = new HostTraversalBudget(cancellationToken);
        var entries = new List<string>(AgentPayloadLimits.MaxLocalDirectoryEntries + 1);
        foreach (var name in session.EnumerateRawNames(directory))
        {
            budget.Visit(depth: 0, name);
            if (HostSecurePathEngine.IsReservedName(name))
            {
                continue;
            }
            if (entries.Count > AgentPayloadLimits.MaxLocalDirectoryEntries)
            {
                break;
            }
            using var child = session.OpenChild(directory, name);
            entries.Add(EscapeEntryName(name)
                        + (child.Kind == LocalSecureNodeKind.Directory
                            ? GetDirectorySuffix(path)
                            : string.Empty));
        }

        var wasTruncated = entries.Count > AgentPayloadLimits.MaxLocalDirectoryEntries;
        return new AgentFileReadResult(
            path,
            string.Join(
                Environment.NewLine,
                entries.Take(AgentPayloadLimits.MaxLocalDirectoryEntries).OrderBy(entry => entry, StringComparer.Ordinal)),
            IsDirectory: true,
            WasTruncated: wasTruncated);
    }

    private static void CapturePostMutationAuthority(
        HostFileSystemPathContext pathContext,
        LocalSecurePathSession session,
        LocalSecureTemporaryFile temporaryFile)
    {
        if (pathContext.PostMutationAuthoritySink is not { } sink)
        {
            return;
        }

        var retainedTarget = temporaryFile.TakePublishedTarget();
        LocalSecureApprovalLease? authority = session.TakePostMutationAuthority(
            retainedTarget,
            retainedTarget.Identity,
            LocalSecureNodeKind.RegularFile);
        try
        {
            sink(authority);
            authority = null;
        }
        finally
        {
            authority?.Dispose();
        }
    }

    private static void CaptureDeletedPostMutationAuthority(
        HostFileSystemPathContext pathContext,
        LocalSecurePathSession session)
    {
        if (pathContext.PostMutationAuthoritySink is not { } sink)
        {
            return;
        }

        LocalSecureApprovalLease? authority = session.TakeDeletedPostMutationAuthority();
        try
        {
            sink(authority);
            authority = null;
        }
        finally
        {
            authority?.Dispose();
        }
    }

    private static async Task DeleteDirectoryContentsAsync(
        LocalSecurePathSession session,
        LocalSecureHandle directory,
        HostTraversalBudget budget,
        int depth,
        CancellationToken cancellationToken)
    {
        var names = new List<string>();
        foreach (var name in session.EnumerateRawNames(directory))
        {
            budget.Visit(depth, name);
            if (HostSecurePathEngine.IsReservedName(name))
            {
                continue;
            }
            names.Add(name);
        }
        using var reservation = session.ReserveMutationSlots(directory, checked(names.Count + 1), cancellationToken);
        foreach (var name in names)
        {
            using var child = session.OpenChild(directory, name, writable: true);
            if (child.Kind == LocalSecureNodeKind.Directory)
            {
                await DeleteDirectoryContentsAsync(session, child, budget, depth + 1, cancellationToken).ConfigureAwait(false);
            }
            session.DeleteChild(
                directory,
                name,
                child,
                finalTargetValidator: null,
                reservation,
                cancellationToken);
        }
    }

    private static string GetDirectorySuffix(string path)
        => path.Length > 0 && path[0] == '/' ? "/" : Path.DirectorySeparatorChar.ToString();

    private static bool ExpectedContentMatches(
        LocalSecureHandle handle,
        string? expectedContentHash,
        CancellationToken cancellationToken)
        => ExpectedContentMatchesAsync(handle, expectedContentHash, cancellationToken).GetAwaiter().GetResult();

    private static async Task<bool> ExpectedContentMatchesAsync(
        LocalSecureHandle handle,
        string? expectedContentHash,
        CancellationToken cancellationToken)
    {
        if (expectedContentHash is null)
        {
            return true;
        }
        await using var stream = OpenReadStream(handle);
        var content = await ReadBoundedTextAsync(stream, cancellationToken).ConfigureAwait(false);
        return content is not null
               && string.Equals(
                   FileOperation.ComputeContentHash(content),
                   expectedContentHash,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static FileStream OpenReadStream(LocalSecureHandle handle)
        => new(handle.DuplicateHandle(), FileAccess.Read, 64 * 1024, isAsync: false);

    private static async Task<bool> IsBinaryFileAsync(FileStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(8192, checked((int)Math.Min(stream.Length, 8192)))];
        stream.Position = 0;
        var read = buffer.Length == 0
            ? 0
            : await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        return buffer.AsSpan(0, read).Contains((byte)0);
    }

    private static async Task<AgentFileReadResult> ReadFullAsync(
        FileStream stream,
        string path,
        CancellationToken cancellationToken)
    {
        var content = await ReadBoundedTextAsync(stream, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            return AgentFileReadResult.Failure(
                path,
                AgentFileReadErrorCodes.TooLarge,
                $"File exceeds the {AgentPayloadLimits.MaxLocalFullFileReadBytes}-byte full-read limit; use offset and limit.");
        }
        var totalLines = FileOperation.CountLines(content);
        return new AgentFileReadResult(path, content)
        {
            StartLine = 1,
            EndLine = totalLines,
            TotalLines = totalLines,
        };
    }

    private static async Task<string?> ReadBoundedTextAsync(FileStream stream, CancellationToken cancellationToken)
    {
        var maximumBytes = AgentPayloadLimits.MaxLocalFullFileReadBytes;
        if (stream.Length > maximumBytes)
        {
            return null;
        }
        var bytes = new byte[checked((int)stream.Length)];
        stream.Position = 0;
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            offset += read;
        }
        using var memory = new MemoryStream(bytes, 0, offset, writable: false);
        using var reader = new StreamReader(memory, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AgentFileReadResult> ReadRangeAsync(
        FileStream stream,
        string path,
        AgentFileReadRequest request,
        CancellationToken cancellationToken)
    {
        var offset = request.Offset ?? 1;
        var limit = request.Limit ?? FileOperation.DefaultReadLimit;
        var lines = new List<string>(limit);
        var scan = await HostBoundedLineReader.ScanAsync(
            stream,
            HostFileSystemLimits.MaxRangedReadBytes,
            HostFileSystemLimits.MaxLineCharacters,
            throwOnInvalidBytes: false,
            (lineNumber, line) =>
            {
                if (lineNumber >= offset && lines.Count < limit)
                {
                    lines.Add(line);
                }
            },
            cancellationToken).ConfigureAwait(false);
        if (scan.ExceededByteLimit || scan.ExceededLineLimit)
        {
            return AgentFileReadResult.Failure(
                path,
                AgentFileReadErrorCodes.TooLarge,
                $"Ranged reads support at most {HostFileSystemLimits.MaxRangedReadBytes} bytes and {HostFileSystemLimits.MaxLineCharacters} characters per line.");
        }
        var totalLines = scan.TotalLines;
        if (offset > totalLines && !(offset == 1 && totalLines == 0))
        {
            return AgentFileReadResult.Failure(
                path,
                AgentFileReadErrorCodes.RangeOutsideFile,
                $"Line offset {offset} is outside the file's {totalLines} line(s).") with
            {
                TotalLines = totalLines,
            };
        }

        var endLine = lines.Count == 0 ? 0 : offset + lines.Count - 1;
        return new AgentFileReadResult(path, string.Join('\n', lines), WasTruncated: endLine < totalLines)
        {
            StartLine = offset,
            EndLine = endLine,
            TotalLines = totalLines,
        };
    }

    private static string EscapeEntryName(string name)
        => name.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}

internal interface ILocalFileWriteFaultInjector
{
    void OnFaultPoint(LocalFileWriteFaultPoint faultPoint);
}

internal enum LocalFileWriteFaultPoint
{
    TemporaryFileFlushed,
    BeforeAtomicReplace,
}
