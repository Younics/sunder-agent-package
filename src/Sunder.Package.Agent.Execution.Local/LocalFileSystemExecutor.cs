using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Local;

internal static class LocalFileSystemExecutor
{
    public static async ValueTask<AgentFileReadResult> ReadFileAsync(
        LocalExecutionWorkspaceConfig config,
        AgentFileReadRequest request,
        bool allowOutsideConfiguredScope,
        CancellationToken cancellationToken)
    {
        var path = LocalPathResolver.ResolvePath(config, request.Path, allowOutsideConfiguredScope);
        if (Directory.Exists(path))
        {
            var entries = Directory.EnumerateFileSystemEntries(path)
                .Select(entry => Directory.Exists(entry) ? Path.GetFileName(entry) + Path.DirectorySeparatorChar : Path.GetFileName(entry))
                .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase);
            return new AgentFileReadResult(path, string.Join(Environment.NewLine, entries), IsDirectory: true);
        }

        if (!File.Exists(path))
        {
            return new AgentFileReadResult(path, $"File not found: {path}");
        }

        if (await IsBinaryFileAsync(path, cancellationToken))
        {
            throw new InvalidOperationException($"Binary file reads are not supported: {path}");
        }

        return new AgentFileReadResult(path, await File.ReadAllTextAsync(path, cancellationToken));
    }

    public static async ValueTask<AgentFileMutationResult> WriteFileAsync(
        LocalExecutionWorkspaceConfig config,
        AgentFileWriteRequest request,
        bool allowOutsideConfiguredScope,
        CancellationToken cancellationToken)
    {
        var path = LocalPathResolver.ResolvePath(config, request.Path, allowOutsideConfiguredScope);
        if (!request.Overwrite && File.Exists(path))
        {
            return new AgentFileMutationResult(path, "File already exists.", IsError: true, ErrorCode: "file-exists");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? LocalPathResolver.ResolveRoot(config));
        await File.WriteAllTextAsync(path, request.Content, cancellationToken);
        return new AgentFileMutationResult(path, $"Wrote {request.Content.Length} character(s).");
    }

    public static ValueTask<AgentFileMutationResult> DeleteFileAsync(
        LocalExecutionWorkspaceConfig config,
        AgentFileDeleteRequest request,
        bool allowOutsideConfiguredScope)
    {
        var path = LocalPathResolver.ResolvePath(config, request.Path, allowOutsideConfiguredScope);
        if (File.Exists(path))
        {
            File.Delete(path);
            return ValueTask.FromResult(new AgentFileMutationResult(path, "File deleted."));
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, request.Recursive);
            return ValueTask.FromResult(new AgentFileMutationResult(path, "Directory deleted."));
        }

        return ValueTask.FromResult(new AgentFileMutationResult(path, "Path does not exist.", IsError: true, ErrorCode: "path-not-found"));
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
}
