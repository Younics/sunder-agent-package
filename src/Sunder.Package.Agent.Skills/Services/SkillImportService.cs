using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Skills.Services;

public sealed partial class SkillImportService
{
    private readonly SkillStore _store;
    private readonly IGitHubSkillClient _gitHubClient;
    private readonly IPackageContext _packageContext;
    private readonly Action<SkillImportFaultPoint>? _faultInjector;
    private readonly SkillSourceAcquirer _sourceAcquirer;

    public SkillImportService(SkillStore store, IGitHubSkillClient gitHubClient, IPackageContext packageContext)
        : this(store, gitHubClient, packageContext, null)
    {
    }

    internal SkillImportService(
        SkillStore store,
        IGitHubSkillClient gitHubClient,
        IPackageContext packageContext,
        Action<SkillImportFaultPoint>? faultInjector)
    {
        _store = store;
        _gitHubClient = gitHubClient;
        _packageContext = packageContext;
        _faultInjector = faultInjector;
        _sourceAcquirer = new SkillSourceAcquirer(gitHubClient, packageContext);
    }

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

    public async Task<IReadOnlyList<InstalledSkillRecord>> ImportLocalSkillsAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            throw new InvalidOperationException("Select an existing skill folder.");
        }

        if (File.Exists(Path.Combine(folderPath, "SKILL.md")))
        {
            return [await ImportLocalFolderAsync(folderPath, cancellationToken).ConfigureAwait(false)];
        }

        var skillFolders = FindSkillFolders(folderPath, cancellationToken).ToArray();
        if (skillFolders.Length == 0)
        {
            throw new InvalidOperationException("Selected folder does not contain any skill folders with SKILL.md files.");
        }

        return await ImportLocalFolderSetAsync(skillFolders, includeSourceUri: true, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<InstalledSkillRecord>> ImportTransferredSkillsAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(Path.Combine(folderPath, "SKILL.md")))
        {
            return [await InstallFromFolderAsync(
                folderPath,
                sourceKind: "local",
                sourceUri: null,
                sourceRef: null,
                resolvedCommitSha: null,
                cancellationToken).ConfigureAwait(false)];
        }

        var skillFolders = FindSkillFolders(folderPath, cancellationToken).ToArray();
        if (skillFolders.Length == 0)
        {
            throw new InvalidOperationException("Transferred folder does not contain any skill folders with SKILL.md files.");
        }

        return await ImportLocalFolderSetAsync(skillFolders, includeSourceUri: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InstalledSkillRecord>> ImportCommonSkillFoldersAsync(CancellationToken cancellationToken = default)
    {
        var imported = new List<InstalledSkillRecord>();
        foreach (var folder in EnumerateCommonSkillRoots().Where(Directory.Exists).Distinct(StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(folder, "SKILL.md")) && !FindSkillFolders(folder, cancellationToken).Any())
            {
                continue;
            }

            imported.AddRange(await ImportLocalSkillsAsync(folder, cancellationToken).ConfigureAwait(false));
        }

        return imported;
    }

    public async Task<IReadOnlyList<InstalledSkillRecord>> ImportGitHubAsync(string githubUrl, CancellationToken cancellationToken = default)
    {
        var parsedUrl = ParseGitHubUrl(githubUrl) ?? throw new InvalidOperationException("Enter a GitHub repository, tree, blob, or raw URL that points to a skill folder, SKILL.md, or a folder containing skills.");
        try
        {
            return [await ImportGitHubFolderAsync(githubUrl, cancellationToken).ConfigureAwait(false)];
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("root SKILL.md", StringComparison.OrdinalIgnoreCase)
                                                 || ex.Message.Contains("must include a branch", StringComparison.OrdinalIgnoreCase))
        {
        }

        var parent = await ResolveGitHubFolderReferenceAsync(parsedUrl, cancellationToken).ConfigureAwait(false);
        var files = await _gitHubClient.ListFilesAsync(parent, cancellationToken).ConfigureAwait(false);
        var skillRoots = files
            .Select(file => NormalizeGitHubPath(file.RelativePath))
            .Where(path => path.EndsWith("SKILL.md", StringComparison.Ordinal))
            .Select(TrimSkillMarkdown)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (skillRoots.Length == 0)
        {
            throw new InvalidOperationException("The selected GitHub folder does not contain any skill folders with SKILL.md files.");
        }

        var stagedSources = new List<(GitHubSkillFolder Folder, StagedSkillSource Source)>();
        try
        {
            foreach (var skillRoot in skillRoots)
            {
                var skillFolderPath = CombineGitHubPath(parent.FolderPath, skillRoot);
                var folder = await _gitHubClient.TryGetSkillFolderAsync(
                    new GitHubSkillFolderRequest(parent.Owner, parent.Repo, parent.CommitSha, skillFolderPath),
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"GitHub skill folder '{skillFolderPath}' changed while resolving commit '{parent.CommitSha}'.");
                if (!string.Equals(folder.CommitSha, parent.CommitSha, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("GitHub returned inconsistent commit identities while importing skills.");
                }

                stagedSources.Add((folder, await _sourceAcquirer.AcquireGitHubAsync(folder, cancellationToken).ConfigureAwait(false)));
            }

            var replacements = new List<PreparedSkillReplacement>(stagedSources.Count);
            foreach (var (folder, staged) in stagedSources)
            {
                var skillUrl = $"https://github.com/{folder.Owner}/{folder.Repo}/tree/{parent.Ref}/{folder.FolderPath}";
                replacements.Add(await PrepareReplacementAsync(
                    staged,
                    folder.FolderPath,
                    "github",
                    skillUrl,
                    parent.Ref,
                    parent.CommitSha,
                    cancellationToken).ConfigureAwait(false));
            }

            return new SkillBatchReplacementCommitter(_store, _faultInjector).Commit(replacements);
        }
        finally
        {
            foreach (var (_, staged) in stagedSources)
            {
                staged.Dispose();
            }
        }
    }

    public async Task<InstalledSkillRecord> ImportGitHubFolderAsync(string githubUrl, CancellationToken cancellationToken = default)
    {
        var parsedUrl = ParseGitHubUrl(githubUrl) ?? throw new InvalidOperationException("Enter a GitHub tree/blob/raw URL that points to a skill folder or SKILL.md.");
        var reference = await ResolveGitHubReferenceAsync(parsedUrl, cancellationToken);
        using var staged = await _sourceAcquirer.AcquireGitHubAsync(reference, cancellationToken).ConfigureAwait(false);
        return await InstallStagedAsync(
                staged,
                reference.FolderPath,
                sourceKind: "github",
                sourceUri: githubUrl,
                sourceRef: reference.Ref,
                resolvedCommitSha: reference.CommitSha,
                cancellationToken).ConfigureAwait(false);
    }

    public Task<InstalledSkillRecord> ImportStackFolderAsync(string folderPath, CancellationToken cancellationToken = default)
        => InstallFromFolderAsync(
            folderPath,
            sourceKind: "stack",
            sourceUri: null,
            sourceRef: null,
            resolvedCommitSha: null,
            cancellationToken);

    private async Task<InstalledSkillRecord> InstallFromFolderAsync(
        string folderPath,
        string sourceKind,
        string? sourceUri,
        string? sourceRef,
        string? resolvedCommitSha,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var staged = await _sourceAcquirer.AcquireLocalAsync(folderPath, cancellationToken).ConfigureAwait(false);
        return await InstallStagedAsync(
            staged,
            folderPath,
            sourceKind,
            sourceUri,
            sourceRef,
            resolvedCommitSha,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<InstalledSkillRecord>> ImportLocalFolderSetAsync(
        IReadOnlyList<string> folderPaths,
        bool includeSourceUri,
        CancellationToken cancellationToken)
    {
        var stagedSources = new List<(string FolderPath, StagedSkillSource Source)>();
        try
        {
            var skillIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folderPath in folderPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var staged = await _sourceAcquirer.AcquireLocalAsync(folderPath, cancellationToken).ConfigureAwait(false);
                stagedSources.Add((folderPath, staged));
                var manifest = await SkillManifestReader.ReadAsync(staged.RootPath, cancellationToken).ConfigureAwait(false);
                var skillId = ResolveSkillId(manifest.Name, folderPath, manifest.RawContent);
                if (!skillIds.Add(skillId))
                {
                    throw new InvalidDataException($"Skill import contains duplicate or case-colliding id '{skillId}'.");
                }
            }

            var replacements = new List<PreparedSkillReplacement>(stagedSources.Count);
            foreach (var (folderPath, staged) in stagedSources)
            {
                replacements.Add(await PrepareReplacementAsync(
                    staged,
                    folderPath,
                    sourceKind: "local",
                    sourceUri: includeSourceUri ? folderPath : null,
                    sourceRef: null,
                    resolvedCommitSha: null,
                    cancellationToken).ConfigureAwait(false));
            }

            return new SkillBatchReplacementCommitter(_store, _faultInjector).Commit(replacements);
        }
        finally
        {
            foreach (var (_, staged) in stagedSources)
            {
                staged.Dispose();
            }
        }
    }

    private async Task<InstalledSkillRecord> InstallStagedAsync(
        StagedSkillSource staged,
        string identityPath,
        string sourceKind,
        string? sourceUri,
        string? sourceRef,
        string? resolvedCommitSha,
        CancellationToken cancellationToken)
    {
        var replacement = await PrepareReplacementAsync(
            staged,
            identityPath,
            sourceKind,
            sourceUri,
            sourceRef,
            resolvedCommitSha,
            cancellationToken).ConfigureAwait(false);
        return new SkillBatchReplacementCommitter(_store, _faultInjector).Commit([replacement]).Single();
    }

    private async Task<PreparedSkillReplacement> PrepareReplacementAsync(
        StagedSkillSource staged,
        string identityPath,
        string sourceKind,
        string? sourceUri,
        string? sourceRef,
        string? resolvedCommitSha,
        CancellationToken cancellationToken)
    {
        var parsed = await SkillManifestReader.ReadAsync(staged.RootPath, cancellationToken).ConfigureAwait(false);
        var skillId = ResolveSkillId(parsed.Name, identityPath, parsed.RawContent);
        var relativeRoot = string.Concat(SkillConstants.SkillsRelativeRoot, "/", skillId);
        var targetRoot = _packageContext.Storage.RoleLocalWorkspace.GetLocalPath(relativeRoot);
        var contentHash = await SkillContentHasher.ComputeAsync(staged.RootPath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var existing = _store.GetSkill(skillId);
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
            staged.Warnings);
        cancellationToken.ThrowIfCancellationRequested();
        return new PreparedSkillReplacement(staged.RootPath, targetRoot, record);
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

    private static string NormalizeGitHubPath(string path)
        => path.Trim().Trim('/');

    private static string CombineGitHubPath(string left, string right)
    {
        left = NormalizeGitHubPath(left);
        right = NormalizeGitHubPath(right);
        return string.IsNullOrWhiteSpace(left) ? right : string.IsNullOrWhiteSpace(right) ? left : left + "/" + right;
    }

    private static IEnumerable<string> FindSkillFolders(string rootPath, CancellationToken cancellationToken)
        => Sunder.Package.Agent.Shared.Importing.BoundedImportIO.ValidateTree(rootPath, SkillSourceAcquirer.Limits, cancellationToken)
            .Where(file => string.Equals(Path.GetFileName(file.RelativePath), "SKILL.md", StringComparison.Ordinal)
                           && !file.RelativePath.Split('/').Any(segment => segment is ".git" or ".svn" or ".hg"))
            .Select(file => Path.GetDirectoryName(file.SourcePath)!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal);

    private static IEnumerable<string> EnumerateCommonSkillRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            yield break;
        }

        yield return Path.Combine(home, ".agents", "skills");
        yield return Path.Combine(home, ".config", "opencode", "skills");
        yield return Path.Combine(home, ".claude", "skills");
        yield return Path.Combine(home, "Library", "Application Support", "Claude", "skills");
    }

    private async Task<GitHubSkillFolder> ResolveGitHubReferenceAsync(ParsedGitHubSkillUrl parsedUrl, CancellationToken cancellationToken)
    {
        var segments = parsedUrl.RefAndPathSegments;
        if (segments.Length == 0)
        {
            var defaultBranch = await _gitHubClient.TryGetDefaultBranchAsync(parsedUrl.Owner, parsedUrl.Repo, cancellationToken).ConfigureAwait(false)
                                ?? throw new InvalidOperationException("Could not determine the GitHub repository default branch.");
            var rootFolder = await _gitHubClient.TryGetSkillFolderAsync(
                new GitHubSkillFolderRequest(parsedUrl.Owner, parsedUrl.Repo, defaultBranch, string.Empty),
                cancellationToken).ConfigureAwait(false);
            return rootFolder ?? throw new InvalidOperationException("The selected GitHub folder does not contain a root SKILL.md file.");
        }

        foreach (var refSegmentCount in EnumerateRefSegmentCounts(segments))
        {
            var refName = string.Join('/', segments.Take(refSegmentCount));
            var folderPath = TrimSkillMarkdown(string.Join('/', segments.Skip(refSegmentCount)));
            var folder = await _gitHubClient.TryGetSkillFolderAsync(
                new GitHubSkillFolderRequest(parsedUrl.Owner, parsedUrl.Repo, refName, folderPath),
                cancellationToken);
            if (folder is not null)
            {
                return folder;
            }
        }

        throw new InvalidOperationException("The selected GitHub folder does not contain a root SKILL.md file.");
    }

    private async Task<GitHubSkillFolder> ResolveGitHubFolderReferenceAsync(ParsedGitHubSkillUrl parsedUrl, CancellationToken cancellationToken)
    {
        var segments = parsedUrl.RefAndPathSegments;
        if (segments.Length == 0)
        {
            var defaultBranch = await _gitHubClient.TryGetDefaultBranchAsync(parsedUrl.Owner, parsedUrl.Repo, cancellationToken).ConfigureAwait(false)
                                ?? throw new InvalidOperationException("Could not determine the GitHub repository default branch.");
            return await _gitHubClient.TryGetFolderAsync(
                       new GitHubSkillFolderRequest(parsedUrl.Owner, parsedUrl.Repo, defaultBranch, string.Empty),
                       cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("The selected GitHub repository could not be read.");
        }

        foreach (var refSegmentCount in EnumerateRefSegmentCounts(segments))
        {
            var refName = string.Join('/', segments.Take(refSegmentCount));
            var folderPath = TrimSkillMarkdown(string.Join('/', segments.Skip(refSegmentCount)));
            var folder = await _gitHubClient.TryGetFolderAsync(
                new GitHubSkillFolderRequest(parsedUrl.Owner, parsedUrl.Repo, refName, folderPath),
                cancellationToken).ConfigureAwait(false);
            if (folder is not null)
            {
                return folder;
            }
        }

        throw new InvalidOperationException("The selected GitHub folder could not be read.");
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

        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) || segments.Length < 2)
        {
            return null;
        }

        if (segments.Length == 2)
        {
            return new ParsedGitHubSkillUrl(segments[0], segments[1], []);
        }

        if (segments.Length < 4)
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

internal enum SkillImportFaultPoint
{
    AfterBackup,
    AfterStagedMove,
    AfterIndexCommitted,
}
