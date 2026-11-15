using System.Buffers;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sunder.Package.Agent.Shared.Importing;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record ImportTreeLimits(
    int MaxFiles,
    int MaxDirectories,
    int MaxPathDepth,
    long MaxFileBytes,
    long MaxTotalBytes);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record ImportTreeFile(
    string RelativePath,
    string SourcePath,
    long Length,
    byte[] Sha256,
    ImportNodeIdentity Identity,
    IReadOnlyList<ImportDirectorySnapshot> Ancestors);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record ImportDirectorySnapshot(
    string Path,
    ImportNodeIdentity Identity,
    bool TrackChanges);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal enum ImportReadCheckpoint
{
    BeforeSourceOpen,
    AfterSourceOpen,
    AfterSourceRead,
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal enum ImportNodeKind
{
    RegularFile,
    Directory,
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal readonly record struct ImportNodeIdentity(
    ulong Volume,
    ulong FileId,
    ulong FileIdHigh,
    ImportNodeKind Kind,
    long Length,
    ulong LinkCount,
    long ModificationSeconds,
    long ModificationNanoseconds,
    long ChangeSeconds,
    long ChangeNanoseconds);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static partial class BoundedImportIO
{
    private const int BufferSize = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static async Task<string> ReadUtf8FileAsync(
        string path,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = CanonicalizePlatformPath(path);
        var ancestors = CaptureAncestorDirectories(fullPath, includeLeaf: false, cancellationToken);
        await using var source = OpenRegularFile(fullPath);
        if (source.Identity.Length > maxBytes)
        {
            throw new InvalidDataException(
                $"Import source '{Path.GetFileName(path)}' exceeds the {maxBytes}-byte limit.");
        }

        var bytes = new byte[checked((int)source.Identity.Length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await source.Stream.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw Changed(path);
            }
            offset += read;
        }
        if (await source.Stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
        {
            throw Changed(path);
        }

        EnsureHandleUnchanged(source, fullPath);
        EnsurePathStillReferences(fullPath, source.Identity);
        ValidateDirectories(ancestors, cancellationToken);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"Import source '{Path.GetFileName(path)}' is not valid UTF-8.",
                exception);
        }
    }

    public static IReadOnlyList<ImportTreeFile> ValidateTree(
        string sourceRoot,
        ImportTreeLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ValidateLimits(limits);
        cancellationToken.ThrowIfCancellationRequested();
        var fullRoot = CanonicalizePlatformPath(sourceRoot);
        var directorySnapshots = CaptureAncestorDirectories(
            fullRoot,
            includeLeaf: true,
            cancellationToken,
            trackLeafChanges: true).ToDictionary(
                snapshot => snapshot.Path,
                snapshot => snapshot,
                PathComparer);
        var files = new List<ImportTreeFile>();
        var canonicalPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directoryCount = 1;
        long totalBytes = 0;

        using var rootDirectory = OpenPathNoFollow(fullRoot, requireDirectory: true);
        if (!directorySnapshots.TryGetValue(fullRoot, out var expectedRoot)
            || rootDirectory.Identity != expectedRoot.Identity)
        {
            throw Changed(fullRoot);
        }

        VisitDirectory(rootDirectory, fullRoot, string.Empty);
        ValidateDirectories(directorySnapshots.Values, cancellationToken);
        return files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();

        void VisitDirectory(
            OpenedImportNode directory,
            string directoryPath,
            string relativePrefix)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryIdentity = ReadIdentity(directory.Handle, directoryPath);
            if (directoryIdentity != directory.Identity)
            {
                throw Changed(directoryPath);
            }

            foreach (var name in EnumerateDirectoryNames(directory.Handle, directoryPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = NormalizeRelativePath(
                    string.IsNullOrEmpty(relativePrefix) ? name : relativePrefix + "/" + name,
                    limits.MaxPathDepth);
                var fullPath = CanonicalizePlatformPath(Path.Combine(directoryPath, name));
                using var entry = OpenRelativeNoFollow(
                    directory.Handle,
                    name,
                    requireDirectory: false,
                    fullPath);
                var identity = entry.Identity;
                if (identity.Kind == ImportNodeKind.Directory
                    && directoryCount == limits.MaxDirectories)
                {
                    throw new InvalidDataException(
                        $"Import source exceeds the {limits.MaxDirectories}-directory limit.");
                }
                if (identity.Kind == ImportNodeKind.RegularFile
                    && files.Count == limits.MaxFiles)
                {
                    throw new InvalidDataException(
                        $"Import source exceeds the {limits.MaxFiles}-file limit.");
                }
                if (!canonicalPaths.TryAdd(relativePath, relativePath))
                {
                    throw new InvalidDataException(
                        $"Import source contains duplicate or case-colliding path '{relativePath}' and '{canonicalPaths[relativePath]}'.");
                }
                if (identity.Kind == ImportNodeKind.Directory)
                {
                    directoryCount++;
                    directorySnapshots.Add(
                        fullPath,
                        new ImportDirectorySnapshot(fullPath, identity, TrackChanges: true));
                    VisitDirectory(entry, fullPath, relativePath);
                    continue;
                }
                if (identity.LinkCount != 1)
                {
                    throw new InvalidDataException(
                        $"Import source contains hard-linked file '{relativePath}'.");
                }
                if (identity.Length > limits.MaxFileBytes)
                {
                    throw new InvalidDataException($"Import file is too large: '{relativePath}'.");
                }

                totalBytes = checked(totalBytes + identity.Length);
                if (totalBytes > limits.MaxTotalBytes)
                {
                    throw new InvalidDataException(
                        $"Import source exceeds the {limits.MaxTotalBytes}-byte aggregate limit.");
                }

                var ancestors = GetAncestorSnapshots(fullPath, directorySnapshots);
                using var source = OpenRegularFile(entry, fullPath);
                var hash = HashValidatedFile(source, fullPath, identity, cancellationToken);
                files.Add(new ImportTreeFile(
                    relativePath,
                    fullPath,
                    identity.Length,
                    hash,
                    identity,
                    ancestors));
            }

            var finalDirectoryIdentity = ReadIdentity(directory.Handle, directoryPath);
            if (finalDirectoryIdentity != directoryIdentity)
            {
                throw Changed(directoryPath);
            }
        }
    }

    public static async Task StageTreeAsync(
        IReadOnlyList<ImportTreeFile> files,
        string targetRoot,
        CancellationToken cancellationToken,
        Action<ImportReadCheckpoint, ImportTreeFile>? checkpoint = null)
    {
        using var writer = OpenTreeWriter(targetRoot);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var target = writer.CreateFile(file.RelativePath);
            await CopyFileToAsync(file, target, cancellationToken, checkpoint).ConfigureAwait(false);
        }
        writer.Complete();
    }

    internal static ImportTreeWriter OpenTreeWriter(string targetRoot)
        => new(CanonicalizePlatformPath(targetRoot));

    internal sealed class ImportTreeWriter : IDisposable
    {
        private readonly string _rootPath;
        private OpenedImportNode? _root;

        internal ImportTreeWriter(string rootPath)
        {
            _rootPath = rootPath;
            _root = OpenPathNoFollow(rootPath, requireDirectory: true);
        }

        public FileStream CreateFile(string relativePath)
        {
            var segments = SplitRelativePath(relativePath);
            using var parent = OpenDirectories(segments[..^1]);
            var parentHandle = parent?.Handle ?? Root.Handle;
            var displayPath = Path.Combine(
                _rootPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            var handle = CreateExclusiveRegularFile(parentHandle, segments[^1], displayPath);
            try
            {
                var identity = ReadIdentity(handle, displayPath);
                if (identity.Kind != ImportNodeKind.RegularFile || identity.LinkCount != 1)
                {
                    throw new InvalidDataException(
                        $"Import staging target is not a regular file: '{relativePath}'.");
                }
                var stream = new FileStream(handle, FileAccess.Write, BufferSize, isAsync: false);
                handle = null!;
                return stream;
            }
            finally
            {
                handle?.Dispose();
            }
        }

        public void CreateDirectory(string relativePath)
        {
            var segments = SplitRelativePath(relativePath);
            using var directory = OpenDirectories(segments);
        }

        public void Complete()
        {
            var root = Root;
            if (!HasSameNodeIdentity(ReadIdentity(root.Handle, _rootPath), root.Identity)
                || !HasSameNodeIdentity(
                    CaptureNodeIdentity(_rootPath, ImportNodeKind.Directory),
                    root.Identity))
            {
                throw Changed(_rootPath);
            }
        }

        public void Dispose()
        {
            _root?.Dispose();
            _root = null;
        }

        private OpenedImportNode? OpenDirectories(string[] segments)
        {
            OpenedImportNode? current = null;
            try
            {
                var path = _rootPath;
                foreach (var segment in segments)
                {
                    path = Path.Combine(path, segment);
                    var next = OpenOrCreateRelativeDirectory(
                        current?.Handle ?? Root.Handle,
                        segment,
                        path);
                    current?.Dispose();
                    current = next;
                }

                var result = current;
                current = null;
                return result;
            }
            finally
            {
                current?.Dispose();
            }
        }

        private OpenedImportNode Root => _root
            ?? throw new ObjectDisposedException(nameof(ImportTreeWriter));

        private static string[] SplitRelativePath(string relativePath)
            => NormalizeRelativePath(relativePath, int.MaxValue).Split('/');
    }

    public static async Task CopyFileToAsync(
        ImportTreeFile file,
        Stream destination,
        CancellationToken cancellationToken,
        Action<ImportReadCheckpoint, ImportTreeFile>? checkpoint = null,
        IncrementalHash? copiedContentHash = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(destination);
        ValidateDirectories(file.Ancestors, cancellationToken);
        checkpoint?.Invoke(ImportReadCheckpoint.BeforeSourceOpen, file);
        await using var source = OpenRegularFile(file.SourcePath);
        if (source.Identity != file.Identity)
        {
            throw Changed(file.RelativePath);
        }
        checkpoint?.Invoke(ImportReadCheckpoint.AfterSourceOpen, file);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long copied = 0;
        try
        {
            while (true)
            {
                var read = await source.Stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                copied = checked(copied + read);
                if (copied > file.Length)
                {
                    throw Changed(file.RelativePath);
                }
                hash.AppendData(buffer.AsSpan(0, read));
                copiedContentHash?.AppendData(buffer.AsSpan(0, read));
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        checkpoint?.Invoke(ImportReadCheckpoint.AfterSourceRead, file);
        if (copied != file.Length
            || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), file.Sha256))
        {
            throw Changed(file.RelativePath);
        }
        EnsureHandleUnchanged(source, file.SourcePath);
        EnsurePathStillReferences(file.SourcePath, file.Identity);
        ValidateDirectories(file.Ancestors, cancellationToken);
    }

    public static string NormalizeRelativePath(string path, int maxDepth)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            throw new InvalidDataException($"Import path is not relative: '{path}'.");
        }

        var segments = path.Replace('\\', '/').Split('/');
        if (segments.Length > maxDepth
            || segments.Any(segment => segment is "" or "." or ".."
                                       || segment.IndexOfAny(['\0', ':']) >= 0))
        {
            throw new InvalidDataException(
                $"Import path is invalid or exceeds the {maxDepth}-segment depth limit: '{path}'.");
        }
        return string.Join('/', segments);
    }

    public static void ValidateDirectoryPath(string path)
    {
        var snapshots = CaptureAncestorDirectories(
            CanonicalizePlatformPath(path),
            includeLeaf: true,
            CancellationToken.None);
        ValidateDirectories(snapshots, CancellationToken.None);
    }

    public static void ValidateDirectoryOrMissing(string path)
    {
        var fullPath = CanonicalizePlatformPath(path);
        CaptureAncestorDirectories(fullPath, includeLeaf: false, CancellationToken.None);
        try
        {
            _ = CaptureNodeIdentity(fullPath, ImportNodeKind.Directory);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static byte[] HashValidatedFile(
        OpenedImportFile source,
        string path,
        ImportNodeIdentity expectedIdentity,
        CancellationToken cancellationToken)
    {
        if (source.Identity != expectedIdentity)
        {
            throw Changed(path);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long readTotal = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = source.Stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }
                readTotal = checked(readTotal + read);
                if (readTotal > expectedIdentity.Length)
                {
                    throw Changed(path);
                }
                hash.AppendData(buffer.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (readTotal != expectedIdentity.Length)
        {
            throw Changed(path);
        }
        EnsureHandleUnchanged(source, path);
        EnsurePathStillReferences(path, expectedIdentity);
        return hash.GetHashAndReset();
    }

    private static IReadOnlyList<ImportDirectorySnapshot> CaptureAncestorDirectories(
        string path,
        bool includeLeaf,
        CancellationToken cancellationToken,
        bool trackLeafChanges = false)
    {
        var directoryPath = includeLeaf ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new InvalidDataException($"Import path does not have a filesystem root: '{path}'.");
        }

        var snapshots = new List<ImportDirectorySnapshot>();
        foreach (var current in EnumeratePathFromRoot(directoryPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshots.Add(new ImportDirectorySnapshot(
                current,
                CaptureNodeIdentity(current, ImportNodeKind.Directory),
                TrackChanges: trackLeafChanges && PathComparer.Equals(current, directoryPath)));
        }
        return snapshots;
    }

    private static IReadOnlyList<ImportDirectorySnapshot> GetAncestorSnapshots(
        string filePath,
        IReadOnlyDictionary<string, ImportDirectorySnapshot> snapshots)
    {
        var result = new List<ImportDirectorySnapshot>();
        var directory = Path.GetDirectoryName(filePath);
        while (directory is not null)
        {
            if (snapshots.TryGetValue(directory, out var snapshot))
            {
                result.Add(snapshot);
            }
            var parent = Path.GetDirectoryName(directory);
            if (parent is null || PathComparer.Equals(parent, directory))
            {
                break;
            }
            directory = parent;
        }
        result.Reverse();
        return result;
    }

    private static IEnumerable<string> EnumeratePathFromRoot(string path)
    {
        var fullPath = CanonicalizePlatformPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidDataException($"Import path does not have a filesystem root: '{path}'.");
        yield return root;
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".")
        {
            yield break;
        }

        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            yield return current;
        }
    }

    private static void ValidateDirectories(
        IEnumerable<ImportDirectorySnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = CaptureNodeIdentity(snapshot.Path, ImportNodeKind.Directory);
            if (!HasSameNodeIdentity(current, snapshot.Identity)
                || snapshot.TrackChanges && current != snapshot.Identity)
            {
                throw Changed(snapshot.Path);
            }
        }
    }

    private static bool HasSameNodeIdentity(
        ImportNodeIdentity current,
        ImportNodeIdentity expected)
        => current.Volume == expected.Volume
           && current.FileId == expected.FileId
           && current.FileIdHigh == expected.FileIdHigh
           && current.Kind == expected.Kind;

    private static void EnsureHandleUnchanged(OpenedImportFile source, string path)
        => EnsureHandleUnchanged(source.Stream.SafeFileHandle, source.Identity, path);

    private static void EnsureHandleUnchanged(
        SafeFileHandle handle,
        ImportNodeIdentity expectedIdentity,
        string path)
    {
        var current = ReadIdentity(handle, path);
        if (current != expectedIdentity)
        {
            throw Changed(path);
        }
    }

    private static void EnsurePathStillReferences(string path, ImportNodeIdentity expectedIdentity)
        => EnsurePathStillReferences(path, expectedIdentity, ImportNodeKind.RegularFile);

    private static void EnsurePathStillReferences(
        string path,
        ImportNodeIdentity expectedIdentity,
        ImportNodeKind requiredKind)
    {
        if (CaptureNodeIdentity(path, requiredKind) != expectedIdentity)
        {
            throw Changed(path);
        }
    }

    private static ImportNodeIdentity CaptureNodeIdentity(
        string path,
        ImportNodeKind? requiredKind = null)
    {
        using var node = OpenPathNoFollow(path, requiredKind == ImportNodeKind.Directory);
        if (requiredKind is { } kind && node.Identity.Kind != kind)
        {
            throw new InvalidDataException(
                $"Import source is not a {Describe(kind)}: '{path}'.");
        }
        return node.Identity;
    }

    private static OpenedImportFile OpenRegularFile(string path)
    {
        using var node = OpenPathNoFollow(path, requireDirectory: false);
        return OpenRegularFile(node, path);
    }

    private static OpenedImportFile OpenRegularFile(OpenedImportNode node, string path)
    {
        var identity = node.Identity;
        if (identity.Kind != ImportNodeKind.RegularFile)
        {
            throw new InvalidDataException($"Import source is not a regular file: '{path}'.");
        }
        if (identity.LinkCount != 1)
        {
            throw new InvalidDataException($"Import source is a hard-linked file: '{path}'.");
        }

        var handle = node.DetachHandle();
        try
        {
            var stream = new FileStream(
                handle,
                FileAccess.Read,
                BufferSize,
                isAsync: false);
            handle = null!;
            return new OpenedImportFile(stream, identity);
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static OpenedImportNode OpenPathNoFollow(string path, bool requireDirectory)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "Secure imports are supported only on Linux, macOS, and Windows.");
        }

        var fullPath = CanonicalizePlatformPath(path);
        var rootPath = Path.GetPathRoot(fullPath)
                       ?? throw new InvalidDataException(
                           $"Import path does not have a filesystem root: '{path}'.");
        OpenedImportNode? current = null;
        try
        {
            current = OpenRootNoFollow(rootPath);
            var relativePath = Path.GetRelativePath(rootPath, fullPath);
            if (relativePath != ".")
            {
                var segments = relativePath.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries);
                for (var index = 0; index < segments.Length; index++)
                {
                    var segmentPath = Path.Combine(rootPath, Path.Combine(segments[..(index + 1)]));
                    var next = OpenRelativeNoFollow(
                        current.Handle,
                        segments[index],
                        requireDirectory: index < segments.Length - 1 || requireDirectory,
                        segmentPath);
                    current.Dispose();
                    current = next;
                }
            }

            if (requireDirectory && current.Identity.Kind != ImportNodeKind.Directory)
            {
                throw new InvalidDataException($"Import source is not a directory: '{path}'.");
            }

            var result = current;
            current = null;
            return result;
        }
        finally
        {
            current?.Dispose();
        }
    }

    private static OpenedImportNode OpenRootNoFollow(string rootPath)
    {
        var handle = OperatingSystem.IsWindows() ? OpenWindowsRoot(rootPath) : OpenUnixRoot(rootPath);
        return CreateOpenedNode(handle, rootPath, ImportNodeKind.Directory);
    }

    private static OpenedImportNode OpenRelativeNoFollow(
        SafeFileHandle parent,
        string name,
        bool requireDirectory,
        string displayPath)
    {
        ValidatePathComponent(name);
        var handle = OperatingSystem.IsWindows()
            ? OpenWindowsRelative(
                parent,
                name,
                WindowsGenericRead,
                requireDirectory ? ImportNodeKind.Directory : null,
                WindowsFileOpen,
                WindowsShareAll,
                displayPath)
            : OpenUnixRelative(parent, name, requireDirectory, displayPath);

        return CreateOpenedNode(
            handle,
            displayPath,
            requireDirectory ? ImportNodeKind.Directory : null);
    }

    private static OpenedImportNode OpenOrCreateRelativeDirectory(
        SafeFileHandle parent,
        string name,
        string displayPath)
    {
        ValidatePathComponent(name);
        if (OperatingSystem.IsWindows())
        {
            var handle = OpenWindowsRelative(
                parent,
                name,
                WindowsGenericRead,
                ImportNodeKind.Directory,
                WindowsFileOpenIf,
                WindowsShareAll,
                displayPath);
            return CreateOpenedNode(handle, displayPath, ImportNodeKind.Directory);
        }

        CreateUnixDirectory(parent, name, displayPath);
        return OpenRelativeNoFollow(parent, name, requireDirectory: true, displayPath);
    }

    private static SafeFileHandle CreateExclusiveRegularFile(
        SafeFileHandle parent,
        string name,
        string displayPath)
    {
        ValidatePathComponent(name);
        if (OperatingSystem.IsWindows())
        {
            return OpenWindowsRelative(
                parent,
                name,
                // Identity validation queries attributes on this same exclusive handle.
                WindowsGenericWrite | WindowsReadAttributes,
                ImportNodeKind.RegularFile,
                WindowsFileCreate,
                shareMode: 0,
                displayPath);
        }

        return CreateUnixRegularFile(parent, name, displayPath);
    }

    private static OpenedImportNode CreateOpenedNode(
        SafeFileHandle handle,
        string path,
        ImportNodeKind? requiredKind)
    {
        try
        {
            var identity = ReadIdentity(handle, path);
            if (requiredKind is { } kind && identity.Kind != kind)
            {
                throw new InvalidDataException(
                    $"Import source is not a {Describe(kind)}: '{path}'.");
            }
            if (identity.Kind == ImportNodeKind.RegularFile && identity.LinkCount != 1)
            {
                throw new InvalidDataException($"Import source is a hard-linked file: '{path}'.");
            }
            var node = new OpenedImportNode(handle, identity);
            handle = null!;
            return node;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static IEnumerable<string> EnumerateDirectoryNames(
        SafeFileHandle directory,
        string directoryPath)
    {
        var names = OperatingSystem.IsWindows()
            ? EnumerateWindowsDirectoryNames(directory, directoryPath)
            : EnumerateUnixDirectoryNames(directory, directoryPath);
        foreach (var name in names)
        {
            ValidatePathComponent(name);
            yield return name;
        }
    }

    private static void ValidatePathComponent(string name)
    {
        if (string.IsNullOrEmpty(name)
            || name is "." or ".."
            || name.IndexOfAny(['/', '\\', '\0']) >= 0
            || OperatingSystem.IsWindows() && name.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidDataException("Import path contains an invalid filesystem component.");
        }
    }

    private static ImportNodeIdentity ReadIdentity(SafeFileHandle handle, string path)
        => OperatingSystem.IsWindows() ? ReadWindowsIdentity(handle, path) : ReadUnixIdentity(handle, path);

    private static void ThrowOpenError(string path, int error)
    {
        var inner = new Win32Exception(error);
        if (error is 2 or 3)
        {
            throw new FileNotFoundException($"Import source does not exist: '{path}'.", path, inner);
        }
        if (error is 13 or 5)
        {
            throw new UnauthorizedAccessException($"Import source is inaccessible: '{path}'.", inner);
        }
        throw new InvalidDataException(
            $"Import source could not be opened without following links: '{path}'.",
            inner);
    }

    private static string CanonicalizePlatformPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsMacOS())
        {
            return fullPath;
        }
        if (fullPath == "/var" || fullPath.StartsWith("/var/", StringComparison.Ordinal)
            || fullPath == "/tmp" || fullPath.StartsWith("/tmp/", StringComparison.Ordinal)
            || fullPath == "/etc" || fullPath.StartsWith("/etc/", StringComparison.Ordinal))
        {
            return "/private" + fullPath;
        }
        return fullPath;
    }

}
