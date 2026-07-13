using System.Text;
using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Threading;

namespace Sunder.Package.Agent.Execution.Local;

internal static class LocalFileSystemExecutor
{
    private static readonly ReferenceCountedKeyedLock<string> MutationGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal static int MutationGateCount => MutationGates.Count;

    public static async ValueTask<AgentFileReadResult> ReadFileAsync(
        LocalExecutionRuntimeConfig config,
        AgentFileReadRequest request,
        bool allowOutsideConfiguredScope,
        CancellationToken cancellationToken)
    {
        if (!FileOperation.TryValidateRange(request.Offset, request.Limit, out var rangeError))
        {
            return AgentFileReadResult.Failure(request.Path, AgentFileReadErrorCodes.InvalidRange, rangeError!);
        }

        string path;
        try
        {
            path = LocalPathResolver.ResolveFileSystemPath(config, request.Path, allowOutsideConfiguredScope);
        }
        catch (InvalidOperationException ex)
        {
            return AgentFileReadResult.Failure(request.Path, AgentFileReadErrorCodes.OutsideConfiguredScope, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AgentFileReadResult.Failure(request.Path, AgentFileReadErrorCodes.PathCanonicalizationFailed, ex.Message);
        }

        if (Directory.Exists(path))
        {
            try
            {
                RevalidateMutationPath(config, request.Path, path, allowOutsideConfiguredScope);
                var entries = Directory.EnumerateFileSystemEntries(path)
                    .Take(AgentPayloadLimits.MaxLocalDirectoryEntries + 1)
                    .Select(entry => Directory.Exists(entry) ? Path.GetFileName(entry) + Path.DirectorySeparatorChar : Path.GetFileName(entry))
                    .ToArray();
                var wasTruncated = entries.Length > AgentPayloadLimits.MaxLocalDirectoryEntries;
                return new AgentFileReadResult(
                    path,
                    string.Join(Environment.NewLine, entries.Take(AgentPayloadLimits.MaxLocalDirectoryEntries).OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase)),
                    IsDirectory: true,
                    WasTruncated: wasTruncated);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return AgentFileReadResult.Failure(path, AgentFileReadErrorCodes.ReadFailed, $"Unable to list directory '{path}': {ex.Message}");
            }
        }

        if (!File.Exists(path))
        {
            return AgentFileReadResult.Failure(path, AgentFileReadErrorCodes.FileNotFound, $"File not found: {path}");
        }

        try
        {
            RevalidateMutationPath(config, request.Path, path, allowOutsideConfiguredScope);
            if (await IsBinaryFileAsync(path, cancellationToken))
            {
                return AgentFileReadResult.Failure(path, AgentFileReadErrorCodes.BinaryFile, $"Binary file reads are not supported: {path}");
            }

            if (request.Offset is not null || request.Limit is not null)
            {
                return await ReadRangeAsync(path, request, cancellationToken);
            }

            var content = await ReadBoundedTextAsync(path, cancellationToken).ConfigureAwait(false);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AgentFileReadResult.Failure(path, AgentFileReadErrorCodes.ReadFailed, $"Unable to read file '{path}': {ex.Message}");
        }
    }

