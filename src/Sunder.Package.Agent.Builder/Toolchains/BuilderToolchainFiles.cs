using System.Text.Json;

namespace Sunder.Package.Agent.Builder;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class BuilderToolchainFiles
{
    internal const string ReceiptName = "builder-toolchain-receipt.json";
    internal static StringComparison PathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static bool IsChild(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, PathComparison);

    internal static void RejectLink(string path)
    {
        var info = new FileInfo(path);
        var directory = new DirectoryInfo(path);
        if (info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0) ||
            directory.LinkTarget is not null || (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new IOException($"Toolchain storage must not be redirected through a link: {path}");
    }

    internal static void CheckPath(string root, string path)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(full, root, PathComparison) && !IsChild(full, root)) throw new IOException("The path is outside Builder toolchain storage.");
        RejectLink(root);
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar))
        {
            if (segment == ".") continue;
            current = Path.Combine(current, segment);
            RejectLink(current);
        }
    }

    internal static void CreatePrivateDirectory(string path)
    {
        RejectLink(path);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken token)
    {
        RejectLink(path);
        if (!File.Exists(path)) return default;
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Toolchain state exceeds its size limit.");
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: token).ConfigureAwait(false);
    }

    internal static async Task WriteJsonAsync<T>(string path, T value, CancellationToken token)
    {
        RejectLink(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, value, cancellationToken: token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal static void DeleteTree(string path)
    {
        RejectLink(path);
        if (!Directory.Exists(path)) return;
        foreach (var file in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.LinkTarget is not null)
                file.Delete();
            else if (file is DirectoryInfo directory) DeleteTree(directory.FullName);
            else { file.Attributes &= ~FileAttributes.ReadOnly; file.Delete(); }
        }
        Directory.Delete(path);
    }
}
