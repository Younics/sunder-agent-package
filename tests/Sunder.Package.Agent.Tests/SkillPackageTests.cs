using System.Text;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Skills.PackageViews;
using Sunder.Package.Agent.Skills.Services;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class SkillPackageTests
{
    private const string SkillsPackageId = "sunder.package.agent.skills";

    [Fact]
    public async Task ImportLocalFolderAsync_CopiesResourcesAndPreservesUnknownMetadata()
    {
        var root = CreateTempRoot();
        var source = Path.Combine(root, "source", "custom-skill");
        Directory.CreateDirectory(Path.Combine(source, "references"));
        await File.WriteAllTextAsync(Path.Combine(source, "SKILL.md"), """
            ---
            name: null
            description: null
            version: "1.2.3"
            author: somebody
            allowed-tools: Bash(agent-browser:*) Bash(npx agent-browser:*)
            references:
              - workers
            ---
            # Custom Skill
            Use this skill carefully.
            """);
        await File.WriteAllTextAsync(Path.Combine(source, "references", "guide.md"), "hello");

        var context = new TestPackageContext(root);
        var store = new SkillStore(context);
        var importer = new SkillImportService(store, new TestGitHubSkillClient(), context);

        var record = await importer.ImportLocalFolderAsync(source);

        Assert.Equal("custom-skill", record.SkillId);
        Assert.Null(record.Name);
        Assert.Null(record.Description);
        Assert.Equal("1.2.3", record.Version);
        Assert.Equal("somebody", record.Author);
        Assert.Contains("allowed-tools", record.Metadata.Keys);
        Assert.Contains("agent-browser", record.Metadata["allowed-tools"]);
        Assert.True(File.Exists(Path.Combine(store.GetSkillRootPath(record), "references", "guide.md")));
    }

    [Fact]
    public async Task ImportGitHubFolderAsync_ImportsCommitTreeFolderUrl()
    {
        var root = CreateTempRoot();
        var context = new TestPackageContext(root);
        var store = new SkillStore(context);
        var gitHub = new TestGitHubSkillClient();
        gitHub.AddFolder(
            "vercel-labs",
            "agent-browser",
            "57405f93614fae46e5c955ce662b4785283e1301",
            "skills/agent-browser",
            "57405f93614fae46e5c955ce662b4785283e1301",
            AgentBrowserSkillFiles());
        var importer = new SkillImportService(store, gitHub, context);

        var record = await importer.ImportGitHubFolderAsync("https://github.com/vercel-labs/agent-browser/tree/57405f93614fae46e5c955ce662b4785283e1301/skills/agent-browser");

        Assert.Equal("agent-browser", record.SkillId);
        Assert.Equal("57405f93614fae46e5c955ce662b4785283e1301", record.SourceRef);
        Assert.Equal("57405f93614fae46e5c955ce662b4785283e1301", record.ResolvedCommitSha);
        Assert.True(File.Exists(Path.Combine(store.GetSkillRootPath(record), "references", "guide.md")));
    }

    [Fact]
    public async Task ImportGitHubFolderAsync_ImportsRawRefsHeadsSkillMarkdownUrl()
    {
        var root = CreateTempRoot();
        var context = new TestPackageContext(root);
        var store = new SkillStore(context);
        var gitHub = new TestGitHubSkillClient();
        gitHub.AddFolder(
            "vercel-labs",
            "agent-browser",
            "main",
            "skills/agent-browser",
            "abc123",
            AgentBrowserSkillFiles());
        var importer = new SkillImportService(store, gitHub, context);

        var record = await importer.ImportGitHubFolderAsync("https://raw.githubusercontent.com/vercel-labs/agent-browser/refs/heads/main/skills/agent-browser/SKILL.md");

        Assert.Equal("agent-browser", record.SkillId);
        Assert.Equal("main", record.SourceRef);
        Assert.Equal("abc123", record.ResolvedCommitSha);
        Assert.True(File.Exists(Path.Combine(store.GetSkillRootPath(record), "references", "guide.md")));
    }

    [Fact]
    public async Task ImportGitHubFolderAsync_ImportsBlobSkillMarkdownUrl()
    {
        var root = CreateTempRoot();
        var context = new TestPackageContext(root);
        var store = new SkillStore(context);
        var gitHub = new TestGitHubSkillClient();
        gitHub.AddFolder("acme", "repo", "main", "skills/docs", "abc123", DocsSkillFiles());
        var importer = new SkillImportService(store, gitHub, context);

        var record = await importer.ImportGitHubFolderAsync("https://github.com/acme/repo/blob/main/skills/docs/SKILL.md");

        Assert.Equal("docs-skill", record.SkillId);
        Assert.Equal("main", record.SourceRef);
        Assert.True(File.Exists(Path.Combine(store.GetSkillRootPath(record), "references", "guide.md")));
    }

    [Fact]
    public async Task ImportGitHubFolderAsync_ResolvesSlashSeparatedBranchBeforeFolderPath()
    {
        var root = CreateTempRoot();
        var context = new TestPackageContext(root);
        var store = new SkillStore(context);
        var gitHub = new TestGitHubSkillClient();
        gitHub.AddFolder("acme", "repo", "feature/branch", "skills/docs", "abc123", DocsSkillFiles());
        var importer = new SkillImportService(store, gitHub, context);

        var record = await importer.ImportGitHubFolderAsync("https://github.com/acme/repo/tree/feature/branch/skills/docs");

        Assert.Equal("docs-skill", record.SkillId);
        Assert.Equal("feature/branch", record.SourceRef);
        Assert.Equal("abc123", record.ResolvedCommitSha);
        Assert.True(File.Exists(Path.Combine(store.GetSkillRootPath(record), "references", "guide.md")));
    }

    [Fact]
    public void SkillSettingsViewModel_CompactLayout_UsesRowActivationAndReturnsToList()
    {
        var root = CreateTempRoot();
        try
        {
            var context = new TestPackageContext(root);
            var store = new SkillStore(context);
            var importer = new SkillImportService(store, new TestGitHubSkillClient(), context);
            InstallSkill(store, "docs-skill", "Docs Skill");
            using var viewModel = new SkillSettingsViewModel(store, importer)
            {
                IsCompactLayout = true,
            };

            Assert.Null(viewModel.SelectedSkill);
            Assert.False(viewModel.IsDetailActive);
            Assert.True(viewModel.ShowCompactList);

            viewModel.ActivateSkill(viewModel.Skills.Single(skill => skill.SkillId == "docs-skill"));

            Assert.True(viewModel.IsDetailActive);
            Assert.Equal("docs-skill", viewModel.SelectedSkill?.SkillId);
            Assert.True(viewModel.ShowCompactDetail);

            viewModel.BackToSkillListCommand.Execute(null);

            Assert.False(viewModel.IsDetailActive);
            Assert.Null(viewModel.SelectedSkill);
            Assert.True(viewModel.ShowCompactList);

            viewModel.IsCompactLayout = false;

            Assert.Equal("docs-skill", viewModel.SelectedSkill?.SkillId);
            Assert.True(viewModel.ShowListPane);
            Assert.True(viewModel.ShowDetailPane);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void SkillSettingsViewModel_Delete_CompactLayout_ReturnsToListAndClearSelection()
    {
        var root = CreateTempRoot();
        try
        {
            var context = new TestPackageContext(root);
            var store = new SkillStore(context);
            var importer = new SkillImportService(store, new TestGitHubSkillClient(), context);
            InstallSkill(store, "docs-skill", "Docs Skill");
            using var viewModel = new SkillSettingsViewModel(store, importer)
            {
                IsCompactLayout = true,
            };
            viewModel.ActivateSkill(viewModel.Skills.Single(skill => skill.SkillId == "docs-skill"));

            viewModel.DeleteSelectedSkillCommand.Execute(null);

            Assert.False(viewModel.IsDetailActive);
            Assert.Null(viewModel.SelectedSkill);
            Assert.Empty(viewModel.StatusText);
            Assert.Empty(viewModel.Skills);
            Assert.Null(store.GetSkill("docs-skill"));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SkillSettingsViewModel_ImportLocalFolder_WideLayout_KeepsDetailAndAutoClearsSuccess()
    {
        var root = CreateTempRoot();
        try
        {
            var source = CreateSkillSource(root, "docs-skill", "Docs Skill");
            var context = new TestPackageContext(Path.Combine(root, "install"));
            var store = new SkillStore(context);
            var importer = new SkillImportService(store, new TestGitHubSkillClient(), context);
            using var viewModel = new SkillSettingsViewModel(store, importer);

            await viewModel.ImportLocalFolderAsync(source);

            Assert.True(viewModel.IsDetailActive);
            Assert.Equal("docs-skill", viewModel.SelectedSkill?.SkillId);
            Assert.Equal("Imported local skill folder.", viewModel.StatusText);
            Assert.True(viewModel.IsStatusSuccess);
            Assert.False(viewModel.IsStatusWarning);
            Assert.False(viewModel.IsStatusError);

            await WaitUntilAsync(() => string.IsNullOrWhiteSpace(viewModel.StatusText), TimeSpan.FromSeconds(4));

            Assert.Empty(viewModel.StatusText);
            Assert.Equal(SkillStatusKind.None, viewModel.StatusKind);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SkillSettingsViewModel_ImportLocalFolder_CompactLayout_ReturnsToListAndClearSelection()
    {
        var root = CreateTempRoot();
        try
        {
            var source = CreateSkillSource(root, "docs-skill", "Docs Skill");
            var context = new TestPackageContext(Path.Combine(root, "install"));
            var store = new SkillStore(context);
            var importer = new SkillImportService(store, new TestGitHubSkillClient(), context);
            using var viewModel = new SkillSettingsViewModel(store, importer)
            {
                IsCompactLayout = true,
            };
            viewModel.NewSkillCommand.Execute(null);

            await viewModel.ImportLocalFolderAsync(source);

            Assert.False(viewModel.IsDetailActive);
            Assert.Null(viewModel.SelectedSkill);
            Assert.Empty(viewModel.StatusText);
            Assert.Contains(viewModel.Skills, skill => skill.SkillId == "docs-skill");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SkillResourceTool_ReadsOnlyEnabledSkillResources()
    {
        var root = CreateTempRoot();
        var source = Path.Combine(root, "source", "docs-skill");
        Directory.CreateDirectory(Path.Combine(source, "references"));
        await File.WriteAllTextAsync(Path.Combine(source, "SKILL.md"), """
            ---
            name: docs-skill
            description: Read docs skill resources.
            ---
            # Docs Skill
            Read references when needed.
            """);
        await File.WriteAllTextAsync(Path.Combine(source, "references", "guide.md"), "first\nsecond");

        var context = new TestPackageContext(root);
        var store = new SkillStore(context);
        var importer = new SkillImportService(store, new TestGitHubSkillClient(), context);
        await importer.ImportLocalFolderAsync(source);

        var profile = new AgentProfileRecord(
            "profile-1",
            "Profile",
            null,
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [],
            [new AgentProfileSelectableCapabilityAssignmentRecord("skill", "docs-skill", SkillsPackageId)]);
        var feature = new SkillsFeature(store, new TestExtensionCatalog(profile));

        var result = await feature.ExecuteAsync(
            new AgentToolExecutionContext(SessionId: null, ProfileId: profile.ProfileId),
            new AgentToolRequest("skill_resource", "{\"skill\":\"docs-skill\",\"path\":\"references/guide.md\"}"));

        Assert.False(result.IsError);
        Assert.Contains("1: first", result.Content);
        Assert.Contains("2: second", result.Content);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-skill-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateSkillSource(string root, string skillId, string displayName)
    {
        var source = Path.Combine(root, "source", skillId);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "SKILL.md"), $$"""
            ---
            name: {{skillId}}
            description: {{displayName}} resources.
            ---
            # {{displayName}}
            """);
        return source;
    }

    private static InstalledSkillRecord InstallSkill(SkillStore store, string skillId, string displayName)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new InstalledSkillRecord(
            skillId,
            $"skills/{skillId}",
            displayName,
            displayName + " resources.",
            "1.0.0",
            "Sunder",
            "local",
            null,
            null,
            null,
            "test-content",
            now,
            now,
            new Dictionary<string, string>(),
            []);
        Directory.CreateDirectory(store.GetSkillRootPath(record));
        File.WriteAllText(Path.Combine(store.GetSkillRootPath(record), "SKILL.md"), $"# {displayName}");
        store.SaveSkill(record);
        return record;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2));
        while (!condition())
        {
            timeoutCts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeoutCts.Token);
        }
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
            // Test cleanup should not hide assertion failures.
        }
    }

    private static IReadOnlyDictionary<string, string> DocsSkillFiles()
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SKILL.md"] = """
                ---
                name: docs-skill
                description: Docs from GitHub.
                ---
                # Docs Skill
                """,
            ["references/guide.md"] = "guide",
        };

    private static IReadOnlyDictionary<string, string> AgentBrowserSkillFiles()
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SKILL.md"] = """
                ---
                name: agent-browser
                description: Browser automation CLI for AI agents.
                allowed-tools: Bash(agent-browser:*) Bash(npx agent-browser:*)
                ---
                # agent-browser
                """,
            ["references/guide.md"] = "guide",
        };

    private sealed class TestExtensionCatalog(AgentProfileRecord profile) : IPackageExtensionCatalog
    {
        public IReadOnlyList<TContract> GetExtensions<TContract>(PackageExtensionPoint<TContract> extensionPoint)
        {
            if (extensionPoint.Id == PackageExtensionPoints.RuntimeCatalogs.Id
                && typeof(TContract) == typeof(IAgentRuntimeCatalog))
            {
                return [(TContract)(object)new TestRuntimeCatalog(profile)];
            }

            return [];
        }
    }

    private sealed class TestRuntimeCatalog(AgentProfileRecord profile) : IAgentRuntimeCatalog
    {
        public event Action<string>? ProfileChanged;

        public event Action<Guid>? SessionChanged
        {
            add { }
            remove { }
        }

        public event Action<Guid, AgentTurnRecord>? TurnChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<AgentSessionRecord> ListSessions() => [];

        public IReadOnlyList<AgentSessionRecord> ListSessionsForProfile(string profileId) => [];

        public AgentSessionRecord? GetSession(Guid sessionId) => null;

        public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => [];

        public AgentWorkspaceRecord? GetWorkspace(string workspaceId) => null;

        public AgentProfileRecord? GetSessionProfile(Guid sessionId) => null;

        public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId) => null;

        public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => null;

        public IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit) => [];

        public IReadOnlyList<AgentTurnRecord> ListTurnsBefore(Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit) => [];

        public IReadOnlyList<AgentProfileRecord> ListProfiles() => [profile];

        public AgentProfileRecord? GetProfile(string profileId) => profile.ProfileId == profileId ? profile : null;

        public AgentProfileModelBindingRecord? GetSessionModelBinding(Guid sessionId, string capabilityKind) => null;

        public AgentProfileModelBindingRecord? GetModelBinding(string profileId, string capabilityKind) => null;

        public void RaiseProfileChanged() => ProfileChanged?.Invoke(profile.ProfileId);
    }

    private sealed class TestGitHubSkillClient : IGitHubSkillClient
    {
        private readonly Dictionary<string, TestGitHubFolder> _folders = new(StringComparer.Ordinal);

        public void AddFolder(
            string owner,
            string repo,
            string reference,
            string folderPath,
            string commitSha,
            IReadOnlyDictionary<string, string> files)
            => _folders[Key(owner, repo, reference, folderPath)] = new TestGitHubFolder(
                new GitHubSkillFolder(owner, repo, reference, folderPath, commitSha, "tree-" + commitSha),
                files.ToDictionary(pair => pair.Key, pair => Encoding.UTF8.GetBytes(pair.Value), StringComparer.Ordinal));

        public Task<GitHubSkillFolder?> TryGetSkillFolderAsync(GitHubSkillFolderRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(_folders.GetValueOrDefault(Key(request.Owner, request.Repo, request.Ref, request.FolderPath))?.Folder);

        public Task<IReadOnlyList<GitHubSkillFile>> ListFilesAsync(GitHubSkillFolder folder, CancellationToken cancellationToken = default)
        {
            var testFolder = _folders[Key(folder.Owner, folder.Repo, folder.Ref, folder.FolderPath)];
            return Task.FromResult<IReadOnlyList<GitHubSkillFile>>(testFolder.Files
                .Select(pair => new GitHubSkillFile(pair.Key, CombineGitHubPath(folder.FolderPath, pair.Key), pair.Value.Length))
                .ToArray());
        }

        public Task<byte[]> ReadFileAsync(GitHubSkillFolder folder, GitHubSkillFile file, CancellationToken cancellationToken = default)
            => Task.FromResult(_folders[Key(folder.Owner, folder.Repo, folder.Ref, folder.FolderPath)].Files[file.RelativePath]);

        private static string Key(string owner, string repo, string reference, string folderPath)
            => string.Join('|', owner, repo, reference, folderPath.Trim().Trim('/'));

        private static string CombineGitHubPath(string folderPath, string relativePath)
            => string.IsNullOrWhiteSpace(folderPath) ? relativePath : folderPath.Trim().Trim('/') + "/" + relativePath.Trim().Trim('/');

        private sealed record TestGitHubFolder(GitHubSkillFolder Folder, IReadOnlyDictionary<string, byte[]> Files);
    }

    private sealed class TestPackageContext(string rootPath) : IPackageContext
    {
        public string PackageId => SkillsPackageId;

        public Version Version { get; } = new(1, 0, 0);

        public string InstallPath => AppContext.BaseDirectory;

        public IPackageStorageContext Storage { get; } = new TestStorageContext(rootPath);

        public IPackageConfiguration Configuration { get; } = new TestConfiguration();

        public IPackageSecrets Secrets { get; } = new TestSecrets();

        public Microsoft.Extensions.Logging.ILoggerFactory LoggerFactory => Logging.LoggerFactory;

        public Sunder.Sdk.Logging.IPackageLogging Logging { get; } = Sunder.Sdk.Logging.NullPackageLogging.Instance;
    }

    private sealed class TestStorageContext : IPackageStorageContext
    {
        public TestStorageContext(string rootPath)
        {
            DataRootPath = Path.Combine(rootPath, "data");
            CacheRootPath = Path.Combine(rootPath, "cache");
            LogsRootPath = Path.Combine(rootPath, "logs");
            Directory.CreateDirectory(DataRootPath);
            Directory.CreateDirectory(CacheRootPath);
            Directory.CreateDirectory(LogsRootPath);
            Files = new TestFileStore(Path.Combine(rootPath, "files"));
            Directory.CreateDirectory(Files.RootPath);
        }

        public string DataRootPath { get; }

        public string CacheRootPath { get; }

        public string LogsRootPath { get; }

        public IPackageFileStore Files { get; }

        public IPackageKeyValueStore State { get; } = new TestKeyValueStore();
    }

    private sealed class TestFileStore(string rootPath) : IPackageFileStore
    {
        public string RootPath { get; } = rootPath;

        public string GetPath(string relativePath)
            => string.IsNullOrWhiteSpace(relativePath)
                ? RootPath
                : Path.Combine([RootPath, .. relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]);
    }

    private sealed class TestKeyValueStore : IPackageKeyValueStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? GetValue(string key) => _values.GetValueOrDefault(key);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(GetValue(key));

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(_values.ContainsKey(key));

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(_values.Keys.Where(key => prefix is null || key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    private sealed class TestConfiguration : IPackageConfiguration
    {
        public string? GetValue(string key) => null;
    }

    private sealed class TestSecrets : IPackageSecrets
    {
        public string? GetSecret(string key) => null;

        public void SetSecret(string key, string value)
        {
        }

        public void DeleteSecret(string key)
        {
        }
    }
}
