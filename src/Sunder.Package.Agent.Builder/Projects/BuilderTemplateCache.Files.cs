namespace Sunder.Package.Agent.Builder;

internal sealed partial class BuilderTemplateCache
{
    private const int MaximumMarkerBytes = 32 * 1024;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OtherPermissions = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private static string CanonicalPath(string path)
    {
        path = BuilderJobValidation.Absolute(path, "Builder template cache path");
        // macOS exposes these OS-owned aliases in Path.GetTempPath(). Account for
        // their real prefix in both the byte budget and ownership identity. All
        // other linked ancestors are rejected, including links above the base.
        if (OperatingSystem.IsMacOS())
            foreach (var alias in new[] { "/var", "/tmp" })
                if (path.StartsWith(alias + "/", StringComparison.Ordinal) && new DirectoryInfo(alias).LinkTarget is not null)
                    path = (new DirectoryInfo(alias).ResolveLinkTarget(true)?.FullName ?? throw new IOException("Cannot resolve the macOS temporary directory.")) + path[alias.Length..];
        return path;
    }

    private static void CheckAncestors(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            BuilderToolchainFiles.RejectLink(current);
            if (File.Exists(current)) throw Unsafe(current, "is a file rather than a directory");
            if (!Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.Device) != 0) throw Unsafe(current, "is not a regular directory");
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(current);
                if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0 && (mode & UnixFileMode.StickyBit) == 0)
                    throw Unsafe(current, "has an ancestor writable by other users");
            }
        }
    }

    private static void RequirePrivateDirectory(string path)
    {
        CheckAncestors(path);
        if (!Directory.Exists(path)) throw Unsafe(path, "is missing");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & OtherPermissions) != 0)
            throw Unsafe(path, "is not private to its user");
    }

    private static async Task EnsureOwnedDirectoryAsync(string path, byte[] marker, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CheckAncestors(path);
        if (!Directory.Exists(path))
        {
            var parent = Path.GetDirectoryName(path)!;
            BuilderToolchainFiles.CreatePrivateDirectory(parent);
            CheckAncestors(parent);
            var staging = Path.Combine(parent, ".bt-init-" + Guid.NewGuid().ToString("N"));
            BuilderToolchainFiles.CreatePrivateDirectory(staging);
            try
            {
                await WriteNewMarkerAsync(Path.Combine(staging, OwnerName), marker, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                CheckAncestors(path);
                try { Directory.Move(staging, path); }
                catch (IOException) when (Directory.Exists(path)) { /* Another activation initialized it. Verify its full owner below. */ }
            }
            finally
            {
                // Only our unpublished staging marker/directory, never pre-existing cache state.
                if (Directory.Exists(staging))
                {
                    BuilderToolchainFiles.RejectLink(staging);
                    File.Delete(Path.Combine(staging, OwnerName));
                    Directory.Delete(staging, recursive: false);
                }
            }
        }
        RequirePrivateDirectory(path);
        var ownerPath = Path.Combine(path, OwnerName);
        CheckStateFile(ownerPath);
        if (!File.Exists(ownerPath))
        {
            if (Directory.EnumerateFileSystemEntries(path).Any()) throw Unsafe(path, "is nonempty and has no Builder ownership marker");
            // An explicitly supplied empty private directory is also allowed. The
            // atomic marker publication below never replaces another owner's marker.
            await WriteNewMarkerAsync(ownerPath, marker, token).ConfigureAwait(false);
        }
        RequireMarker(ownerPath, marker);
    }

    private static void RequireOwner(string directory, byte[] marker)
    {
        RequirePrivateDirectory(directory);
        RequireMarker(Path.Combine(directory, OwnerName), marker);
    }

    private static void CheckStateFile(string path, int maximumBytes = MaximumMarkerBytes)
    {
        CheckAncestors(Path.GetDirectoryName(path)!);
        BuilderToolchainFiles.RejectLink(path);
        if (Directory.Exists(path)) throw Unsafe(path, "is a directory rather than a state file");
        if (!File.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.Device) != 0 || new FileInfo(path).Length > maximumBytes)
            throw Unsafe(path, "is not a bounded regular state file");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & OtherPermissions) != 0)
            throw Unsafe(path, "is not private to its user");
    }

    private static void RequireMarker(string path, byte[] expected)
    {
        CheckStateFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != expected.Length) throw Unsafe(path, "does not match this Builder cache's full identity");
        var actual = new byte[expected.Length];
        stream.ReadExactly(actual);
        if (stream.ReadByte() != -1 || !actual.AsSpan().SequenceEqual(expected)) throw Unsafe(path, "does not match this Builder cache's full identity");
        CheckStateFile(path);
    }

    private static async Task WriteNewMarkerAsync(string path, byte[] bytes, CancellationToken token)
    {
        if (bytes.Length > MaximumMarkerBytes) throw Unsafe(path, "exceeds the state size limit");
        CheckStateFile(path);
        // A sibling temporary file also permits concurrent claims of an existing
        // empty directory without making that directory look unmarked/nonempty.
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path))!, ".bt-state-" + Guid.NewGuid().ToString("N"));
        var created = false;
        try
        {
            await using (var stream = new FileStream(temporary, PrivateFileOptions(FileMode.CreateNew, FileAccess.Write, FileShare.None)))
            {
                created = true;
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            CheckStateFile(path);
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { RequireMarker(path, bytes); }
        }
        finally { if (created) File.Delete(temporary); }
    }

    private static FileStreamOptions PrivateFileOptions(FileMode mode, FileAccess access, FileShare share)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = share };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFileMode;
        return options;
    }

    private static void CheckHomeTree(string home)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((home, 0));
        var count = 0;
        while (pending.TryPop(out var directory))
            foreach (var entry in new DirectoryInfo(directory.Path).EnumerateFileSystemInfos())
            {
                if (++count > 16384 || directory.Depth > 32) throw Unsafe(home, "exceeds the template state inspection limit");
                if (entry.LinkTarget is not null || (entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw Unsafe(entry.FullName, "contains linked or special template state");
                if (entry is DirectoryInfo) pending.Push((entry.FullName, directory.Depth + 1));
            }
    }

    private static IOException Unsafe(string path, string reason) => new(
        $"Builder template cache '{path}' {reason}. Choose a new short private DotnetTemplateCacheRootPath; existing state was not adopted or repaired.");
}