    public static async ValueTask<AgentFileMutationResult> WriteFileAsync(
        LocalExecutionRuntimeConfig config,
        AgentFileWriteRequest request,
        bool allowOutsideConfiguredScope,
        CancellationToken cancellationToken,
        ILocalFileWriteFaultInjector? faultInjector = null)
    {
        if (!TryResolveMutationPath(config, request.Path, allowOutsideConfiguredScope, out var path, out var pathError))
        {
            return pathError!;
        }

        using var gate = await MutationGates.EnterAsync(path, cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            var parent = Path.GetDirectoryName(path) ?? LocalPathResolver.ResolveRoot(config);
            Directory.CreateDirectory(parent);
            RevalidateMutationPath(config, request.Path, path, allowOutsideConfiguredScope);

            if (Directory.Exists(path))
            {
                return FileOperation.Failure(path, "The write target is not a regular file.", AgentFileReadErrorCodes.NotAFile);
            }

            temporaryPath = Path.Combine(parent, $".{Path.GetFileName(path)}.sunder-{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                await writer.WriteAsync(request.Content.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            faultInjector?.OnFaultPoint(LocalFileWriteFaultPoint.TemporaryFileFlushed);

            // The final physical-scope and CAS checks intentionally sit directly before rename.
            // Directory handles cannot be used portably by the BCL, so an external process can
            // still swap a validated parent path in the narrow interval between this check and move.
            RevalidateMutationPath(config, request.Path, path, allowOutsideConfiguredScope);
            if (!request.Overwrite && (File.Exists(path) || Directory.Exists(path)))
            {
                return FileOperation.Failure(path, "File already exists.", FileOperation.FileExistsErrorCode);
            }

            faultInjector?.OnFaultPoint(LocalFileWriteFaultPoint.BeforeAtomicReplace);
            if (!await ExpectedContentMatchesAsync(path, request.ExpectedContentHash, cancellationToken).ConfigureAwait(false))
            {
                return FileOperation.ContentChanged(path);
            }

            RevalidateMutationPath(config, request.Path, path, allowOutsideConfiguredScope);
            File.Move(temporaryPath, path, request.Overwrite);
            temporaryPath = null;
            return FileOperation.Written(path, request.Content.Length);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

        }
    }

    public static async ValueTask<AgentFileMutationResult> DeleteFileAsync(
        LocalExecutionRuntimeConfig config,
        AgentFileDeleteRequest request,
        bool allowOutsideConfiguredScope,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveMutationPath(config, request.Path, allowOutsideConfiguredScope, out var path, out var pathError))
        {
            return pathError!;
        }

        using var gate = await MutationGates.EnterAsync(path, cancellationToken).ConfigureAwait(false);
        if (File.Exists(path))
        {
            RevalidateMutationPath(config, request.Path, path, allowOutsideConfiguredScope);
            if (!await ExpectedContentMatchesAsync(path, request.ExpectedContentHash, cancellationToken).ConfigureAwait(false))
            {
                return FileOperation.ContentChanged(path);
            }

            RevalidateMutationPath(config, request.Path, path, allowOutsideConfiguredScope);
            File.Delete(path);
            return FileOperation.FileDeleted(path);
        }

        if (Directory.Exists(path))
        {
            if (request.ExpectedContentHash is not null)
            {
                return FileOperation.ContentChanged(path);
            }

            RevalidateMutationPath(config, request.Path, path, allowOutsideConfiguredScope);
            Directory.Delete(path, request.Recursive);
            return FileOperation.DirectoryDeleted(path);
        }

        return FileOperation.Failure(path, "Path does not exist.", FileOperation.PathNotFoundErrorCode);
    }

    private static void RevalidateMutationPath(
        LocalExecutionRuntimeConfig config,
        string requestedPath,
        string expectedPath,
        bool allowOutsideConfiguredScope)
    {
        var revalidated = LocalPathResolver.ResolveFileSystemPath(config, requestedPath, allowOutsideConfiguredScope);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(revalidated, expectedPath, comparison))
        {
            throw new IOException($"Path '{requestedPath}' changed while the file operation was in progress.");
        }
    }

    private static async Task<bool> ExpectedContentMatchesAsync(
        string path,
        string? expectedContentHash,
        CancellationToken cancellationToken)
    {
        if (expectedContentHash is null)
        {
            return true;
        }

        if (!File.Exists(path) || Directory.Exists(path))
        {
            return false;
        }

        var content = await ReadBoundedTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            return false;
        }

        var actual = FileOperation.ComputeContentHash(content);
        return string.Equals(actual, expectedContentHash, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveMutationPath(
        LocalExecutionRuntimeConfig config,
        string requestedPath,
        bool allowOutsideConfiguredScope,
        out string path,
        out AgentFileMutationResult? error)
    {
        try
        {
            path = LocalPathResolver.ResolveFileSystemPath(config, requestedPath, allowOutsideConfiguredScope);
            error = null;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            path = requestedPath;
            error = FileOperation.Failure(requestedPath, ex.Message, AgentFileReadErrorCodes.OutsideConfiguredScope);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            path = requestedPath;
            error = FileOperation.Failure(requestedPath, ex.Message, AgentFileReadErrorCodes.PathCanonicalizationFailed);
            return false;
        }
    }

    private static async Task<bool> IsBinaryFileAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(8192, (int)Math.Min(new FileInfo(path).Length, 8192))];
        if (buffer.Length == 0)
        {
            return false;
        }

        await using var stream = File.OpenRead(path);
        var read = await stream.ReadAsync(buffer, cancellationToken);
        return buffer.Take(read).Any(value => value == 0);
    }

    private static async Task<string?> ReadBoundedTextAsync(string path, CancellationToken cancellationToken)
    {
        var maxBytes = AgentPayloadLimits.MaxLocalFullFileReadBytes;
        if (new FileInfo(path).Length > maxBytes)
        {
            return null;
        }

        var bytes = new byte[maxBytes + 1];
        var totalRead = 0;
        await using (var stream = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.ReadWrite,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            while (totalRead < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(totalRead), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }
        }

        if (totalRead > maxBytes)
        {
            return null;
        }

        using var memory = new MemoryStream(bytes, 0, totalRead, writable: false);
        using var reader = new StreamReader(memory, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AgentFileReadResult> ReadRangeAsync(
        string path,
        AgentFileReadRequest request,
        CancellationToken cancellationToken)
    {
        var offset = request.Offset ?? 1;
        var limit = request.Limit ?? FileOperation.DefaultReadLimit;
        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>(limit);
        var totalLines = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            totalLines++;
            if (totalLines >= offset && lines.Count < limit)
            {
                lines.Add(line);
            }
        }

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
