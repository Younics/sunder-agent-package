using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Docker;

internal sealed class DockerFileSystemExecutor(DockerCommandRunner commandRunner)
{
    private const string DirectoryMarker = "__SUNDER_DIRECTORY__";
    private const string NotFoundMarker = "__SUNDER_NOT_FOUND__";
    private const string BinaryMarker = "__SUNDER_BINARY__";
    private const string FileDeletedMarker = "__SUNDER_FILE_DELETED__";
    private const string DirectoryDeletedMarker = "__SUNDER_DIRECTORY_DELETED__";

    public async ValueTask<AgentFileReadResult> ReadFileAsync(
        DockerExecutionWorkspaceConfig config,
        string containerName,
        AgentFileReadRequest request,
        bool allowOutsideConfiguredScope,
        CancellationToken cancellationToken)
    {
        var path = DockerPathResolver.ResolvePath(config, request.Path, allowOutsideConfiguredScope);
        var quotedPath = DockerCommandRunner.Quote(path);
        var command = string.Join(' ',
        [
            $"if [ -d {quotedPath} ]; then printf '%s\\n' {DockerCommandRunner.Quote(DirectoryMarker)}; ls -1A {quotedPath};",
            $"elif [ ! -e {quotedPath} ]; then printf '%s\\n' {DockerCommandRunner.Quote(NotFoundMarker)};",
            $"elif [ -f {quotedPath} ] && command -v od >/dev/null 2>&1 && command -v grep >/dev/null 2>&1 && od -An -tx1 -N 8192 {quotedPath} | grep -q ' 00'; then printf '%s\\n' {DockerCommandRunner.Quote(BinaryMarker)};",
            $"else cat {quotedPath}; fi",
        ]);
        var result = await commandRunner.RunAsync(
            ["exec", containerName, DockerCommandRunner.ResolveShellPath(config), "-c", command],
            commandRunner.ResolveDefaultTimeoutSeconds(),
            cancellationToken);

        if (TryConsumeMarker(result.Output, DirectoryMarker, out var directoryContent))
        {
            return new AgentFileReadResult(path, directoryContent, IsDirectory: true, result.WasTruncated);
        }

        if (TryConsumeMarker(result.Output, NotFoundMarker, out _))
        {
            return new AgentFileReadResult(path, $"File not found: {path}", IsDirectory: false, result.WasTruncated);
        }

        if (TryConsumeMarker(result.Output, BinaryMarker, out _))
        {
            throw new InvalidOperationException($"Binary file reads are not supported: {path}");
        }

        return new AgentFileReadResult(path, result.Output, IsDirectory: false, result.WasTruncated);
    }

    public async ValueTask<AgentFileMutationResult> WriteFileAsync(
        DockerExecutionWorkspaceConfig config,
        string containerName,
        AgentFileWriteRequest request,
        bool allowOutsideConfiguredScope,
        CancellationToken cancellationToken)
    {
        var path = DockerPathResolver.ResolvePath(config, request.Path, allowOutsideConfiguredScope);
        var overwriteGuard = request.Overwrite ? string.Empty : $"if [ -e {DockerCommandRunner.Quote(path)} ]; then exit 73; fi && ";
        var command = $"{overwriteGuard}mkdir -p {DockerCommandRunner.Quote(GetDirectoryName(path))} && cat > {DockerCommandRunner.Quote(path)}";
        var result = await commandRunner.RunAsync(
            ["exec", "-i", containerName, DockerCommandRunner.ResolveShellPath(config), "-c", command],
            commandRunner.ResolveDefaultTimeoutSeconds(),
            cancellationToken,
            request.Content);
        if (result.ExitCode == 73)
        {
            return new AgentFileMutationResult(path, "File already exists.", IsError: true, ErrorCode: "file-exists");
        }

        return result.ExitCode == 0
            ? new AgentFileMutationResult(path, $"Wrote {request.Content.Length} character(s).")
            : new AgentFileMutationResult(path, result.Output, IsError: true, ErrorCode: "docker-write-failed");
    }

    public async ValueTask<AgentFileMutationResult> DeleteFileAsync(
        DockerExecutionWorkspaceConfig config,
        string containerName,
        AgentFileDeleteRequest request,
        bool allowOutsideConfiguredScope,
        CancellationToken cancellationToken)
    {
        var path = DockerPathResolver.ResolvePath(config, request.Path, allowOutsideConfiguredScope);
        var quotedPath = DockerCommandRunner.Quote(path);
        var deleteDirectoryCommand = request.Recursive ? $"rm -rf {quotedPath}" : $"rmdir {quotedPath}";
        var command = string.Join(' ',
        [
            $"if [ -f {quotedPath} ] || [ -L {quotedPath} ]; then rm -f {quotedPath} && printf '%s\\n' {DockerCommandRunner.Quote(FileDeletedMarker)};",
            $"elif [ -d {quotedPath} ]; then {deleteDirectoryCommand} && printf '%s\\n' {DockerCommandRunner.Quote(DirectoryDeletedMarker)};",
            $"else printf '%s\\n' {DockerCommandRunner.Quote(NotFoundMarker)}; exit 74; fi",
        ]);
        var result = await commandRunner.RunAsync(
            ["exec", containerName, DockerCommandRunner.ResolveShellPath(config), "-c", command],
            commandRunner.ResolveDefaultTimeoutSeconds(),
            cancellationToken);

        if (TryConsumeMarker(result.Output, FileDeletedMarker, out _))
        {
            return new AgentFileMutationResult(path, "File deleted.");
        }

        if (TryConsumeMarker(result.Output, DirectoryDeletedMarker, out _))
        {
            return new AgentFileMutationResult(path, "Directory deleted.");
        }

        if (result.ExitCode == 74 || TryConsumeMarker(result.Output, NotFoundMarker, out _))
        {
            return new AgentFileMutationResult(path, "Path does not exist.", IsError: true, ErrorCode: "path-not-found");
        }

        return result.ExitCode == 0
            ? new AgentFileMutationResult(path, "Path deleted.")
            : new AgentFileMutationResult(path, result.Output, IsError: true, ErrorCode: "docker-delete-failed");
    }

    private static bool TryConsumeMarker(string output, string marker, out string content)
    {
        if (!output.StartsWith(marker, StringComparison.Ordinal))
        {
            content = string.Empty;
            return false;
        }

        content = output[marker.Length..].TrimStart('\r', '\n');
        return true;
    }

    private static string GetDirectoryName(string path)
    {
        var index = path.LastIndexOf('/');
        return index <= 0 ? "/" : path[..index];
    }
}
