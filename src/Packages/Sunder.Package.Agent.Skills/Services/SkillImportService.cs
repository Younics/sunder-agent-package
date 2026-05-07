using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Skills.Services;

public sealed partial class SkillImportService(SkillStore store, IGitHubSkillClient gitHubClient, IPackageContext packageContext)
{
    private const long MaxFileBytes = 10 * 1024 * 1024;
    private const long MaxTotalBytes = 50 * 1024 * 1024;

    public Task<InstalledSkillRecord> ImportLocalFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            throw new InvalidOperationException("Select an existing skill folder.");
        }

        return InstallFromFolderAsync(
            folderPath,
            sourceKind: "local",
            sourceUri: folderPath,
            sourceRef: null,
            resolvedCommitSha: null,
            cancellationToken);
    }

    public async Task<InstalledSkillRecord> ImportGitHubFolderAsync(string githubUrl, CancellationToken cancellationToken = default)
    {
        var parsedUrl = ParseGitHubUrl(githubUrl) ?? throw new InvalidOperationException("Enter a GitHub tree/blob/raw URL that points to a skill folder or SKILL.md.");
        var reference = await ResolveGitHubReferenceAsync(parsedUrl, cancellationToken);
        var files = await gitHubClient.ListFilesAsync(reference, cancellationToken);
        if (files.All(file => !string.Equals(file.RelativePath, "SKILL.md", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The selected GitHub folder does not contain a root SKILL.md file.");
        }

        var stagingRoot = CreateStagingRoot();
        try
        {
            var totalBytes = 0L;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = file.RelativePath;
                if (!IsSafeRelativePath(relativePath) || IsIgnoredPath(relativePath))
                {
                    continue;
                }

                if (file.Size > MaxFileBytes)
                {
                    throw new InvalidOperationException($"GitHub file is too large: {relativePath}");
                }

                var targetPath = Path.Combine(stagingRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                var bytes = await gitHubClient.ReadFileAsync(reference, file, cancellationToken);
                if (bytes.Length > MaxFileBytes)
                {
                    throw new InvalidOperationException($"GitHub file is too large: {relativePath}");
                }

                totalBytes += bytes.Length;
                if (totalBytes > MaxTotalBytes)
                {
                    throw new InvalidOperationException("GitHub skill folder is too large.");
                }

                await File.WriteAllBytesAsync(targetPath, bytes, cancellationToken);
            }

            return await InstallFromFolderAsync(
                stagingRoot,
                sourceKind: "github",
                sourceUri: githubUrl,
                sourceRef: reference.Ref,
                resolvedCommitSha: reference.CommitSha,
                cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    private async Task<InstalledSkillRecord> InstallFromFolderAsync(
        string folderPath,
        string sourceKind,
        string? sourceUri,
        string? sourceRef,
        string? resolvedCommitSha,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var skillMarkdownPath = Path.Combine(folderPath, "SKILL.md");
        if (!File.Exists(skillMarkdownPath))
        {
            throw new InvalidOperationException("Skill folder must contain a root SKILL.md file.");
        }

        var parsed = SkillMarkdownParser.Parse(await File.ReadAllTextAsync(skillMarkdownPath, cancellationToken));
        var skillId = ResolveSkillId(parsed.Name, folderPath, parsed.RawContent);
        var relativeRoot = string.Concat(SkillConstants.SkillsRelativeRoot, "/", skillId);
        var targetRoot = packageContext.Storage.Files.GetPath(relativeRoot);
        var stagingRoot = CreateStagingRoot();
        try
        {
            var warnings = CopySkillFolder(folderPath, stagingRoot);
            var contentHash = ComputeContentHash(stagingRoot);
            var now = DateTimeOffset.UtcNow;
            if (Directory.Exists(targetRoot))
            {
                Directory.Delete(targetRoot, recursive: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetRoot)!);
            Directory.Move(stagingRoot, targetRoot);

            var existing = store.GetSkill(skillId);
            var record = new InstalledSkillRecord(
                skillId,
                relativeRoot,
                parsed.Name,
                parsed.Description,
                parsed.Version,
                parsed.Author,
                sourceKind,
                sourceUri,
                sourceRef,
                resolvedCommitSha,
                contentHash,
                existing?.InstalledAtUtc ?? now,
                now,
                parsed.Metadata,
                warnings);
            store.SaveSkill(record);
            return record;
        }
        catch
        {
            TryDeleteDirectory(stagingRoot);
            throw;
        }
    }

    private static IReadOnlyList<string> CopySkillFolder(string sourceRoot, string targetRoot)
    {
        var warnings = new List<string>();
        var totalBytes = 0L;
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, directory);
            if (!IsSafeRelativePath(relativePath) || IsIgnoredPath(relativePath))
            {
                continue;
            }

            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            {
                warnings.Add($"Skipped symlinked directory: {relativePath}");
                continue;
            }

            Directory.CreateDirectory(Path.Combine(targetRoot, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, file);
            if (!IsSafeRelativePath(relativePath) || IsIgnoredPath(relativePath))
            {
                continue;
            }

            if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
            {
                warnings.Add($"Skipped symlinked file: {relativePath}");
                continue;
            }

            var length = new FileInfo(file).Length;
            if (length > MaxFileBytes)
            {
                throw new InvalidOperationException($"Skill file is too large: {relativePath}");
            }

            totalBytes += length;
            if (totalBytes > MaxTotalBytes)
            {
                throw new InvalidOperationException("Skill folder is too large.");
            }

            var targetPath = Path.Combine(targetRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, overwrite: true);
        }

        return warnings;
    }

    private static string ComputeContentHash(string rootPath)
    {
        using var sha = SHA256.Create();
        foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(rootPath, path), StringComparer.Ordinal))
        {
            var relativePath = Path.GetRelativePath(rootPath, file).Replace(Path.DirectorySeparatorChar, '/');
            var pathBytes = Encoding.UTF8.GetBytes(relativePath);
            sha.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);
            sha.TransformBlock([0], 0, 1, null, 0);
            var content = File.ReadAllBytes(file);
            sha.TransformBlock(content, 0, content.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash ?? []).ToLowerInvariant();
    }

    private string CreateStagingRoot()
    {
        var root = Path.Combine(packageContext.Storage.CacheRootPath, "skill-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ResolveSkillId(string? name, string folderPath, string rawContent)
    {
        var slug = Slugify(name) ?? Slugify(Path.GetFileName(Path.TrimEndingDirectorySeparator(folderPath)));
        if (!string.IsNullOrWhiteSpace(slug))
        {
            return slug;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawContent))).ToLowerInvariant();
        return "skill-" + hash[..12];
    }

    private static string? Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = SkillIdInvalidCharactersRegex().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
        normalized = ConsecutiveHyphenRegex().Replace(normalized, "-");
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized.Length <= 64 ? normalized : normalized[..64].Trim('-');
    }

    private static bool IsSafeRelativePath(string relativePath)
        => !string.IsNullOrWhiteSpace(relativePath)
           && !Path.IsPathRooted(relativePath)
           && relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
               .All(segment => segment is not "" and not "." and not "..");

    private static bool IsIgnoredPath(string relativePath)
    {
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => segment is ".git" or ".svn" or ".hg");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static string NormalizeGitHubPath(string path)
        => path.Trim().Trim('/');

    private async Task<GitHubSkillFolder> ResolveGitHubReferenceAsync(ParsedGitHubSkillUrl parsedUrl, CancellationToken cancellationToken)
    {
        var segments = parsedUrl.RefAndPathSegments;
        if (segments.Length == 0)
        {
            throw new InvalidOperationException("GitHub URL must include a branch, tag, or commit reference.");
        }

        foreach (var refSegmentCount in EnumerateRefSegmentCounts(segments))
        {
            var refName = string.Join('/', segments.Take(refSegmentCount));
            var folderPath = TrimSkillMarkdown(string.Join('/', segments.Skip(refSegmentCount)));
            var folder = await gitHubClient.TryGetSkillFolderAsync(
                new GitHubSkillFolderRequest(parsedUrl.Owner, parsedUrl.Repo, refName, folderPath),
                cancellationToken);
            if (folder is not null)
            {
                return folder;
            }
        }

        throw new InvalidOperationException("The selected GitHub folder does not contain a root SKILL.md file.");
    }

    private static IEnumerable<int> EnumerateRefSegmentCounts(string[] segments)
    {
        if (GitShaRegex().IsMatch(segments[0]))
        {
            yield return 1;
            yield break;
        }

        for (var refSegmentCount = segments.Length; refSegmentCount >= 1; refSegmentCount--)
        {
            yield return refSegmentCount;
        }
    }

    private static ParsedGitHubSkillUrl? ParseGitHubUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (string.Equals(uri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            && segments.Length >= 4)
        {
            if (segments.Length >= 6
                && string.Equals(segments[2], "refs", StringComparison.Ordinal)
                && segments[3] is "heads" or "tags")
            {
                return new ParsedGitHubSkillUrl(segments[0], segments[1], segments.Skip(4).ToArray());
            }

            return new ParsedGitHubSkillUrl(segments[0], segments[1], segments.Skip(2).ToArray());
        }

        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) || segments.Length < 4)
        {
            return null;
        }

        if (segments[2] is not ("tree" or "blob"))
        {
            return null;
        }

        return new ParsedGitHubSkillUrl(segments[0], segments[1], segments.Skip(3).ToArray());
    }

    private static string TrimSkillMarkdown(string path)
        => path.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase)
            ? path[..^"/SKILL.md".Length]
            : string.Equals(path, "SKILL.md", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : path;

    [GeneratedRegex("[^a-z0-9._-]+")]
    private static partial Regex SkillIdInvalidCharactersRegex();

    [GeneratedRegex("-+")]
    private static partial Regex ConsecutiveHyphenRegex();

    [GeneratedRegex("^[a-fA-F0-9]{40}$")]
    private static partial Regex GitShaRegex();

    private sealed record ParsedGitHubSkillUrl(string Owner, string Repo, string[] RefAndPathSegments);
}
