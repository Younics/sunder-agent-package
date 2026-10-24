extern alias DockerWorker;

using DockerWorker::Sunder.Package.Agent.Execution.Docker;
using Xunit;
using DockerModels = DockerWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using AgentFileDeleteRequest = DockerWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileDeleteRequest;
using AgentFileMutationResult = DockerWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileMutationResult;
using AgentFileReadRequest = DockerWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileReadRequest;
using AgentFileReadResult = DockerWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileReadResult;
using AgentFileSearchKind = DockerWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileSearchKind;
using AgentFileSearchRequest = DockerWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileSearchRequest;
using AgentFileSearchResult = DockerWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileSearchResult;
using AgentFileWriteRequest = DockerWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileWriteRequest;
using AgentScopedInstructionDiscoveryRequest = DockerWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentScopedInstructionDiscoveryRequest;
using AgentScopedInstructionDiscoveryResult = DockerWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentScopedInstructionDiscoveryResult;
using AgentScopedInstructionProbe = DockerWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentScopedInstructionProbe;

namespace Sunder.Package.Agent.Execution.Local.Tests;

using DockerWorker::Sunder.Agent.Execution.Common;

public sealed class DockerHostBindRaceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        OperatingSystem.IsMacOS() ? "/private" + Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) : Path.GetTempPath(),
        "sunder-docker-host-races",
        Guid.NewGuid().ToString("N"));

    [FileSystemFact(
        TestFileSystemCapability.UnixDirectorySymbolicLinks,
        "Bind-parent replacement requires directory symbolic links on Unix.")]
    public async Task Read_UsesPinnedBindParentDuringRenameAndSymlinkReplacement()
    {
        var fixture = CreateFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Pinned, "safe.txt"), "safe");
        await File.WriteAllTextAsync(Path.Combine(fixture.Outside, "safe.txt"), "OUTSIDE");
        var hook = fixture.CreateSwapHook(LocalSecureFileSystemCheckpoint.BeforeOpen, "safe.txt");

        AgentFileReadResult result;
        try
        {
            result = await fixture.Executor.ReadFileAsync(
                fixture.Config,
                new AgentFileReadRequest { Path = "/workspace/pinned/safe.txt" },
                CancellationToken.None,
                hook);
        }
        finally
        {
            hook.Restore();
        }

        Assert.False(result.IsError, result.ErrorMessage);
        Assert.Equal("safe", result.Content);
        Assert.Equal("/workspace/pinned/safe.txt", result.Path);
        Assert.Equal("OUTSIDE", await File.ReadAllTextAsync(Path.Combine(fixture.Outside, "safe.txt")));
    }

    [FileSystemFact(
        TestFileSystemCapability.UnixDirectorySymbolicLinks,
        "Bind-parent replacement requires directory symbolic links on Unix.")]
    public async Task WriteAndDelete_UsePinnedBindParentDuringRenameAndSymlinkReplacement()
    {
        var fixture = CreateFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Pinned, "write.txt"), "old");
        await File.WriteAllTextAsync(Path.Combine(fixture.Outside, "write.txt"), "OUTSIDE");
        var writeHook = fixture.CreateSwapHook(LocalSecureFileSystemCheckpoint.BeforePublish, "write.txt");

        AgentFileMutationResult write;
        try
        {
            write = await fixture.Executor.WriteFileAsync(
                fixture.Config,
                new AgentFileWriteRequest { Path = "/workspace/pinned/write.txt", Content = "new", Overwrite = true },
                CancellationToken.None,
                writeHook);
        }
        finally
        {
            writeHook.Restore();
        }

        await File.WriteAllTextAsync(Path.Combine(fixture.Pinned, "delete.txt"), "safe");
        await File.WriteAllTextAsync(Path.Combine(fixture.Outside, "delete.txt"), "OUTSIDE");
        var deleteHook = fixture.CreateSwapHook(LocalSecureFileSystemCheckpoint.BeforeUnlink, "delete.txt");
        AgentFileMutationResult delete;
        try
        {
            delete = await fixture.Executor.DeleteFileAsync(
                fixture.Config,
                new AgentFileDeleteRequest { Path = "/workspace/pinned/delete.txt", Recursive = false },
                CancellationToken.None,
                deleteHook);
        }
        finally
        {
            deleteHook.Restore();
        }

        Assert.False(write.IsError, write.Summary);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(fixture.Pinned, "write.txt")));
        Assert.Equal("OUTSIDE", await File.ReadAllTextAsync(Path.Combine(fixture.Outside, "write.txt")));
        Assert.False(delete.IsError, delete.Summary);
        Assert.False(File.Exists(Path.Combine(fixture.Pinned, "delete.txt")));
        Assert.Equal("OUTSIDE", await File.ReadAllTextAsync(Path.Combine(fixture.Outside, "delete.txt")));
    }

    [FileSystemFact(
        TestFileSystemCapability.UnixDirectorySymbolicLinks,
        "Bind-parent replacement requires directory symbolic links on Unix.")]
    public async Task Search_UsesPinnedBindParentAndReturnsContainerPathsDuringReplacement()
    {
        var fixture = CreateFixture();
        var search = Path.Combine(fixture.Pinned, "search");
        var outsideSearch = Path.Combine(fixture.Outside, "search");
        Directory.CreateDirectory(search);
        Directory.CreateDirectory(outsideSearch);
        await File.WriteAllTextAsync(Path.Combine(search, "match.txt"), "needle");
        await File.WriteAllTextAsync(Path.Combine(outsideSearch, "secret.txt"), "needle OUTSIDE");
        var hook = fixture.CreateSwapHook(LocalSecureFileSystemCheckpoint.BeforeOpen, "search");

        AgentFileSearchResult result;
        try
        {
            result = await fixture.Executor.SearchAsync(
                fixture.Config,
                new AgentFileSearchRequest
                {
                    Hidden = false, Literal = false,
                    Path = "/workspace/pinned/search",
                    Kind = AgentFileSearchKind.Grep,
                    Pattern = "needle",
                    Include = "*.txt",
                },
                CancellationToken.None,
                hook);
        }
        finally
        {
            hook.Restore();
        }

        var match = Assert.Single(result.Matches);
        Assert.False(result.IsError, result.ErrorMessage);
        Assert.Equal("/workspace/pinned/search/match.txt", match.Path);
        Assert.Equal("needle", match.Text);
        Assert.Equal("needle OUTSIDE", await File.ReadAllTextAsync(Path.Combine(outsideSearch, "secret.txt")));
    }

    [FileSystemFact(
        TestFileSystemCapability.UnixDirectorySymbolicLinks,
        "Bind-parent replacement requires directory symbolic links on Unix.")]
    public async Task ScopedDiscovery_UsesPinnedBindParentAndReturnsContainerPathsDuringReplacement()
    {
        var fixture = CreateFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Pinned, "AGENTS.md"), "safe policy");
        await File.WriteAllTextAsync(Path.Combine(fixture.Outside, "AGENTS.md"), "OUTSIDE policy");
        var hook = fixture.CreateSwapHook(LocalSecureFileSystemCheckpoint.BeforeOpen, "AGENTS.md");

        AgentScopedInstructionDiscoveryResult result;
        try
        {
            result = await fixture.Executor.DiscoverScopedInstructionsAsync(
                fixture.Config,
                new AgentScopedInstructionDiscoveryRequest
                {
                    Probes = [new AgentScopedInstructionProbe { Path = "/workspace/pinned", IsDirectory = true, FollowFinalSymbolicLink = true }],
                },
                "namespace",
                CancellationToken.None,
                hook);
        }
        finally
        {
            hook.Restore();
        }

        var scope = Assert.Single(result.Scopes);
        var document = Assert.Single(scope.Documents);
        Assert.Equal("/workspace/pinned", scope.TargetDirectory);
        Assert.Equal("/workspace", scope.ScopeRoot);
        Assert.Equal("/workspace/pinned/AGENTS.md", document.Path);
        Assert.Equal("safe policy", document.Content);
        Assert.Equal("OUTSIDE policy", await File.ReadAllTextAsync(Path.Combine(fixture.Outside, "AGENTS.md")));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    private Fixture CreateFixture()
    {
        var workspace = Path.Combine(_root, "workspace");
        var pinned = Path.Combine(workspace, "pinned");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(pinned);
        Directory.CreateDirectory(outside);
        var config = new DockerExecutionRuntimeConfig(
            "image",
            "container",
            "/bin/sh",
            [],
            [new DockerExecutionMount(workspace, "/workspace")],
            "/workspace");
        return new Fixture(pinned, outside, config, new DockerFileSystemExecutor());
    }

    private sealed record Fixture(
        string Pinned,
        string Outside,
        DockerExecutionRuntimeConfig Config,
        DockerFileSystemExecutor Executor)
    {
        public ParentSwapHook CreateSwapHook(LocalSecureFileSystemCheckpoint checkpoint, string entryName)
            => new(Pinned, Outside, checkpoint, entryName);
    }

    private sealed class ParentSwapHook(
        string parent,
        string outside,
        LocalSecureFileSystemCheckpoint checkpoint,
        string entryName) : ILocalSecureFileSystemHooks
    {
        private readonly string _parked = parent + "-parked";
        private bool _invoked;
        private bool _swapped;

        public void OnCheckpoint(LocalSecureFileSystemCheckpoint current, string parentPath, string? currentEntryName)
        {
            if (_invoked
                || current != checkpoint
                || !HostSecurePathEngine.PathComparer.Equals(parentPath, parent)
                || !string.Equals(currentEntryName, entryName, StringComparison.Ordinal))
            {
                return;
            }

            _invoked = true;
            try
            {
                Directory.Move(parent, _parked);
                Directory.CreateSymbolicLink(parent, outside);
                _swapped = true;
            }
            catch (IOException) when (OperatingSystem.IsWindows())
            {
                // A held ancestor without FILE_SHARE_DELETE blocks replacement on Windows.
            }
            catch (Exception) when (!OperatingSystem.IsWindows())
            {
                if (Directory.Exists(_parked) && !Directory.Exists(parent))
                {
                    Directory.Move(_parked, parent);
                }
                throw;
            }
        }

        public void Restore()
        {
            Assert.True(_invoked, "The deterministic secure-filesystem checkpoint was not reached.");
            if (!_swapped)
            {
                return;
            }
            Directory.Delete(parent);
            Directory.Move(_parked, parent);
            _swapped = false;
        }
    }
}
