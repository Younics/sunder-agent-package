using System.Text;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Skills.PackageViews;
using Sunder.Package.Agent.Skills.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;
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
    public async Task ImportLocalSkillsAsync_ImportsNestedSkillFolders()
    {
        var root = CreateTempRoot();
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(Path.Combine(source, "skills", "one"));
            Directory.CreateDirectory(Path.Combine(source, "skills", "two"));
            await File.WriteAllTextAsync(Path.Combine(source, "skills", "one", "SKILL.md"), """
                ---
                name: one
                description: First skill.
                ---
                # One
                """);
            await File.WriteAllTextAsync(Path.Combine(source, "skills", "two", "SKILL.md"), """
                ---
                name: two
                description: Second skill.
                ---
                # Two
                """);
            var context = new TestPackageContext(root);
            var store = new SkillStore(context);
            var importer = new SkillImportService(store, new TestGitHubSkillClient(), context);

            var imported = await importer.ImportLocalSkillsAsync(source);

            Assert.Equal(2, imported.Count);
            Assert.NotNull(store.GetSkill("one"));
            Assert.NotNull(store.GetSkill("two"));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ImportGitHubAsync_ImportsRepositoryWithMultipleSkills()
    {
        var root = CreateTempRoot();
        try
        {
            var context = new TestPackageContext(root);
            var store = new SkillStore(context);
            var gitHub = new TestGitHubSkillClient();
            gitHub.AddFolder("meshy-dev", "meshy-3d-agent", "main", string.Empty, "abc123", new Dictionary<string, string>
            {
                ["skills/meshy-3d-generation/SKILL.md"] = "---\nname: meshy-3d-generation\ndescription: Generate 3D assets.\n---\n# Meshy 3D Generation",
                ["skills/meshy-openclaw/SKILL.md"] = "---\nname: meshy-openclaw\ndescription: OpenClaw workflows.\n---\n# Meshy OpenClaw",
            });
            gitHub.AddFolder("meshy-dev", "meshy-3d-agent", "main", "skills/meshy-3d-generation", "abc123", new Dictionary<string, string>
            {
                ["SKILL.md"] = "---\nname: meshy-3d-generation\ndescription: Generate 3D assets.\n---\n# Meshy 3D Generation",
            });
            gitHub.AddFolder("meshy-dev", "meshy-3d-agent", "main", "skills/meshy-openclaw", "abc123", new Dictionary<string, string>
            {
                ["SKILL.md"] = "---\nname: meshy-openclaw\ndescription: OpenClaw workflows.\n---\n# Meshy OpenClaw",
            });
            var importer = new SkillImportService(store, gitHub, context);

            var imported = await importer.ImportGitHubAsync("https://github.com/meshy-dev/meshy-3d-agent");

            Assert.Equal(2, imported.Count);
            Assert.NotNull(store.GetSkill("meshy-3d-generation"));
            Assert.NotNull(store.GetSkill("meshy-openclaw"));
            Assert.All(imported, skill => Assert.Equal("github", skill.SourceKind));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
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

            Assert.Null(viewModel.SelectedSkill);
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
            var timeProvider = new ManualTimerTimeProvider();
            using var viewModel = new SkillSettingsViewModel(store, importer, timeProvider);

            await viewModel.ImportLocalFolderAsync(source);

            Assert.True(viewModel.IsDetailActive);
            Assert.Equal("docs-skill", viewModel.SelectedSkill?.SkillId);
            Assert.Equal("Imported local skill folder.", viewModel.StatusText);
            Assert.True(viewModel.IsStatusSuccess);
            Assert.False(viewModel.IsStatusWarning);
            Assert.False(viewModel.IsStatusError);

            var statusCleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            viewModel.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(viewModel.StatusText)
                    && string.IsNullOrWhiteSpace(viewModel.StatusText))
                {
                    statusCleared.TrySetResult();
                }
            };
            await timeProvider.FireAsync();
            await statusCleared.Task.WaitAsync(TimeSpan.FromSeconds(2));

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

    [Fact]
    public async Task SkillStackContributor_ListExportItemsAsync_IgnoresLocalSkills()
    {
        var root = CreateTempRoot();
        try
        {
            var source = Path.Combine(root, "source", "docs-skill");
            Directory.CreateDirectory(Path.Combine(source, "references"));
            await File.WriteAllTextAsync(Path.Combine(source, "SKILL.md"), """
                ---
                name: docs-skill
                description: Docs skill resources.
                ---
                # Docs Skill
                """);
            await File.WriteAllTextAsync(Path.Combine(source, "references", "guide.md"), "guide");
            var context = new TestPackageContext(Path.Combine(root, "install"));
            var store = new SkillStore(context);
            var importer = new SkillImportService(store, new TestGitHubSkillClient(), context);
            await importer.ImportLocalFolderAsync(source);
            var contributor = new SkillStackContributor(store, importer, context);

            var items = await contributor.ListExportItemsAsync(new StackExportDiscoveryContext(SkillsPackageId));
            var contribution = await contributor.ExportAsync(new StackExportRequest(
                [new StackExportItemSelection("docs-skill")]));

            Assert.Empty(items);
            Assert.Empty(contribution.Fragments);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SkillStackContributor_ExportAsync_ExportsGitHubUrlOnly()
    {
        var root = CreateTempRoot();
        try
        {
            var githubUrl = "https://github.com/acme/skills/tree/main/docs-skill";
            var githubClient = new TestGitHubSkillClient();
            githubClient.AddFolder("acme", "skills", "main", "docs-skill", "abc123", DocsSkillFiles());
            var context = new TestPackageContext(Path.Combine(root, "install"));
            var store = new SkillStore(context);
            var importer = new SkillImportService(store, githubClient, context);
            await importer.ImportGitHubFolderAsync(githubUrl);
            var contributor = new SkillStackContributor(store, importer, context);

            var item = Assert.Single(await contributor.ListExportItemsAsync(
                new StackExportDiscoveryContext(SkillsPackageId)));
            var contribution = await contributor.ExportAsync(new StackExportRequest(
                [new StackExportItemSelection("docs-skill")]));

            var fragment = Assert.Single(contribution.Fragments);
            Assert.All(item.Details ?? [], detail => Assert.NotNull(detail.Sensitivity));
            Assert.Equal("1.1.0", Assert.Single(contribution.PackageRequirements).MinimumVersion);
            Assert.Equal("github-skill.docs-skill", fragment.FragmentId);
            Assert.Null(fragment.Files);
            Assert.Contains(githubUrl, fragment.JsonPayload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("references/guide.md", fragment.JsonPayload, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SkillStackContributor_ImportAsync_ImportsGitHubSkillByUrl()
    {
        var root = CreateTempRoot();
        try
        {
            var githubUrl = "https://github.com/acme/skills/tree/main/docs-skill";
            var githubClient = new TestGitHubSkillClient();
            githubClient.AddFolder("acme", "skills", "main", "docs-skill", "abc123", DocsSkillFiles());
            var sourceContext = new TestPackageContext(Path.Combine(root, "source-install"));
            var sourceStore = new SkillStore(sourceContext);
            var sourceImporter = new SkillImportService(sourceStore, githubClient, sourceContext);
            await sourceImporter.ImportGitHubFolderAsync(githubUrl);
            var sourceContributor = new SkillStackContributor(sourceStore, sourceImporter, sourceContext);
            var fragment = Assert.Single((await sourceContributor.ExportAsync(new StackExportRequest(
                [new StackExportItemSelection("docs-skill")]))).Fragments);

            var targetContext = new TestPackageContext(Path.Combine(root, "target-install"));
            var targetStore = new SkillStore(targetContext);
            var targetImporter = new SkillImportService(targetStore, githubClient, targetContext);
            var targetContributor = new SkillStackContributor(targetStore, targetImporter, targetContext);
            var importFragment = ToImportFragment(fragment);
            var preview = await targetContributor.PreviewImportAsync(new StackImportPreviewRequest(
                [importFragment],
                new Dictionary<string, string>(),
                new Dictionary<string, string>()));
            var action = Assert.Single(preview.Actions);

            var result = await targetContributor.ImportAsync(new StackImportRequest(
                [importFragment],
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                [action.ActionId]));

            Assert.Equal(StackImportOutcome.Completed, result.Outcome);
            var importedSkill = targetStore.GetSkill("docs-skill");
            Assert.NotNull(importedSkill);
            Assert.Equal("github", importedSkill.SourceKind);
            Assert.Equal(githubUrl, importedSkill.SourceUri);
            Assert.True(File.Exists(Path.Combine(targetStore.GetSkillRootPath(importedSkill), "references", "guide.md")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-skill-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static StackFragmentImport ToImportFragment(StackFragmentExport fragment)
        => new(
            fragment.FragmentId,
            SkillsPackageId,
            "sunder.package.agent.skills.github-skills",
            fragment.SchemaId,
            fragment.SchemaVersion,
            fragment.DisplayName,
            fragment.JsonPayload,
            fragment.Description,
            fragment.Files?.Select(file => new StackImportPayloadHandle(
                file.RelativePath,
                file.OpenReadAsync,
                file.Length ?? throw new InvalidOperationException("Test export payload length is required."))).ToArray());

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

    private sealed class TestExtensionCatalog : RegressionTestExtensionCatalog
    {
        public TestExtensionCatalog(AgentProfileRecord profile)
        {
            AddProvider(AgentRpcServices.RuntimeCatalogs, new TestRuntimeCatalog(profile));
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

        public IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(string workspaceId) => [];

        public AgentSessionRecord? GetSession(Guid sessionId) => null;

        public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => [];

        public AgentWorkspaceRecord? GetWorkspace(string workspaceId) => null;

        public AgentProfileRecord? GetSessionProfile(Guid sessionId) => null;

        public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId) => null;

        public AgentSessionContextCheckpointRecord? GetLatestSessionContextCheckpoint(Guid sessionId) => null;

        public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => null;

        public IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit) => [];

        public IReadOnlyList<AgentTurnRecord> ListTurnsBefore(Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit) => [];

        public IReadOnlyList<AgentTurnRecord> ListTurnsAfter(Guid sessionId, DateTimeOffset afterCreatedAtUtc, Guid afterTurnId, int limit) => [];

        public IReadOnlyList<AgentProfileRecord> ListProfiles() => [profile];

        public AgentProfileRecord? GetProfile(string profileId) => profile.ProfileId == profileId ? profile : null;

        public AgentProfileModelBindingRecord? GetSessionModelBinding(Guid sessionId, string capabilityKind) => null;

        public AgentProfileModelBindingRecord? GetModelBinding(string profileId, string capabilityKind) => null;

        public void RaiseProfileChanged() => ProfileChanged?.Invoke(profile.ProfileId);
    }

    private sealed class TestGitHubSkillClient : IGitHubSkillClient
    {
        private readonly Dictionary<string, TestGitHubFolder> _folders = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _defaultBranches = new(StringComparer.OrdinalIgnoreCase);

        public void AddFolder(
            string owner,
            string repo,
            string reference,
            string folderPath,
            string commitSha,
            IReadOnlyDictionary<string, string> files)
        {
            _defaultBranches.TryAdd(RepoKey(owner, repo), reference);
            _folders[Key(owner, repo, reference, folderPath)] = new TestGitHubFolder(
                new GitHubSkillFolder(owner, repo, reference, folderPath, commitSha, "tree-" + commitSha),
                files.ToDictionary(pair => pair.Key, pair => Encoding.UTF8.GetBytes(pair.Value), StringComparer.Ordinal));
        }

        public Task<string?> TryGetDefaultBranchAsync(string owner, string repo, CancellationToken cancellationToken = default)
            => Task.FromResult(_defaultBranches.GetValueOrDefault(RepoKey(owner, repo)));

        public Task<GitHubSkillFolder?> TryGetFolderAsync(GitHubSkillFolderRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(FindFolder(request)?.Folder);

        public Task<GitHubSkillFolder?> TryGetSkillFolderAsync(GitHubSkillFolderRequest request, CancellationToken cancellationToken = default)
        {
            var folder = FindFolder(request);
            return Task.FromResult(folder?.Files.ContainsKey("SKILL.md") == true ? folder.Folder : null);
        }

        public Task<IReadOnlyList<GitHubSkillFile>> ListFilesAsync(GitHubSkillFolder folder, CancellationToken cancellationToken = default)
        {
            var testFolder = FindFolder(folder);
            return Task.FromResult<IReadOnlyList<GitHubSkillFile>>(testFolder.Files
                .Select(pair => new GitHubSkillFile(pair.Key, CombineGitHubPath(folder.FolderPath, pair.Key), pair.Value.Length))
                .ToArray());
        }

        public Task<byte[]> ReadFileAsync(GitHubSkillFolder folder, GitHubSkillFile file, CancellationToken cancellationToken = default)
            => Task.FromResult(FindFolder(folder).Files[file.RelativePath]);

        private TestGitHubFolder? FindFolder(GitHubSkillFolderRequest request)
            => _folders.GetValueOrDefault(Key(request.Owner, request.Repo, request.Ref, request.FolderPath))
               ?? _folders.Values.FirstOrDefault(folder =>
                   string.Equals(folder.Folder.Owner, request.Owner, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(folder.Folder.Repo, request.Repo, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(folder.Folder.CommitSha, request.Ref, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(folder.Folder.FolderPath, request.FolderPath, StringComparison.Ordinal));

        private TestGitHubFolder FindFolder(GitHubSkillFolder folder)
            => _folders.GetValueOrDefault(Key(folder.Owner, folder.Repo, folder.Ref, folder.FolderPath))
               ?? _folders.Values.Single(candidate =>
                   string.Equals(candidate.Folder.Owner, folder.Owner, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(candidate.Folder.Repo, folder.Repo, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(candidate.Folder.CommitSha, folder.CommitSha, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(candidate.Folder.FolderPath, folder.FolderPath, StringComparison.Ordinal));

        private static string Key(string owner, string repo, string reference, string folderPath)
            => string.Join('|', owner, repo, reference, folderPath.Trim().Trim('/'));

        private static string RepoKey(string owner, string repo)
            => string.Join('|', owner, repo);

        private static string CombineGitHubPath(string folderPath, string relativePath)
            => string.IsNullOrWhiteSpace(folderPath) ? relativePath : folderPath.Trim().Trim('/') + "/" + relativePath.Trim().Trim('/');

        private sealed record TestGitHubFolder(GitHubSkillFolder Folder, IReadOnlyDictionary<string, byte[]> Files);
    }

    private sealed class ManualTimerTimeProvider : TimeProvider
    {
        private readonly TaskCompletionSource<ManualTimer> _timerCreated =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            if (!_timerCreated.TrySetResult(timer))
            {
                throw new InvalidOperationException("The test expected one status timer.");
            }
            return timer;
        }

        public async Task FireAsync()
        {
            var timer = await _timerCreated.Task.WaitAsync(TimeSpan.FromSeconds(2));
            timer.Fire();
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private int _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
                => Volatile.Read(ref _disposed) == 0;

            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Fire()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    callback(state);
                }
            }
        }
    }

    private sealed class TestPackageContext(string rootPath) : IPackageContext
    {
        public string PackageId => SkillsPackageId;

        public string Version { get; } = "1.0.0";

        public string ContentRootPath => AppContext.BaseDirectory;

        public IPackageStorageContext Storage { get; } = new TestStorageContext(rootPath);

        public IPackageSettings Settings { get; } = new TestSettings();

        public IPackageSecrets Secrets { get; } = new TestSecrets();


        public Sunder.Sdk.Logging.IPackageLogging Logging { get; } = Sunder.Sdk.Logging.NullPackageLogging.Instance;
    }

    private sealed class TestStorageContext : IPackageStorageContext
    {
        public TestStorageContext(string rootPath)
        {
            Directory.CreateDirectory(rootPath);
            Files = new TestFileStore(Path.Combine(rootPath, "files"));
            RoleLocalWorkspace = new TestPackageRoleLocalWorkspace(rootPath);
        }

        public IPackageFileStore Files { get; }

        public IPackageKeyValueStore State { get; } = new TestKeyValueStore();
        public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; }
    }

    private sealed class TestFileStore(string rootPath) : TestPackageFileStoreBase(rootPath);

    private sealed class TestKeyValueStore : IPackageKeyValueStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult(_values.GetValueOrDefault(key));
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            TestPackageStorageGuards.Value(value);
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult(_values.ContainsKey(key));
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Prefix(prefix);
            return Task.FromResult<IReadOnlyList<string>>(_values.Keys
                .Where(key => prefix is null || key.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray());
        }
    }

    private sealed class TestSettings : EmptyPackageSettings;

    private sealed class TestSecrets : InMemoryPackageSecrets;
}
