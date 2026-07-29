using System.Security.Cryptography;
using System.Text;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Agent.Execution.Common;

public static class FileOperation
{
    public const string FileExistsErrorCode = "file-exists";
    public const string ContentChangedErrorCode = "file-content-changed";
    public const string PathNotFoundErrorCode = "path-not-found";
    public const string StrictPlatformMutationUnavailableErrorCode = "strict-platform-mutation-unavailable";
    public const string StrictPlatformMutationUnavailableMessage =
        "Strict structured filesystem writes and deletes are unavailable on this platform.";
    public const string StrictMutationRecoveryRequiredErrorCode = "strict-mutation-recovery-required";
    public const string StrictMutationRecoveryRequiredMessage =
        "Strict filesystem mutation recovery could not be completed; recoverable state was retained under hidden reserved names.";

    public const int DefaultReadLimit = 2000;
    public const int MaximumReadLimit = 2000;

    public static bool TryValidateRange(int? offset, int? limit, out string? error)
    {
        if (offset is <= 0)
        {
            error = "File read offset must be greater than or equal to 1.";
            return false;
        }

        if (limit is <= 0 or > MaximumReadLimit)
        {
            error = $"File read limit must be between 1 and {MaximumReadLimit}.";
            return false;
        }

        error = null;
        return true;
    }

    public static int CountLines(string content)
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

    public static string ComputeContentHash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    public static AgentFileMutationResult Written(string path, int characterCount)
        => new(path, $"Wrote {characterCount} character(s).");

    public static AgentFileMutationResult FileDeleted(string path)
        => new(path, "File deleted.");

    public static AgentFileMutationResult DirectoryDeleted(string path)
        => new(path, "Directory deleted.");

    public static AgentFileMutationResult Failure(string path, string summary, string errorCode)
        => new(path, summary, IsError: true, ErrorCode: errorCode);

    public static AgentFileMutationResult ContentChanged(string path)
        => Failure(path, "The file changed after patch preflight; no mutation was applied.", ContentChangedErrorCode);
}
