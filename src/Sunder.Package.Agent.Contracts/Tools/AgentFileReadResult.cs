namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentFileReadResult(
    string Path,
    string Content,
    bool IsDirectory = false,
    bool WasTruncated = false)
{
    public bool IsError { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public int? StartLine { get; init; }

    public int? EndLine { get; init; }

    public int? TotalLines { get; init; }

    public static AgentFileReadResult Failure(string path, string errorCode, string errorMessage)
        => new(path, string.Empty)
        {
            IsError = true,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
        };
}

public static class AgentFileReadErrorCodes
{
    public const string FileNotFound = "file-not-found";
    public const string NotAFile = "file-not-regular";
    public const string BinaryFile = "file-binary";
    public const string OutsideConfiguredScope = "file-outside-scope";
    public const string InvalidRange = "file-range-invalid";
    public const string RangeOutsideFile = "file-range-out-of-bounds";
    public const string PathCanonicalizationFailed = "file-path-unresolvable";
    public const string ReadFailed = "file-read-failed";
    public const string TimedOut = "file-read-timeout";
    public const string TooLarge = "file-too-large";
}
