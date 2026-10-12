namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Defines stable execution-target search failure codes.</summary>
public static class AgentFileSearchErrorCodes
{
    /// <summary>The request or expression was invalid.</summary>
    public const string InvalidRequest = "file-search-invalid";
    /// <summary>The search root was missing.</summary>
    public const string PathNotFound = "file-search-path-not-found";
    /// <summary>The root was outside authorized scope.</summary>
    public const string OutsideConfiguredScope = "file-search-outside-scope";
    /// <summary>A no-follow or filesystem identity check failed.</summary>
    public const string PathUnresolvable = "file-search-path-unresolvable";
    /// <summary>The backend failed after authorization.</summary>
    public const string SearchFailed = "file-search-failed";
}
