using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Local;

internal static class LocalFileSystemExecutor
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> MutationGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static async ValueTask<AgentFileReadResult> ReadFileAsync(
        LocalExecutionRuntimeConfig config,
        AgentFileReadRequest request,
        bool allowOutsideConfiguredScope,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRange(request, out var rangeError))
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
            RevalidateMutationPath(config, request.Path, path, allowOutsideConfiguredScope);
            var entries = Directory.EnumerateFileSystemEntries(path)
                .Select(entry => Directory.Exists(entry) ? Path.GetFileName(entry) + Path.DirectorySeparatorChar : Path.GetFileName(entry))
                .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase);
            return new AgentFileReadResult(path, string.Join(Environment.NewLine, entries), IsDirectory: true);
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

            var content = await File.ReadAllTextAsync(path, cancellationToken);
            var totalLines = CountLines(content);
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
        var path = LocalPathResolver.ResolveFileSystemPath(config, request.Path, allowOutsideConfiguredScope);
        var gate = MutationGates.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            var parent = Path.GetDirectoryName(path) ?? LocalPathResolver.ResolveRoot(config);
            Directory.CreateDirectory(parent);
            RevalidateMutationPath(config, request.Path, path, allowOutsideConfiguredScope);

            if (Directory.Exists(path))
            {
                return new AgentFileMutationResult(path, "The write target is not a regular file.", IsError: true, ErrorCode: AgentFileReadErrorCodes.NotAFile);
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
                return new AgentFileMutationResult(path, "File already exists.", IsError: true, ErrorCode: "file-exists");
            }

            faultInjector?.OnFaultPoint(LocalFileWriteFaultPoint.BeforeAtomicReplace);
            if (!await ExpectedContentMatchesAsync(path, request.ExpectedContentHash, cancellationToken).ConfigureAwait(false))
            {
                return ContentChanged(path);
            }

            File.Move(temporaryPath, path, request.Overwrite);
            temporaryPath = null;
            return new AgentFileMutationResult(path, $"Wrote {request.Content.Length} character(s).");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                File.Delete(temporaryPath);
            }

            gate.Release();
        }
    }

    public static async ValueTask<AgentFileMutationResult> DeleteFileAsync(
        LocalExecutionRuntimeConfig config,
        AgentFileDeleteRequest request,
        bool allowOutsideConfiguredScope,
        CancellationToken cancellationToken = default)
    {
        var path = LocalPathResolver.ResolveFileSystemPath(config, request.Path, allowOutsideConfiguredScope);
        var gate = MutationGates.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
            {
                RevalidateMutationPath(config, request.Path, path, allowOutsideConfiguredScope);
                if (!await ExpectedContentMatchesAsync(path, request.ExpectedContentHash, cancellationToken).ConfigureAwait(false))
                {
                    return ContentChanged(path);
                }

                File.Delete(path);
                return new AgentFileMutationResult(path, "File deleted.");
            }

            if (Directory.Exists(path))
            {
                if (request.ExpectedContentHash is not null)
                {
                    return ContentChanged(path);
                }

                RevalidateMutationPath(config, request.Path, path, allowOutsideConfiguredScope);
                Directory.Delete(path, request.Recursive);
                return new AgentFileMutationResult(path, "Directory deleted.");
            }

            return new AgentFileMutationResult(path, "Path does not exist.", IsError: true, ErrorCode: "path-not-found");
        }
        finally
        {
            gate.Release();
        }
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

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        return string.Equals(actual, expectedContentHash, StringComparison.OrdinalIgnoreCase);
    }

    private static AgentFileMutationResult ContentChanged(string path)
        => new(path, "The file changed after patch preflight; no mutation was applied.", IsError: true, ErrorCode: "file-content-changed");

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

    private static async Task<AgentFileReadResult> ReadRangeAsync(
        string path,
        AgentFileReadRequest request,
        CancellationToken cancellationToken)
    {
        var offset = request.Offset ?? 1;
        var limit = request.Limit ?? 2000;
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

    private static bool TryValidateRange(AgentFileReadRequest request, out string? error)
    {
        if (request.Offset is <= 0)
        {
            error = "File read offset must be greater than or equal to 1.";
            return false;
        }

        if (request.Limit is <= 0 or > 2000)
        {
            error = "File read limit must be between 1 and 2000.";
            return false;
        }

        error = null;
        return true;
    }

    private static int CountLines(string content)
    {
        if (content.Length == 0)
        {
            return 0;
        }

        var count = 0;
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] == '\r')
            {
                count++;
                if (index + 1 < content.Length && content[index + 1] == '\n')
                {
                    index++;
                }
            }
            else if (content[index] == '\n')
            {
                count++;
            }
        }

        return content[^1] is '\r' or '\n' ? count : count + 1;
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
