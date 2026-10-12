namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Defines common execution-target file-read failure codes.</summary>
public static class AgentFileReadErrorCodes
{
    /// <summary>The resolved path does not exist.</summary>
    public const string FileNotFound = "file-not-found";
    /// <summary>The operation requires a regular file.</summary>
    public const string NotAFile = "file-not-regular";
    /// <summary>Binary content was found where text is required.</summary>
    public const string BinaryFile = "file-binary";
    /// <summary>The path is outside the authorized configured scope.</summary>
    public const string OutsideConfiguredScope = "file-outside-scope";
    /// <summary>The requested offset or limit is invalid.</summary>
    public const string InvalidRange = "file-range-invalid";
    /// <summary>The requested starting line is beyond the file.</summary>
    public const string RangeOutsideFile = "file-range-out-of-bounds";
    /// <summary>The path could not be securely canonicalized.</summary>
    public const string PathCanonicalizationFailed = "file-path-unresolvable";
    /// <summary>An I/O or access failure occurred.</summary>
    public const string ReadFailed = "file-read-failed";
    /// <summary>The read timeout elapsed.</summary>
    public const string TimedOut = "file-read-timeout";
    /// <summary>A full read exceeded its byte limit.</summary>
    public const string TooLarge = "file-too-large";
}
