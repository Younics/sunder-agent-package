using System.Buffers;
using System.Text;

namespace Sunder.Package.Agent.Shared.Importing;

internal sealed record ImportTreeLimits(
    int MaxFiles,
    int MaxPathDepth,
    long MaxFileBytes,
    long MaxTotalBytes);

internal sealed record ImportTreeFile(string RelativePath, string SourcePath, long Length);

internal static class BoundedImportIO
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static async Task<string> ReadUtf8FileAsync(
        string path,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RejectReparsePoint(path, "Import source file");
        var info = new FileInfo(path);
        if (info.Length > maxBytes)
        {
            throw new InvalidDataException($"Import source '{Path.GetFileName(path)}' exceeds the {maxBytes}-byte limit.");
        }

        var bytes = new byte[checked((int)info.Length)];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException($"Import source '{Path.GetFileName(path)}' changed while it was being read.");
            }

            offset += read;
        }

        if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException($"Import source '{Path.GetFileName(path)}' grew beyond the {maxBytes}-byte limit while it was being read.");
        }

        RejectReparsePoint(path, "Import source file");
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException($"Import source '{Path.GetFileName(path)}' is not valid UTF-8.", ex);
        }
    }

    public static IReadOnlyList<ImportTreeFile> ValidateTree(
        string sourceRoot,
        ImportTreeLimits limits,
        CancellationToken cancellationToken = default)
    {
        RejectReparsePoint(sourceRoot, "Import source directory");
        var files = new List<ImportTreeFile>();
        var canonicalPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(sourceRoot);
        long totalBytes = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            RejectReparsePoint(directory, "Import source directory");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                var relativePath = NormalizeRelativePath(Path.GetRelativePath(sourceRoot, entry), limits.MaxPathDepth);
                if (!canonicalPaths.TryAdd(relativePath, relativePath))
                {
                    throw new InvalidDataException($"Import source contains duplicate or case-colliding path '{relativePath}' and '{canonicalPaths[relativePath]}'.");
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Import source contains a symbolic link or reparse point: '{relativePath}'.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                if (files.Count == limits.MaxFiles)
                {
                    throw new InvalidDataException($"Import source exceeds the {limits.MaxFiles}-file limit.");
                }

                var length = new FileInfo(entry).Length;
                if (length > limits.MaxFileBytes)
                {
                    throw new InvalidDataException($"Import file is too large: '{relativePath}'.");
                }

                totalBytes = checked(totalBytes + length);
                if (totalBytes > limits.MaxTotalBytes)
                {
                    throw new InvalidDataException($"Import source exceeds the {limits.MaxTotalBytes}-byte aggregate limit.");
                }

                files.Add(new ImportTreeFile(relativePath, entry, length));
            }
        }

        return files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
    }

    public static async Task StageTreeAsync(
        IReadOnlyList<ImportTreeFile> files,
        string targetRoot,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectReparseAncestors(file);
                var targetPath = Path.Combine(targetRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                await using var source = new FileStream(
                    file.SourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    buffer.Length,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var target = new FileStream(
                    targetPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    buffer.Length,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                long copied = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    copied = checked(copied + read);
                    if (copied > file.Length)
                    {
                        throw new InvalidDataException($"Import file '{file.RelativePath}' changed while it was being staged.");
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                if (copied != file.Length)
                {
                    throw new InvalidDataException($"Import file '{file.RelativePath}' changed while it was being staged.");
                }

                RejectReparseAncestors(file);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static string NormalizeRelativePath(string path, int maxDepth)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            throw new InvalidDataException($"Import path is not relative: '{path}'.");
        }

        var segments = path.Replace('\\', '/').Split('/');
        if (segments.Length > maxDepth || segments.Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException($"Import path is invalid or exceeds the {maxDepth}-segment depth limit: '{path}'.");
        }

        return string.Join('/', segments);
    }

    public static void RejectReparsePoint(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{description} is a symbolic link or reparse point: '{path}'.");
        }
    }

    private static void RejectReparseAncestors(ImportTreeFile file)
    {
        RejectReparsePoint(file.SourcePath, "Import source file");
        var path = Path.GetDirectoryName(file.SourcePath);
        var remainingSegments = file.RelativePath.Count(character => character == '/') + 1;
        while (path is not null && remainingSegments-- > 0)
        {
            RejectReparsePoint(path, "Import source directory");
            path = Path.GetDirectoryName(path);
        }
    }
}
