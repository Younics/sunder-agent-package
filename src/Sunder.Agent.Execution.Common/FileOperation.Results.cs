using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Agent.Execution.Common;

public static partial class FileOperation
{
    public static AgentFileMutationResult Written(string path, int characterCount)
        => Mutation(path, $"Wrote {characterCount} character(s).");

    public static AgentFileMutationResult FileDeleted(string path)
        => Mutation(path, "File deleted.");

    public static AgentFileMutationResult DirectoryDeleted(string path)
        => Mutation(path, "Directory deleted.");

    public static AgentFileMutationResult Failure(string path, string summary, string errorCode)
        => Mutation(path, summary, true, errorCode);

    public static AgentFileMutationResult ContentChanged(string path)
        => Failure(path, "The file changed after patch preflight; no mutation was applied.", ContentChangedErrorCode);

    private static AgentFileMutationResult Mutation(string path, string summary, bool error = false, string? code = null)
#if SUNDER_AGENT_PACKAGE_MODELS
        => new() { Path = path, Summary = summary, IsError = error, ErrorCode = code, PostMutationResource = null };
#else
        => new(path, summary, error, code);
#endif

    /// <summary>Creates a complete bounded read result for the native and generated-model execution surfaces.</summary>
    public static AgentFileReadResult ReadResult(string path, string content, bool isDirectory = false, bool wasTruncated = false,
        int? startLine = null, int? endLine = null, int? totalLines = null)
#if SUNDER_AGENT_PACKAGE_MODELS
        => new() { Path = path, Content = content, IsDirectory = isDirectory, WasTruncated = wasTruncated,
            StartLine = startLine, EndLine = endLine, TotalLines = totalLines, IsError = false, ErrorCode = null, ErrorMessage = null };
#else
        => new(path, content, isDirectory, wasTruncated) { StartLine = startLine, EndLine = endLine, TotalLines = totalLines };
#endif

    /// <summary>Creates an unsuccessful read without treating the empty content as a successful result.</summary>
    public static AgentFileReadResult ReadFailure(string path, string errorCode, string errorMessage)
        => ReadResult(path, string.Empty) with { IsError = true, ErrorCode = errorCode, ErrorMessage = errorMessage };

    /// <summary>Creates a structured search result with its truncation state.</summary>
    public static AgentFileSearchResult SearchResult(IReadOnlyList<AgentFileSearchMatch> matches, bool wasTruncated = false, string? truncationReason = null)
#if SUNDER_AGENT_PACKAGE_MODELS
        => new() { Matches = matches, WasTruncated = wasTruncated, IsError = false, ErrorCode = null, ErrorMessage = null, TruncationReason = truncationReason };
#else
        => new(matches, wasTruncated) { TruncationReason = truncationReason };
#endif

    /// <summary>Creates a failed search with no consumable matches.</summary>
    public static AgentFileSearchResult SearchFailure(string errorCode, string errorMessage)
        => SearchResult([]) with { IsError = true, ErrorCode = errorCode, ErrorMessage = errorMessage };
}
