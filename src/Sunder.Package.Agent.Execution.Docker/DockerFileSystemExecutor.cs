using System.Globalization;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Docker;

internal sealed class DockerFileSystemExecutor(IDockerCommandExecutor commandRunner)
{
    public async ValueTask<AgentResolvedResource> ResolveFileResourceAsync(
        DockerExecutionRuntimeConfig config,
        string containerName,
        string requestedPath,
        bool allowOutsideConfiguredScope,
        CancellationToken cancellationToken)
    {
        var path = DockerPathResolver.ResolvePath(config, requestedPath, allowOutsideConfiguredScope);
        var result = await commandRunner.RunAsync(
            BuildArguments(config, containerName, "exists", path, ranged: false, 1, 1, option: false, redirectStandardInput: false, expectedContentHash: null),
            await commandRunner.ResolveDefaultTimeoutSecondsAsync(cancellationToken),
            cancellationToken);
        if (!TryParseProtocol(result.Output, out var fields, out _) || result.ExitCode != 0)
        {
            throw new InvalidOperationException(BuildCommandFailureMessage("Docker path existence query failed", result));
        }

        var exists = fields[1] == "exists";
        if (!exists && fields[1] != "missing")
        {
            throw new InvalidOperationException("Docker path existence query returned an invalid response.");
        }

        return DockerPathResolver.ResolveFileResource(config, requestedPath, allowOutsideConfiguredScope, exists);
    }

    public async ValueTask<AgentFileReadResult> ReadFileAsync(
        DockerExecutionRuntimeConfig config,
        string containerName,
        AgentFileReadRequest request,
        bool allowOutsideConfiguredScope,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRange(request, out var rangeError))
        {
            return AgentFileReadResult.Failure(request.Path, AgentFileReadErrorCodes.InvalidRange, rangeError!);
        }

        if (!TryResolvePath(config, request.Path, allowOutsideConfiguredScope, out var path, out var pathError))
        {
            return pathError!;
        }

        var ranged = request.Offset is not null || request.Limit is not null;
        var result = await commandRunner.RunAsync(
            BuildArguments(
                config,
                containerName,
                "read",
                path!,
                ranged,
                request.Offset ?? 1,
                request.Limit ?? 2000,
                option: false,
                redirectStandardInput: false,
                expectedContentHash: null),
            await commandRunner.ResolveDefaultTimeoutSecondsAsync(cancellationToken),
            cancellationToken);

        if (result.TimedOut)
        {
            return Failure(path!, AgentFileReadErrorCodes.TimedOut, "Docker file read timed out.", result.WasTruncated);
        }

        if (!TryParseProtocol(result.Output, out var fields, out var payload))
        {
            return Failure(
                path!,
                AgentFileReadErrorCodes.ReadFailed,
                BuildCommandFailureMessage("Docker file read failed", result),
                result.WasTruncated);
        }

        if (fields[1] == "error")
        {
            var errorCode = fields.Length > 2 ? fields[2] : AgentFileReadErrorCodes.ReadFailed;
            var failure = Failure(path!, errorCode, BuildReadErrorMessage(errorCode, path!), result.WasTruncated);
            return errorCode == AgentFileReadErrorCodes.RangeOutsideFile
                   && fields.Length > 3
                   && TryParseNonNegativeInt(fields[3], out var errorTotalLines)
                ? failure with { TotalLines = errorTotalLines }
                : failure;
        }

        if (result.ExitCode != 0)
        {
            return Failure(
                path!,
                AgentFileReadErrorCodes.ReadFailed,
                BuildCommandFailureMessage("Docker file read failed", result),
                result.WasTruncated);
        }

        if (fields[1] == "directory")
        {
            return new AgentFileReadResult(path!, RemoveSingleLineTerminator(payload), IsDirectory: true, result.WasTruncated);
        }

        if (fields[1] != "file"
            || fields.Length < 7
            || !TryParseNonNegativeInt(fields[2], out var startLine)
            || !TryParseNonNegativeInt(fields[3], out var endLine)
            || !TryParseNonNegativeInt(fields[4], out var totalLines)
            || fields[5] is not ("0" or "1")
            || fields[6] is not ("0" or "1"))
        {
            return Failure(path!, AgentFileReadErrorCodes.ReadFailed, "Docker file read returned an invalid response.", result.WasTruncated);
        }

