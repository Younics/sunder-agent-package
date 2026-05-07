using System.Net;
using Octokit;

namespace Sunder.Package.Agent.Skills.Services;

public interface IGitHubSkillClient
{
    Task<GitHubSkillFolder?> TryGetSkillFolderAsync(GitHubSkillFolderRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubSkillFile>> ListFilesAsync(GitHubSkillFolder folder, CancellationToken cancellationToken = default);

    Task<byte[]> ReadFileAsync(GitHubSkillFolder folder, GitHubSkillFile file, CancellationToken cancellationToken = default);
}

public sealed record GitHubSkillFolderRequest(string Owner, string Repo, string Ref, string FolderPath);

public sealed record GitHubSkillFolder(string Owner, string Repo, string Ref, string FolderPath, string CommitSha, string? TreeSha);

public sealed record GitHubSkillFile(string RelativePath, string RepositoryPath, long Size);

public sealed class OctokitGitHubSkillClient(GitHubClient client) : IGitHubSkillClient
{
    public async Task<GitHubSkillFolder?> TryGetSkillFolderAsync(GitHubSkillFolderRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var folderPath = NormalizeGitHubPath(request.FolderPath);
        var skillMarkdownPath = CombineGitHubPath(folderPath, "SKILL.md");
        try
        {
            var skillContent = await client.Repository.Content.GetAllContentsByRef(request.Owner, request.Repo, skillMarkdownPath, request.Ref);
            if (skillContent.Count == 0 || !skillContent.Any(content => string.Equals(content.Path, skillMarkdownPath, StringComparison.Ordinal)))
            {
                return null;
            }

            var commit = await client.Repository.Commit.Get(request.Owner, request.Repo, request.Ref);
            var treeSha = await ResolveFolderTreeShaAsync(request.Owner, request.Repo, folderPath, request.Ref, cancellationToken);
            if (!string.IsNullOrWhiteSpace(folderPath) && string.IsNullOrWhiteSpace(treeSha))
            {
                return null;
            }

            return new GitHubSkillFolder(request.Owner, request.Repo, request.Ref, folderPath, commit.Sha, treeSha);
        }
        catch (ApiException ex) when (IsMissingOrInvalidRef(ex))
        {
            return null;
        }
        catch (ApiException ex) when (IsRateLimit(ex))
        {
            throw new InvalidOperationException("GitHub rate limit reached; try again later.", ex);
        }
    }

    public async Task<IReadOnlyList<GitHubSkillFile>> ListFilesAsync(GitHubSkillFolder folder, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var tree = await client.Git.Tree.GetRecursive(folder.Owner, folder.Repo, folder.TreeSha ?? folder.CommitSha);
            if (tree.Truncated)
            {
                throw new InvalidOperationException("GitHub skill folder is too large to import.");
            }

            return tree.Tree
                .Where(item => string.Equals(item.Type.ToString(), "Blob", StringComparison.OrdinalIgnoreCase)
                               && !string.Equals(item.Mode, Octokit.FileMode.Symlink, StringComparison.Ordinal)
                               && !string.Equals(item.Mode, Octokit.FileMode.Submodule, StringComparison.Ordinal))
                .Select(item =>
                {
                    var relativePath = NormalizeGitHubPath(item.Path);
                    return new GitHubSkillFile(relativePath, CombineGitHubPath(folder.FolderPath, relativePath), item.Size);
                })
                .ToArray();
        }
        catch (ApiException ex) when (IsRateLimit(ex))
        {
            throw new InvalidOperationException("GitHub rate limit reached; try again later.", ex);
        }
    }

    public async Task<byte[]> ReadFileAsync(GitHubSkillFolder folder, GitHubSkillFile file, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await client.Repository.Content.GetRawContentByRef(folder.Owner, folder.Repo, file.RepositoryPath, folder.Ref);
        }
        catch (ApiException ex) when (IsRateLimit(ex))
        {
            throw new InvalidOperationException("GitHub rate limit reached; try again later.", ex);
        }
    }

    private async Task<string?> ResolveFolderTreeShaAsync(string owner, string repo, string folderPath, string reference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var lastSlashIndex = folderPath.LastIndexOf('/');
        var parentPath = lastSlashIndex < 0 ? string.Empty : folderPath[..lastSlashIndex];
        var folderName = lastSlashIndex < 0 ? folderPath : folderPath[(lastSlashIndex + 1)..];
        var contents = string.IsNullOrWhiteSpace(parentPath)
            ? await client.Repository.Content.GetAllContentsByRef(owner, repo, reference)
            : await client.Repository.Content.GetAllContentsByRef(owner, repo, parentPath, reference);
        var entry = contents.FirstOrDefault(content =>
            string.Equals(content.Name, folderName, StringComparison.Ordinal)
            && string.Equals(NormalizeGitHubPath(content.Path), folderPath, StringComparison.Ordinal));
        return entry?.Sha;
    }

    private static string NormalizeGitHubPath(string path)
        => path.Trim().Trim('/');

    private static string CombineGitHubPath(string left, string right)
    {
        left = NormalizeGitHubPath(left);
        right = NormalizeGitHubPath(right);
        return string.IsNullOrWhiteSpace(left) ? right : string.IsNullOrWhiteSpace(right) ? left : left + "/" + right;
    }

    private static bool IsMissingOrInvalidRef(ApiException ex)
        => ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity;

    private static bool IsRateLimit(ApiException ex)
        => ex.StatusCode == HttpStatusCode.Forbidden
           && ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
}
