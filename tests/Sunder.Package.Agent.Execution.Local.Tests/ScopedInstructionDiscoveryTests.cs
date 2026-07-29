using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Execution.Docker;
using Sunder.Package.Agent.Execution.Local;
using Xunit;
using Xunit.Sdk;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class ScopedInstructionDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        OperatingSystem.IsMacOS() ? "/private" + Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) : Path.GetTempPath(),
        "sunder-scoped-instruction-tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("local")]
    [InlineData("docker")]
    public async Task Discovery_ReturnsOnlyExactRootToTargetAgentsFiles(string targetKind)
    {
        var workspace = Path.Combine(_root, "workspace");
        var target = Path.Combine(workspace, "src", "feature");
        var unrelated = Path.Combine(workspace, "other");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(unrelated);
        await File.WriteAllTextAsync(Path.Combine(workspace, "AGENTS.md"), "root instructions");
        await File.WriteAllTextAsync(Path.Combine(workspace, "src", "AGENTS.md"), "nested instructions");
        await File.WriteAllTextAsync(Path.Combine(unrelated, "AGENTS.md"), "unrelated instructions");
        await File.WriteAllTextAsync(Path.Combine(target, "agents.md"), "wrong case");

        var result = targetKind == "local"
            ? await DiscoverLocalAsync([workspace], target)
            : await DiscoverDockerAsync(workspace, target);

        var scope = Assert.Single(result.Scopes);
        Assert.Equal(Physical(target), scope.TargetDirectory);
        Assert.Equal(Physical(workspace), scope.ScopeRoot);
        Assert.Equal(2, scope.Documents.Count);
        Assert.Contains(scope.Documents, document => document.Content == "root instructions");
        Assert.Contains(scope.Documents, document => document.Content == "nested instructions");
        Assert.All(scope.Documents, document => Assert.False(document.WasTruncated));
        Assert.DoesNotContain(scope.Documents, document => document.Content.Contains("unrelated", StringComparison.Ordinal));
        Assert.False(result.WasTruncated);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("docker")]
    public async Task Discovery_RejectsOversizedInstructionFiles(string targetKind)
    {
        var workspace = Path.Combine(_root, "oversized", targetKind);
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "AGENTS.md"), new string('x', 12_001));

        var exception = targetKind == "local"
            ? await Assert.ThrowsAsync<InvalidOperationException>(async () => await DiscoverLocalAsync([workspace], workspace))
            : await Assert.ThrowsAsync<InvalidOperationException>(async () => await DiscoverDockerAsync(workspace, workspace));

        Assert.Contains("12000-character limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalDiscovery_UsesMostSpecificContainingRootAndRejectsOutsideAndSymlinkEscapes()
    {
        var outer = Path.Combine(_root, "outer");
        var inner = Path.Combine(outer, "inner");
        var target = Path.Combine(inner, "src");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outer, "AGENTS.md"), "outer");
        await File.WriteAllTextAsync(Path.Combine(inner, "AGENTS.md"), "inner");
        await File.WriteAllTextAsync(Path.Combine(outside, "AGENTS.md"), "outside");

        var result = await DiscoverLocalAsync([outer, inner], target, outside);

        var contained = Assert.Single(result.Scopes);
        Assert.Equal(Physical(inner), contained.ScopeRoot);
        Assert.DoesNotContain(contained.Documents, document => document.Content == "outer");
        Assert.Contains(contained.Documents, document => document.Content == "inner");

        var escape = Path.Combine(inner, "escape");
        try
        {
            Directory.CreateSymbolicLink(escape, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            throw SkipException.ForSkip($"Symbolic links are unavailable: {ex.Message}");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await DiscoverLocalAsync([outer, inner], Path.Combine(escape, "file.txt")));
    }

    [Fact]
    public async Task LocalDiscovery_RejectsAncestorChainsBeyondSixtyFour()
    {
        var workspace = Path.Combine(_root, "deep");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "AGENTS.md"), "root");
        var target = workspace;
        for (var index = 0; index < 70; index++)
        {
            target = Path.Combine(target, $"d{index}");
            Directory.CreateDirectory(target);
        }
        await File.WriteAllTextAsync(Path.Combine(target, "AGENTS.md"), "deepest");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await DiscoverLocalAsync([workspace], target));

        Assert.Contains("64-directory limit", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("docker")]
    public async Task Discovery_RejectsInstructionFileSymlinkEscapes(string targetKind)
    {
        var workspace = Path.Combine(_root, "symlink-workspace");
        var outside = Path.Combine(_root, "outside-policy.md");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(outside, "outside policy");
        try
        {
            File.CreateSymbolicLink(Path.Combine(workspace, "AGENTS.md"), outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException or NotSupportedException)
        {
            throw SkipException.ForSkip($"Symbolic links are unavailable: {ex.Message}");
        }

        if (targetKind == "local")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await DiscoverLocalAsync([workspace], workspace));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await DiscoverDockerAsync(workspace, workspace));
        }
    }

    [Fact]
    public async Task DockerDiscovery_FingerprintChangesWithContainerIdentity()
    {
        var workspace = Path.Combine(_root, "identity-workspace");
        Directory.CreateDirectory(workspace);
        var first = await DiscoverDockerAsync(
            workspace,
            workspace,
            "container-one image signature");
        var second = await DiscoverDockerAsync(
            workspace,
            workspace,
            "container-two image signature");

        Assert.NotEqual(first.TargetFingerprint, second.TargetFingerprint);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("docker")]
    public async Task FileTargetSymlink_UsesFollowedParentWhileDeleteUsesLinkParent(string targetKind)
    {
        var workspace = Path.Combine(_root, "target-link", targetKind, "workspace");
        var outside = Path.Combine(_root, "target-link", targetKind, "outside");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(workspace, "AGENTS.md"), "workspace policy");
        var target = Path.Combine(outside, "target.txt");
        await File.WriteAllTextAsync(target, "target");
        var link = Path.Combine(workspace, "link.txt");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException or NotSupportedException)
        {
            throw SkipException.ForSkip($"Symbolic links are unavailable: {ex.Message}");
        }

        var followedProbe = new AgentScopedInstructionProbe(link);
        var deleteProbe = new AgentScopedInstructionProbe(link) { FollowFinalSymbolicLink = false };
        if (targetKind == "local")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await DiscoverLocalProbesAsync([workspace], [followedProbe]));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await DiscoverLocalProbesAsync([workspace], [deleteProbe]));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await DiscoverDockerProbesAsync(workspace, [followedProbe]));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await DiscoverDockerProbesAsync(workspace, [deleteProbe]));
        }
    }

    [Fact]
    public async Task DockerDiscovery_PreservesNewlinePathsWithoutProtocolEncoding()
    {
        var workspace = Path.Combine(_root, "newline-workspace");
        var target = Path.Combine(workspace, "line\nbreak");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(workspace, "AGENTS.md"), "newline policy");

        var result = await DiscoverDockerAsync(workspace, target);

        Assert.Equal(Physical(target), Assert.Single(result.Scopes).TargetDirectory);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("docker")]
    public async Task Discovery_RejectsMoreThanSixtyFourProbes(string targetKind)
    {
        var workspace = Path.Combine(_root, "probe-limit", targetKind);
        Directory.CreateDirectory(workspace);
        var probes = Enumerable.Range(0, 65)
            .Select(index => new AgentScopedInstructionProbe(Path.Combine(workspace, index.ToString()), IsDirectory: true))
            .ToArray();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (targetKind == "local")
            {
                await DiscoverLocalProbesAsync([workspace], probes);
            }
            else
            {
                await DiscoverDockerProbesAsync(workspace, probes);
            }
        });
    }

    [Fact]
    public async Task DockerDiscovery_RejectsProbeOutsideConfiguredBind()
    {
        var workspace = Path.Combine(_root, "outside-bind");
        Directory.CreateDirectory(workspace);
        var outside = Path.Combine(_root, "not-mounted");
        Directory.CreateDirectory(outside);

        var exception = await Assert.ThrowsAsync<DockerStructuredBindRequiredException>(async () =>
            await DiscoverDockerProbesAsync(
                workspace,
                [new AgentScopedInstructionProbe(outside, IsDirectory: true)]));

        Assert.Equal(DockerPathResolver.StructuredBindRequiredErrorCode, exception.ErrorCode);
    }

    [Fact]
    public async Task DockerDiscovery_KeepsProbeBoundToSelectedContainerMountWhenHostRootsAreNested()
    {
        var outer = Path.Combine(_root, "nested-mounts", "outer");
        var inner = Path.Combine(outer, "inner");
        var target = Path.Combine(inner, "target");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(outer, "AGENTS.md"), "outer policy");
        await File.WriteAllTextAsync(Path.Combine(inner, "AGENTS.md"), "inner policy");
        var config = new DockerExecutionRuntimeConfig(
            "test-image",
            "test-container",
            "/bin/sh",
            [],
            [
                new DockerExecutionMount(outer, "/workspace/outer"),
                new DockerExecutionMount(inner, "/workspace/inner"),
            ],
            "/workspace/outer");

        var result = await new DockerFileSystemExecutor().DiscoverScopedInstructionsAsync(
            config,
            new AgentScopedInstructionDiscoveryRequest(
                [new AgentScopedInstructionProbe("/workspace/outer/inner/target", IsDirectory: true)]),
            "namespace",
            CancellationToken.None);

        var discoveredScope = Assert.Single(result.Scopes);
        Assert.Equal("/workspace/outer", discoveredScope.ScopeRoot);
        Assert.Equal("/workspace/outer/inner/target", discoveredScope.TargetDirectory);
        Assert.Equal(
            ["/workspace/outer/AGENTS.md", "/workspace/outer/inner/AGENTS.md"],
            discoveredScope.Documents.Select(document => document.Path));
    }

    private static async Task<AgentScopedInstructionDiscoveryResult> DiscoverLocalAsync(
        IReadOnlyList<string> roots,
        params string[] directories)
        => await DiscoverLocalProbesAsync(
            roots,
            directories.Select(path => new AgentScopedInstructionProbe(path, IsDirectory: true)).ToArray());

    private static async Task<AgentScopedInstructionDiscoveryResult> DiscoverLocalProbesAsync(
        IReadOnlyList<string> roots,
        IReadOnlyList<AgentScopedInstructionProbe> probes)
        => await LocalScopedInstructionDiscovery.DiscoverAsync(
            new LocalExecutionRuntimeConfig(
                roots.Select(Path.GetFullPath).ToArray(),
                Path.GetFullPath(roots[0])),
            new AgentScopedInstructionDiscoveryRequest(probes),
            CancellationToken.None);

    private static async Task<AgentScopedInstructionDiscoveryResult> DiscoverDockerAsync(
        string workspace,
        string target,
        string namespaceFingerprint = "test-container")
    {
        return await DiscoverDockerProbesAsync(
            workspace,
            [new AgentScopedInstructionProbe(target, IsDirectory: true)],
            namespaceFingerprint);
    }

    private static async Task<AgentScopedInstructionDiscoveryResult> DiscoverDockerProbesAsync(
        string workspace,
        IReadOnlyList<AgentScopedInstructionProbe> probes,
        string namespaceFingerprint = "test-container")
    {
        var config = new DockerExecutionRuntimeConfig(
            "test-image",
            "test-container",
            "/bin/sh",
            [],
            [new DockerExecutionMount(workspace, workspace)],
            workspace);
        return await new DockerFileSystemExecutor()
            .DiscoverScopedInstructionsAsync(
                config,
                new AgentScopedInstructionDiscoveryRequest(probes),
                namespaceFingerprint,
                CancellationToken.None);
    }

    private static string Physical(string path)
        => LocalPathResolver.ResolvePhysicalPath(path);

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
}