        var rangeWasTruncated = fields[5] == "1";
        var content = fields[6] == "1" ? RemoveSingleLineTerminator(payload) : payload;
        return new AgentFileReadResult(path!, content, WasTruncated: result.WasTruncated || rangeWasTruncated)
        {
            StartLine = startLine,
            EndLine = endLine,
            TotalLines = totalLines,
        };
    }

    public async ValueTask<AgentFileMutationResult> WriteFileAsync(
        DockerExecutionRuntimeConfig config,
        string containerName,
        AgentFileWriteRequest request,
        bool allowOutsideConfiguredScope,
        CancellationToken cancellationToken)
    {
        string path;
        try
        {
            path = DockerPathResolver.ResolvePath(config, request.Path, allowOutsideConfiguredScope);
        }
        catch (InvalidOperationException ex)
        {
            return new AgentFileMutationResult(request.Path, ex.Message, IsError: true, ErrorCode: AgentFileReadErrorCodes.OutsideConfiguredScope);
        }

        var result = await commandRunner.RunAsync(
            BuildArguments(config, containerName, "write", path, ranged: false, 1, 2000, request.Overwrite, redirectStandardInput: true, request.ExpectedContentHash),
            await commandRunner.ResolveDefaultTimeoutSecondsAsync(cancellationToken),
            cancellationToken,
            request.Content);
        if (TryParseProtocol(result.Output, out var fields, out _))
        {
            if (fields[1] == "ok" && fields.ElementAtOrDefault(2) == "file-written" && result.ExitCode == 0)
            {
                return new AgentFileMutationResult(path, $"Wrote {request.Content.Length} character(s).");
            }

            if (fields[1] == "error")
            {
                var errorCode = fields.ElementAtOrDefault(2) ?? "docker-write-failed";
                var message = errorCode switch
                {
                    "file-exists" => "File already exists.",
                    AgentFileReadErrorCodes.OutsideConfiguredScope => $"Path resolves outside the configured Docker workspace paths: {path}",
                    AgentFileReadErrorCodes.PathCanonicalizationFailed => $"Unable to securely resolve path inside the Docker container: {path}",
                    AgentFileReadErrorCodes.NotAFile => "The write target is not a regular file.",
                    "file-content-changed" => "The file changed after patch preflight; no mutation was applied.",
                    "file-hash-unavailable" => "The container cannot verify the expected file content hash.",
                    _ => BuildCommandFailureMessage("Docker file write failed", result),
                };
                return new AgentFileMutationResult(path, message, IsError: true, ErrorCode: errorCode);
            }
        }

        return new AgentFileMutationResult(path, BuildCommandFailureMessage("Docker file write failed", result), IsError: true, ErrorCode: "docker-write-failed");
    }

    public async ValueTask<AgentFileMutationResult> DeleteFileAsync(
        DockerExecutionRuntimeConfig config,
        string containerName,
        AgentFileDeleteRequest request,
        bool allowOutsideConfiguredScope,
        CancellationToken cancellationToken)
    {
        string path;
        try
        {
            path = DockerPathResolver.ResolvePath(config, request.Path, allowOutsideConfiguredScope);
        }
        catch (InvalidOperationException ex)
        {
            return new AgentFileMutationResult(request.Path, ex.Message, IsError: true, ErrorCode: AgentFileReadErrorCodes.OutsideConfiguredScope);
        }

        var result = await commandRunner.RunAsync(
            BuildArguments(config, containerName, "delete", path, ranged: false, 1, 2000, request.Recursive, redirectStandardInput: false, request.ExpectedContentHash),
            await commandRunner.ResolveDefaultTimeoutSecondsAsync(cancellationToken),
            cancellationToken);
        if (TryParseProtocol(result.Output, out var fields, out _))
        {
            if (fields[1] == "ok" && result.ExitCode == 0)
            {
                return fields.ElementAtOrDefault(2) == "directory-deleted"
                    ? new AgentFileMutationResult(path, "Directory deleted.")
                    : new AgentFileMutationResult(path, "File deleted.");
            }

            if (fields[1] == "error")
            {
                var errorCode = fields.ElementAtOrDefault(2) ?? "docker-delete-failed";
                var message = errorCode switch
                {
                    "path-not-found" => "Path does not exist.",
                    AgentFileReadErrorCodes.OutsideConfiguredScope => $"Path resolves outside the configured Docker workspace paths: {path}",
                    AgentFileReadErrorCodes.PathCanonicalizationFailed => $"Unable to securely resolve path inside the Docker container: {path}",
                    AgentFileReadErrorCodes.NotAFile => "The delete target is not a regular file or directory.",
                    "file-content-changed" => "The file changed after patch preflight; no mutation was applied.",
                    "file-hash-unavailable" => "The container cannot verify the expected file content hash.",
                    _ => BuildCommandFailureMessage("Docker path deletion failed", result),
                };
                return new AgentFileMutationResult(path, message, IsError: true, ErrorCode: errorCode);
            }
        }

        return new AgentFileMutationResult(path, BuildCommandFailureMessage("Docker path deletion failed", result), IsError: true, ErrorCode: "docker-delete-failed");
    }

    internal static IReadOnlyList<string> BuildArguments(
        DockerExecutionRuntimeConfig config,
        string containerName,
        string operation,
        string path,
        bool ranged,
        int offset,
        int limit,
        bool option,
        bool redirectStandardInput,
        string? expectedContentHash)
    {
        var arguments = new List<string>
        {
            "exec",
        };
        if (redirectStandardInput)
        {
            arguments.Add("-i");
        }

        arguments.AddRange(
        [
            containerName,
            DockerCommandRunner.ResolveShellPath(config),
            "-c",
            DockerFileOperationScript.Content,
            "sunder-file-operation",
            operation,
            path,
            ranged ? "1" : "0",
            offset.ToString(CultureInfo.InvariantCulture),
            limit.ToString(CultureInfo.InvariantCulture),
            option ? "1" : "0",
            expectedContentHash ?? "-",
        ]);
        arguments.AddRange(config.Mounts.Select(mount => mount.ContainerPath));
        return arguments;
    }

    private static bool TryResolvePath(
        DockerExecutionRuntimeConfig config,
        string requestedPath,
        bool allowOutsideConfiguredScope,
        out string? path,
        out AgentFileReadResult? error)
    {
        try
        {
            path = DockerPathResolver.ResolvePath(config, requestedPath, allowOutsideConfiguredScope);
            error = null;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            path = null;
            error = AgentFileReadResult.Failure(requestedPath, AgentFileReadErrorCodes.OutsideConfiguredScope, ex.Message);
            return false;
        }
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

    private static bool TryParseProtocol(string output, out string[] fields, out string payload)
    {
        var lineEnd = output.IndexOf('\n');
        var header = (lineEnd < 0 ? output : output[..lineEnd]).TrimEnd('\r');
        fields = header.Split('|');
        payload = lineEnd < 0 ? string.Empty : output[(lineEnd + 1)..];
        return fields.Length >= 2 && fields[0] == DockerFileOperationScript.Protocol;
    }

    private static bool TryParseNonNegativeInt(string value, out int parsed)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) && parsed >= 0;

    private static AgentFileReadResult Failure(string path, string errorCode, string message, bool wasTruncated)
        => AgentFileReadResult.Failure(path, errorCode, message) with { WasTruncated = wasTruncated };

    private static string BuildReadErrorMessage(string errorCode, string path)
        => errorCode switch
        {
            AgentFileReadErrorCodes.FileNotFound => $"File not found: {path}",
            AgentFileReadErrorCodes.NotAFile => $"Path is not a regular file or directory: {path}",
            AgentFileReadErrorCodes.BinaryFile => $"Binary file reads are not supported: {path}",
            AgentFileReadErrorCodes.OutsideConfiguredScope => $"Path resolves outside the configured Docker workspace paths: {path}",
            AgentFileReadErrorCodes.InvalidRange => "The requested file range is invalid.",
            AgentFileReadErrorCodes.RangeOutsideFile => "The requested line offset is outside the file.",
            AgentFileReadErrorCodes.PathCanonicalizationFailed => $"Unable to securely resolve path inside the Docker container: {path}",
            _ => $"Unable to read file: {path}",
        };

    private static string BuildCommandFailureMessage(string prefix, DockerCliRunResult result)
        => string.IsNullOrWhiteSpace(result.Output) ? $"{prefix} (exit code {result.ExitCode})." : $"{prefix}: {result.Output.Trim()}";

    private static string RemoveSingleLineTerminator(string value)
        => value.EndsWith("\r\n", StringComparison.Ordinal)
            ? value[..^2]
            : value.EndsWith('\n')
                ? value[..^1]
                : value;
}
