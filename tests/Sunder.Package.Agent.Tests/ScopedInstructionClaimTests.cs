using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.Tools.Files;
using Sunder.Package.Agent.Tools.Shell;
using Xunit;
using Xunit.Sdk;

namespace Sunder.Package.Agent.Tests;

public sealed class ScopedInstructionClaimTests
{
    [Fact]
    public async Task NestedInstructions_AreLazyAndChangedInstructionsDeferBeforeMutationAcrossRestart()
    {
        using var fixture = await Fixture.CreateAsync();
        var nested = Path.Combine(fixture.Root, "src", "feature");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "AGENTS.md"), "root policy");
        await File.WriteAllTextAsync(Path.Combine(nested, "AGENTS.md"), "nested policy v1");
        var source = fixture.CreateSource();

        var initial = await PresentAsync(source, fixture.PromptRequest(epoch: 1));

        Assert.Single(initial);
        Assert.Contains("root policy", initial[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("nested policy", initial[0].Content, StringComparison.Ordinal);

        var listing = await source.ExecuteAsync(
            fixture.ToolContext(epoch: 1),
            Request("read", new { path = "." }));
        Assert.False(listing.IsError, listing.Summary);
        Assert.False(listing.RequiresPromptContextRefresh);

        var deferred = await source.ExecuteAsync(
            fixture.ToolContext(epoch: 1),
            Request("write", new { path = "src/feature/output.txt", content = "first" }));

        AssertDeferred(deferred);
        Assert.False(File.Exists(Path.Combine(nested, "output.txt")));

        var refreshed = await PresentAsync(source, fixture.PromptRequest(epoch: 1));
        Assert.Equal(2, refreshed.Count);
        Assert.Contains(refreshed, block => block.Content.Contains("nested policy v1", StringComparison.Ordinal));

        var restarted = fixture.CreateSource();
        var written = await restarted.ExecuteAsync(
            fixture.ToolContext(epoch: 1),
            Request("write", new { path = "src/feature/output.txt", content = "first" }));
        Assert.False(written.IsError, written.Summary);
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(nested, "output.txt")));

        await File.WriteAllTextAsync(Path.Combine(nested, "AGENTS.md"), "nested policy v2");
        var changed = await restarted.ExecuteAsync(
            fixture.ToolContext(epoch: 1),
            Request("write", new { path = "src/feature/output.txt", content = "second" }));

        AssertDeferred(changed);
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(nested, "output.txt")));
        var changedContext = await PresentAsync(restarted, fixture.PromptRequest(epoch: 1));
        Assert.Contains(changedContext, block => block.Content.Contains("nested policy v2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Claims_AreIsolatedAndInvalidateForEpochBindingAndCleanup()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "AGENTS.md"), "root policy");
        var source = fixture.CreateSource();
        var firstSession = Guid.NewGuid();
        var secondSession = Guid.NewGuid();
        await PresentAsync(source, fixture.PromptRequest(firstSession, epoch: 1));

        var first = await source.ExecuteAsync(
            fixture.ToolContext(firstSession, epoch: 1),
            Request("write", new { path = "first.txt", content = "first" }));
        var isolated = await source.ExecuteAsync(
            fixture.ToolContext(secondSession, epoch: 1),
            Request("write", new { path = "isolated.txt", content = "isolated" }));
        var rolledBack = await source.ExecuteAsync(
            fixture.ToolContext(firstSession, epoch: 2),
            Request("write", new { path = "epoch.txt", content = "epoch" }));
        var alternateBinding = await fixture.CreateBindingAsync("alternate-binding");
        var rebound = await source.ExecuteAsync(
            fixture.ToolContext(firstSession, epoch: 1, binding: alternateBinding),
            Request("write", new { path = "binding.txt", content = "binding" }));

        Assert.False(first.IsError, first.Summary);
        AssertDeferred(isolated);
        AssertDeferred(rolledBack);
        AssertDeferred(rebound);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "first.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "isolated.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "epoch.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "binding.txt")));
        Assert.NotEqual(fixture.ClaimPath(firstSession), fixture.ClaimPath(secondSession));

        await PresentAsync(source, fixture.PromptRequest(firstSession, epoch: 1));
        Assert.True(File.Exists(fixture.ClaimPath(firstSession)));
        source.DeleteSessionData(firstSession);
        Assert.False(File.Exists(fixture.ClaimPath(firstSession)));
        var afterCleanup = await source.ExecuteAsync(
            fixture.ToolContext(firstSession, epoch: 1),
            Request("write", new { path = "after-cleanup.txt", content = "cleaned" }));
        AssertDeferred(afterCleanup);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "after-cleanup.txt")));
    }

    [Fact]
    public async Task CorruptCurrentClaimState_IsQuarantinedAndRebuilt()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "AGENTS.md"), "root policy");
        var source = fixture.CreateSource();
        var sessionId = Guid.NewGuid();
        var claimPath = fixture.ClaimPath(sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(claimPath)!);
        const string invalid = "{not-json";
        await File.WriteAllTextAsync(claimPath, invalid);

        await PresentAsync(source, fixture.PromptRequest(sessionId, epoch: 1));
        var result = await source.ExecuteAsync(
            fixture.ToolContext(sessionId, epoch: 1),
            Request("write", new { path = "blocked.txt", content = "blocked" }));
        Assert.False(result.IsError, result.Summary);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "blocked.txt")));
        Assert.NotEqual(invalid, await File.ReadAllTextAsync(claimPath));
        Assert.True(File.Exists(claimPath + ".corrupt"));
    }

    [Fact]
    public async Task OversizedCurrentClaimState_IsReadBoundedQuarantinedAndRebuilt()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "AGENTS.md"), "root policy");
        var source = fixture.CreateSource();
        var sessionId = Guid.NewGuid();
        var claimPath = fixture.ClaimPath(sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(claimPath)!);
        await File.WriteAllBytesAsync(claimPath, new byte[ScopedInstructionClaimStore.MaxStoreBytes + 1]);

        var blocks = await PresentAsync(source, fixture.PromptRequest(sessionId, epoch: 1));

        Assert.Single(blocks);
        Assert.True(new FileInfo(claimPath).Length < ScopedInstructionClaimStore.MaxStoreBytes);
        Assert.Equal(ScopedInstructionClaimStore.MaxStoreBytes + 1, new FileInfo(claimPath + ".corrupt").Length);
    }

    [Fact]
    public async Task FutureClaimState_RemainsUntouchedAndFailsClosed()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "AGENTS.md"), "root policy");
        var source = fixture.CreateSource();
        var sessionId = Guid.NewGuid();
        var claimPath = fixture.ClaimPath(sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(claimPath)!);
        var future = JsonSerializer.Serialize(new { version = 3, sessionId });
        await File.WriteAllTextAsync(claimPath, future);

        var result = await source.ExecuteAsync(
            fixture.ToolContext(sessionId, epoch: 1),
            Request("write", new { path = "blocked.txt", content = "blocked" }));

        Assert.True(result.IsError);
        Assert.Equal("files-scoped-instruction-claims-future-version", result.ErrorCode);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await source.ContributeContextAsync(fixture.PromptRequest(sessionId, epoch: 1)));
        Assert.Equal(future, await File.ReadAllTextAsync(claimPath));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "blocked.txt")));
    }

    [Fact]
    public async Task MultiRootAndApprovedExternalAccess_DoNotLeakNestedOrExternalInstructions()
    {
        using var fixture = await Fixture.CreateAsync(rootCount: 2);
        var firstRoot = fixture.Roots[0];
        var secondRoot = fixture.Roots[1];
        var firstNested = Path.Combine(firstRoot, "nested");
        var outside = Path.Combine(fixture.ScopeRoot, "outside");
        Directory.CreateDirectory(firstNested);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(firstRoot, "AGENTS.md"), "first root");
        await File.WriteAllTextAsync(Path.Combine(firstNested, "AGENTS.md"), "first nested");
        await File.WriteAllTextAsync(Path.Combine(secondRoot, "AGENTS.md"), "second root");
        await File.WriteAllTextAsync(Path.Combine(outside, "AGENTS.md"), "external secret policy");
        await File.WriteAllTextAsync(Path.Combine(outside, "external-match.txt"), "external match");
        var source = fixture.CreateSource();
        var sessionId = Guid.NewGuid();

        var initial = await PresentAsync(source, fixture.PromptRequest(sessionId, epoch: 1));
        var secondWrite = await source.ExecuteAsync(
            fixture.ToolContext(sessionId, epoch: 1),
            Request("write", new { path = Path.Combine(secondRoot, "allowed.txt"), content = "allowed" }));
        var nestedWrite = await source.ExecuteAsync(
            fixture.ToolContext(sessionId, epoch: 1),
            Request("write", new { path = Path.Combine(firstNested, "deferred.txt"), content = "deferred" }));
        var externalWrite = await source.ExecuteAsync(
            await fixture.ApprovedToolContextAsync(
                sessionId,
                epoch: 1,
                Path.Combine(outside, "allowed.txt"),
                "files.mutate"),
            Request("write", new { path = Path.Combine(outside, "allowed.txt"), content = "external" }));
        var externalSearch = await source.ExecuteAsync(
            await fixture.ApprovedToolContextAsync(sessionId, epoch: 1, outside, "files.search"),
            Request("glob", new { pattern = "*.txt", path = outside }));

        Assert.Equal(2, initial.Count);
        Assert.DoesNotContain(initial, block => block.Content.Contains("first nested", StringComparison.Ordinal));
        Assert.False(secondWrite.IsError, secondWrite.Summary);
        AssertDeferred(nestedWrite);
        Assert.False(externalWrite.IsError, externalWrite.Summary);
        Assert.False(externalSearch.IsError, externalSearch.Content);
        Assert.Contains("external-match.txt", externalSearch.Content, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(secondRoot, "allowed.txt")));
        Assert.False(File.Exists(Path.Combine(firstNested, "deferred.txt")));
        Assert.True(File.Exists(Path.Combine(outside, "allowed.txt")));
        Assert.DoesNotContain("outside", await File.ReadAllTextAsync(fixture.ClaimPath(sessionId)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutsideCapability_TransfersAcrossPreflightExecutionAndPostAccessThenReleasesUnusedAuthority()
    {
        using var fixture = await Fixture.CreateAsync();
        var outside = Path.Combine(fixture.ScopeRoot, "outside-lifecycle");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "AGENTS.md"), "root policy");
        await File.WriteAllTextAsync(Path.Combine(outside, "AGENTS.md"), "external secret policy");
        var path = Path.Combine(outside, "approved.txt");
        await File.WriteAllTextAsync(path, "approved content");
        var source = fixture.CreateSource();
        await PresentAsync(source, fixture.PromptRequest(epoch: 1));
        var context = await fixture.ApprovedToolContextAsync(
            fixture.SessionId,
            epoch: 1,
            path,
            "files.read",
            authorityUseCount: 2);
        var claim = Assert.Single(context.ApprovedResourceClaims);
        var operation = Assert.IsType<AgentResourceOperationContext>(context.ResourceOperation);
        var capabilities = context.ApprovedResourceCapabilities.ToArray();
        var request = Request("read", new { path });

        var preflight = await source.PreflightExecutionAsync(context, request);

        Assert.Null(preflight);
        Assert.Equal(2, capabilities.Length);
        Assert.All(capabilities, capability =>
            Assert.True(fixture.IsCurrent(capability, claim, operation)));

        var result = await source.ExecuteAsync(context, request);

        Assert.False(result.IsError, result.Content);
        Assert.Contains("approved content", result.Content, StringComparison.Ordinal);
        Assert.All(capabilities, capability =>
            Assert.False(fixture.IsCurrent(capability, claim, operation)));
        Assert.DoesNotContain(
            "external secret policy",
            await File.ReadAllTextAsync(fixture.ClaimPath(fixture.SessionId)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromptRenderingAndPatchPreflight_EnforceHardLimits()
    {
        using var fixture = await Fixture.CreateAsync(rootCount: 25);
        foreach (var (root, index) in fixture.Roots.Select((root, index) => (root, index)))
        {
            await File.WriteAllTextAsync(Path.Combine(root, "AGENTS.md"), $"policy-{index}\n" + new string('x', 3_000));
        }
        var source = fixture.CreateSource();
        var sessionId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await source.ContributeContextAsync(fixture.PromptRequest(sessionId, epoch: 1)));
        var patch = new StringBuilder("*** Begin Patch\n");
        for (var index = 0; index < 65; index++)
        {
            patch.Append("*** Add File: limited-").Append(index).Append(".txt\n+value\n");
        }
        patch.Append("*** End Patch");
        var limited = await source.ExecuteAsync(
            fixture.ToolContext(sessionId, epoch: 1),
            Request("apply_patch", new { patchText = patch.ToString() }));

        Assert.Contains("20-document prompt limit", exception.Message, StringComparison.Ordinal);
        Assert.True(limited.IsError);
        Assert.Equal("files-scoped-instruction-path-limit", limited.ErrorCode);
        Assert.False(limited.RequiresPromptContextRefresh);
        Assert.DoesNotContain(fixture.Roots.SelectMany(root => Directory.EnumerateFiles(root)), path => Path.GetFileName(path).StartsWith("limited-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ObservedButUnacknowledgedInstructions_BlockMutationUntilExactReceipt()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "AGENTS.md"), "root policy");
        var source = fixture.CreateSource();
        var request = fixture.PromptRequest(epoch: 1);

        var omitted = ScopedBlocks(await source.ContributeContextAsync(request));
        var blocked = await source.ExecuteAsync(
            fixture.ToolContext(epoch: 1),
            Request("write", new { path = "blocked.txt", content = "blocked" }));
        await AcknowledgeAsync(source, request, omitted);
        var allowed = await source.ExecuteAsync(
            fixture.ToolContext(epoch: 1),
            Request("write", new { path = "allowed.txt", content = "allowed" }));

        AssertDeferred(blocked);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "blocked.txt")));
        Assert.False(allowed.IsError, allowed.Summary);
    }

    [Fact]
    public async Task CompoundAgentsPatch_IsRejectedBeforeAnyMutation()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "AGENTS.md"), "old policy");
        var source = fixture.CreateSource();
        await PresentAsync(source, fixture.PromptRequest(epoch: 1));
        var patch = """
            *** Begin Patch
            *** Update File: AGENTS.md
            @@
            -old policy
            +new policy
            *** Add File: governed.txt
            +changed
            *** End Patch
            """;

        var result = await source.ExecuteAsync(
            fixture.ToolContext(epoch: 1),
            Request("apply_patch", new { patchText = patch }));

        Assert.True(result.IsError);
        Assert.Equal("files-agents-compound-patch-rejected", result.ErrorCode);
        Assert.Equal("old policy", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "AGENTS.md")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "governed.txt")));
    }

    [Fact]
    public async Task CompoundAgentsPatch_RejectsSymlinkAliasBeforeAnyMutation()
    {
        using var fixture = await Fixture.CreateAsync();
        var agentsPath = Path.Combine(fixture.Root, "AGENTS.md");
        await File.WriteAllTextAsync(agentsPath, "old policy");
        CreateFileSymlinkOrSkip(Path.Combine(fixture.Root, "policy-link.md"), agentsPath);
        var source = fixture.CreateSource();
        await PresentAsync(source, fixture.PromptRequest(epoch: 1));
        var patch = """
            *** Begin Patch
            *** Update File: policy-link.md
            @@
            -old policy
            +new policy
            *** Add File: governed.txt
            +changed
            *** End Patch
            """;

        var result = await source.ExecuteAsync(
            fixture.ToolContext(epoch: 1),
            Request("apply_patch", new { patchText = patch }));

        Assert.True(result.IsError);
        Assert.Equal("files-agents-patch-classification-failed", result.ErrorCode);
        Assert.Equal("old policy", await File.ReadAllTextAsync(agentsPath));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "governed.txt")));
    }

    [Theory]
    [InlineData("grep")]
    [InlineData("glob")]
    public async Task RecursiveSearch_WithholdsMatchesFromUnclaimedNestedScopeUntilPresented(string toolId)
    {
        using var fixture = await Fixture.CreateAsync();
        var nested = Path.Combine(fixture.Root, "nested");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "AGENTS.md"), "root policy");
        await File.WriteAllTextAsync(Path.Combine(nested, "AGENTS.md"), "nested search policy");
        await File.WriteAllTextAsync(Path.Combine(nested, "secret-match.txt"), "SEARCH_SCOPE_SECRET");
        var source = fixture.CreateSource();
        var initial = await PresentAsync(source, fixture.PromptRequest(epoch: 1));
        var request = toolId == "grep"
            ? Request("grep", new { pattern = "SEARCH_SCOPE_SECRET", path = "." })
            : Request("glob", new { pattern = "**/secret-match.txt", path = "." });

        var withheld = await source.ExecuteAsync(fixture.ToolContext(epoch: 1), request);

        Assert.Single(initial);
        Assert.DoesNotContain(initial, block => block.Content.Contains("nested search policy", StringComparison.Ordinal));
        Assert.True(withheld.IsError);
        Assert.Equal("files-prompt-context-refresh-required", withheld.ErrorCode);
        Assert.True(withheld.RequiresPromptContextRefresh);
        Assert.Null(withheld.StructuredPayloadJson);
        Assert.DoesNotContain("SEARCH_SCOPE_SECRET", withheld.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-match.txt", withheld.Content, StringComparison.Ordinal);

        var refreshed = await PresentAsync(source, fixture.PromptRequest(epoch: 1));
        var rerun = await source.ExecuteAsync(fixture.ToolContext(epoch: 1), request);

        Assert.Contains(refreshed, block => block.Content.Contains("nested search policy", StringComparison.Ordinal));
        Assert.False(rerun.IsError, rerun.Content);
        Assert.Contains("secret-match.txt", rerun.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecursiveGrep_UsesUnambiguousMatchedPathForColonNamedNestedScope()
    {
        if (OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("Colon path segments are not valid Windows file names.");
        }

        using var fixture = await Fixture.CreateAsync();
        var nested = Path.Combine(fixture.Root, "branch:12:nested");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "AGENTS.md"), "root policy");
        await File.WriteAllTextAsync(Path.Combine(nested, "AGENTS.md"), "colon path policy");
        await File.WriteAllTextAsync(Path.Combine(nested, "secret.txt"), "COLON_PATH_SECRET");
        var source = fixture.CreateSource();
        await PresentAsync(source, fixture.PromptRequest(epoch: 1));
        var request = Request("grep", new { pattern = "COLON_PATH_SECRET", path = "." });

        var withheld = await source.ExecuteAsync(fixture.ToolContext(epoch: 1), request);

        Assert.True(withheld.IsError);
        Assert.Equal("files-prompt-context-refresh-required", withheld.ErrorCode);
        Assert.DoesNotContain("COLON_PATH_SECRET", withheld.Content, StringComparison.Ordinal);

        var refreshed = await PresentAsync(source, fixture.PromptRequest(epoch: 1));
        var rerun = await source.ExecuteAsync(fixture.ToolContext(epoch: 1), request);

        Assert.Contains(refreshed, block => block.Content.Contains("colon path policy", StringComparison.Ordinal));
        Assert.False(rerun.IsError, rerun.Content);
        Assert.Contains("branch:12:nested", rerun.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecursiveSearch_PreservesLiteralBackslashPathForGovernedDiscovery()
    {
        if (OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("Backslash is a native path separator on Windows.");
        }

        using var fixture = await Fixture.CreateAsync();
        var nested = Path.Combine(fixture.Root, "backslash\\dir");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "AGENTS.md"), "root policy");
        await File.WriteAllTextAsync(Path.Combine(nested, "AGENTS.md"), "literal backslash policy");
        await File.WriteAllTextAsync(Path.Combine(nested, "match.txt"), "BACKSLASH_SCOPE_SECRET");
        var source = fixture.CreateSource();
        await PresentAsync(source, fixture.PromptRequest(epoch: 1));
        var request = Request("glob", new { pattern = "**/*.txt", path = "." });

        var withheld = await source.ExecuteAsync(fixture.ToolContext(epoch: 1), request);

        AssertDeferred(withheld);
        Assert.DoesNotContain("BACKSLASH_SCOPE_SECRET", withheld.Content, StringComparison.Ordinal);

        var refreshed = await PresentAsync(source, fixture.PromptRequest(epoch: 1));
        var rerun = await source.ExecuteAsync(fixture.ToolContext(epoch: 1), request);

        Assert.Contains(refreshed, block => block.Content.Contains("literal backslash policy", StringComparison.Ordinal));
        Assert.False(rerun.IsError, rerun.Content);
        Assert.Contains("backslash\\dir", rerun.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchThroughContainedDirectorySymlink_IsRejectedWithoutDisclosingMatches()
    {
        using var fixture = await Fixture.CreateAsync();
        var target = Path.Combine(fixture.Root, "target");
        var alias = Path.Combine(fixture.Root, "alias");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "AGENTS.md"), "root policy");
        await File.WriteAllTextAsync(Path.Combine(target, "AGENTS.md"), "canonical target policy");
        await File.WriteAllTextAsync(Path.Combine(target, "match.txt"), "match");
        CreateDirectorySymlinkOrSkip(alias, target);
        var source = fixture.CreateSource();
        await PresentAsync(source, fixture.PromptRequest(epoch: 1));
        var request = Request("glob", new { pattern = "*.txt", path = alias });

        var result = await source.ExecuteAsync(fixture.ToolContext(epoch: 1), request);

        Assert.True(result.IsError);
        Assert.Equal("file-search-path-unresolvable", result.ErrorCode);
        Assert.False(result.RequiresPromptContextRefresh);
        Assert.Null(result.StructuredPayloadJson);
        Assert.DoesNotContain("match.txt", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("canonical target policy", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedPostReadDiscovery_WithholdsContentAndPersistsRecoveryAcrossSourceRestart()
    {
        using var fixture = await Fixture.CreateAsync();
        var nested = Path.Combine(fixture.Root, "nested");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "AGENTS.md"), "root policy");
        await File.WriteAllTextAsync(Path.Combine(nested, "AGENTS.md"), new string('x', 12_001));
        await File.WriteAllTextAsync(Path.Combine(nested, "secret.txt"), "READ_SCOPE_SECRET");
        var source = fixture.CreateSource();
        await PresentAsync(source, fixture.PromptRequest(epoch: 1));

        var withheld = await source.ExecuteAsync(
            fixture.ToolContext(epoch: 1),
            Request("read", new { path = "nested/secret.txt" }));

        Assert.True(withheld.IsError);
        Assert.Equal("files-scoped-instruction-discovery-failed", withheld.ErrorCode);
        Assert.True(withheld.RequiresPromptContextRefresh);
        Assert.Null(withheld.StructuredPayloadJson);
        Assert.DoesNotContain("READ_SCOPE_SECRET", withheld.Content, StringComparison.Ordinal);
        Assert.Contains("pendingAccessProbes", await File.ReadAllTextAsync(fixture.ClaimPath(fixture.SessionId)), StringComparison.OrdinalIgnoreCase);

        await File.WriteAllTextAsync(Path.Combine(nested, "AGENTS.md"), "recovered nested policy");
        var restarted = fixture.CreateSource();
        var recovered = await PresentAsync(restarted, fixture.PromptRequest(epoch: 1));
        var rerun = await restarted.ExecuteAsync(
            fixture.ToolContext(epoch: 1),
            Request("read", new { path = "nested/secret.txt" }));

        Assert.Contains(recovered, block => block.Content.Contains("recovered nested policy", StringComparison.Ordinal));
        Assert.False(rerun.IsError, rerun.Content);
        Assert.Contains("READ_SCOPE_SECRET", rerun.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncompletePostReadDiscovery_WithholdsContentUntilPersistedProbeRecovers()
    {
        using var fixture = await Fixture.CreateAsync();
        var target = new IncompleteDiscoveryExecutionTarget(fixture.Root);
        var catalog = new RegressionTestExtensionCatalog();
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);
        var source = new FilesToolSource(catalog, fixture.PackageContext);
        await PresentAsync(source, fixture.PromptRequest(epoch: 1));
        target.ReturnIncompleteDiscovery = true;

        var withheld = await source.ExecuteAsync(
            fixture.ToolContext(epoch: 1),
            Request("read", new { path = "nested/secret.txt" }));

        Assert.True(withheld.IsError);
        Assert.Equal("files-scoped-instruction-discovery-failed", withheld.ErrorCode);
        Assert.True(withheld.RequiresPromptContextRefresh);
        Assert.DoesNotContain("INCOMPLETE_DISCOVERY_SECRET", withheld.Content, StringComparison.Ordinal);

        target.ReturnIncompleteDiscovery = false;
        var restarted = new FilesToolSource(catalog, fixture.PackageContext);
        await PresentAsync(restarted, fixture.PromptRequest(epoch: 1));
        var rerun = await restarted.ExecuteAsync(
            fixture.ToolContext(epoch: 1),
            Request("read", new { path = "nested/secret.txt" }));

        Assert.False(rerun.IsError, rerun.Content);
        Assert.Contains("INCOMPLETE_DISCOVERY_SECRET", rerun.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecursiveSearch_OverMatchedPathBoundSuppressesAllResults()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "AGENTS.md"), "root policy");
        for (var index = 0; index < 65; index++)
        {
            var directory = Path.Combine(fixture.Root, $"nested-{index:D2}");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "match.txt"), "bounded");
        }
        var source = fixture.CreateSource();
        await PresentAsync(source, fixture.PromptRequest(epoch: 1));

        var result = await source.ExecuteAsync(
            fixture.ToolContext(epoch: 1),
            Request("glob", new { pattern = "**/match.txt", path = "." }));

        Assert.True(result.IsError);
        Assert.Equal("files-scoped-instruction-result-path-limit", result.ErrorCode);
        Assert.Null(result.StructuredPayloadJson);
        Assert.DoesNotContain("nested-00", result.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("edit")]
    [InlineData("patch")]
    [InlineData("delete")]
    public async Task LocalOutsideApproval_RetargetedAliasUsesRetainedHandleWithoutTouchingAliasTarget(string operation)
    {
        using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "AGENTS.md"), "root policy");
        var outside = Path.Combine(fixture.ScopeRoot, "approved-retarget", operation);
        Directory.CreateDirectory(outside);
        var second = Path.Combine(outside, "second.txt");
        await File.WriteAllTextAsync(second, "RETARGETED_TOOL_SECRET");
        var link = Path.Combine(outside, $"{operation}-approved.txt");
        await File.WriteAllTextAsync(link, "approved content");
        var context = await fixture.ApprovedToolContextAsync(
            fixture.SessionId,
            epoch: 1,
            link,
            operation == "read" ? "files.read" : "files.mutate",
            operation switch
            {
                "edit" => 2,
                "patch" or "delete" => 8,
                _ => 1,
            });
        await PresentAsync(fixture.CreateSource(), fixture.PromptRequest(epoch: 1));
        File.Delete(link);
        CreateFileSymlinkOrSkip(link, second);
        var source = fixture.CreateSource();
        var request = operation switch
        {
            "read" => Request("read", new { path = link }),
            "write" => Request("write", new { path = link, content = "replacement" }),
            "edit" => Request("edit", new { path = link, oldString = "RETARGETED_TOOL_SECRET", newString = "replacement" }),
            "patch" => Request("apply_patch", new
            {
                patchText = $"*** Begin Patch\n*** Update File: {link}\n@@\n-RETARGETED_TOOL_SECRET\n+replacement\n*** End Patch",
            }),
            "delete" => Request("apply_patch", new
            {
                patchText = $"*** Begin Patch\n*** Delete File: {link}\n*** End Patch",
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        var result = await source.ExecuteAsync(context, request);

        if (operation == "read")
        {
            Assert.False(result.IsError, result.Content);
            Assert.Contains("approved content", result.Content, StringComparison.Ordinal);
        }
        else
        {
            Assert.True(result.IsError);
        }
        Assert.DoesNotContain("RETARGETED_TOOL_SECRET", result.Content, StringComparison.Ordinal);
        Assert.True(File.Exists(second));
        Assert.Equal("RETARGETED_TOOL_SECRET", await File.ReadAllTextAsync(second));
    }

    [Fact]
    public async Task ShellExecution_RefreshesPreviouslyKnownClaimsWithoutClaimingCommandPaths()
    {
        using var fixture = await Fixture.CreateAsync();
        var nested = Path.Combine(fixture.Root, "nested");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "AGENTS.md"), "root policy");
        await File.WriteAllTextAsync(Path.Combine(nested, "AGENTS.md"), "nested v1");
        await File.WriteAllTextAsync(Path.Combine(nested, "file.txt"), "content");
        var files = fixture.CreateSource();
        await PresentAsync(files, fixture.PromptRequest(epoch: 1));
        await files.ExecuteAsync(
            fixture.ToolContext(epoch: 1),
            Request("read", new { path = "nested/file.txt" }));
        await PresentAsync(files, fixture.PromptRequest(epoch: 1));
        await File.WriteAllTextAsync(Path.Combine(nested, "AGENTS.md"), "nested v2");

        var shell = await fixture.CreateShellSourceAsync();
        var shellResult = await shell.ExecuteAsync(
            fixture.ToolContext(epoch: 1),
            Request("shell", new { command = "true" }));
        var refreshed = await PresentAsync(files, fixture.PromptRequest(epoch: 1));

        Assert.False(shellResult.IsError, shellResult.Summary);
        Assert.True(shellResult.RequiresPromptContextRefresh);
        Assert.Contains(refreshed, block => block.Content.Contains("nested v2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClaimStore_RejectsOversizedSerializedReplacement()
    {
        using var fixture = await Fixture.CreateAsync();
        var store = new ScopedInstructionClaimStore(fixture.PackageContext);
        var sessionId = Guid.NewGuid();
        var initial = new ScopedInstructionClaimState(
            ScopedInstructionClaimStore.CurrentVersion,
            sessionId,
            "workspace",
            "binding",
            "local",
            "local",
            new string('a', 64),
            new string('b', 64),
            1,
            []);
        await store.SaveAsync(initial, CancellationToken.None);
        var claims = Enumerable.Range(0, 512)
            .Select(claimIndex => new ScopedInstructionDirectoryClaim(
                $"/root/{claimIndex}",
                "/root",
                Enumerable.Range(0, 8)
                    .Select(documentIndex => new ScopedInstructionDocumentClaim(
                        $"/root/{claimIndex}/{documentIndex}/" + new string('p', 700),
                        new string('c', 64),
                        null))
                    .ToArray()))
            .ToArray();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.SaveAsync(initial with { Claims = claims }, CancellationToken.None));

        Assert.Contains("persistence limit", exception.Message, StringComparison.Ordinal);
        var retained = Assert.IsType<ScopedInstructionClaimState>(
            (await store.LoadAsync(sessionId, CancellationToken.None)).State);
        Assert.Equal(initial.SessionId, retained.SessionId);
        Assert.Empty(retained.Claims);
    }

    [Fact]
    public async Task ClaimStore_RejectsDuplicateAndOverCapacityClaims()
    {
        using var fixture = await Fixture.CreateAsync();
        var store = new ScopedInstructionClaimStore(fixture.PackageContext);
        var state = new ScopedInstructionClaimState(
            ScopedInstructionClaimStore.CurrentVersion,
            Guid.NewGuid(),
            "workspace",
            "binding",
            "local",
            "local",
            new string('a', 64),
            new string('b', 64),
            1,
            []);
        var duplicate = new ScopedInstructionDirectoryClaim("/root", "/root", []);
        var overCapacity = Enumerable.Range(0, ScopedInstructionClaimStore.MaxRetainedClaims + 1)
            .Select(index => new ScopedInstructionDirectoryClaim($"/root/{index}", "/root", []))
            .ToArray();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.SaveAsync(state with { Claims = [duplicate, duplicate] }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.SaveAsync(state with { Claims = overCapacity }, CancellationToken.None));
    }

    private static AgentToolRequest Request(string toolId, object arguments)
        => new(toolId, JsonSerializer.Serialize(arguments));

    private static IReadOnlyList<AgentPromptContextBlock> ScopedBlocks(AgentPromptContextContribution? contribution)
        => contribution?.Blocks
               .Where(block => block.Usage == AgentPromptContextUsage.ScopedInstruction)
               .ToArray()
           ?? [];

    private static async Task<IReadOnlyList<AgentPromptContextBlock>> PresentAsync(
        FilesToolSource source,
        AgentPromptContextRequest request)
    {
        var blocks = ScopedBlocks(await source.ContributeContextAsync(request));
        await AcknowledgeAsync(source, request, blocks);
        return blocks;
    }

    private static async Task AcknowledgeAsync(
        FilesToolSource source,
        AgentPromptContextRequest request,
        IReadOnlyList<AgentPromptContextBlock> blocks)
    {
        var receiptBlocks = blocks.Select(block =>
        {
            var scope = Assert.IsType<AgentPromptContextScope>(block.Scope);
            return new AgentPromptContextReceiptBlock(
                "test-host",
                scope.ContextIdentity,
                scope.DocumentPath,
                scope.ContentHash);
        }).ToArray();
        await source.AcknowledgePromptContextAsync(new AgentPromptContextReceipt(
            request.Session.SessionId,
            request.Run.RunId,
            request.TranscriptEpoch,
            receiptBlocks));
    }

    private static void AssertDeferred(AgentToolResult result)
    {
        Assert.True(result.IsError);
        Assert.Equal("files-prompt-context-refresh-required", result.ErrorCode);
        Assert.True(result.RequiresPromptContextRefresh);
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly AgentToolDescriptor FilesTool = new(
            "read",
            "Read",
            "Read files.",
            SourceId: "files");

        private readonly RegressionTestPackageScope _scope;
        private readonly LocalExecutionWorkspaceConfigService _configService;
        private readonly RegressionTestExtensionCatalog _catalog;
        private readonly LocalExecutionTarget _target;

        private Fixture(
            RegressionTestPackageScope scope,
            IReadOnlyList<string> roots,
            AgentWorkspaceRecord workspace,
            AgentWorkspaceBindingRecord binding,
            LocalExecutionWorkspaceConfigService configService,
            RegressionTestExtensionCatalog catalog,
            LocalExecutionTarget target)
        {
            _scope = scope;
            Roots = roots;
            Workspace = workspace;
            Binding = binding;
            _configService = configService;
            _catalog = catalog;
            _target = target;
        }

        public string ScopeRoot => _scope.RootPath;

        public string Root => Roots[0];

        public IReadOnlyList<string> Roots { get; }

        public Guid SessionId { get; } = Guid.NewGuid();

        public AgentWorkspaceRecord Workspace { get; }

        public AgentWorkspaceBindingRecord Binding { get; }

        public Sunder.Sdk.Abstractions.IPackageContext PackageContext => _scope.Context;

        public static async Task<Fixture> CreateAsync(int rootCount = 1)
        {
            var scope = RegressionTestPackageScope.Create();
            try
            {
                var roots = Enumerable.Range(0, rootCount)
                    .Select(index => Path.Combine(scope.RootPath, $"workspace-{index}"))
                    .ToArray();
                foreach (var root in roots)
                {
                    Directory.CreateDirectory(root);
                }

                var now = DateTimeOffset.UtcNow;
                const string workspaceId = "scoped-instruction-workspace";
                var workspace = new AgentWorkspaceRecord(
                    workspaceId,
                    "Scoped Instructions",
                    null,
                    now,
                    now,
                    roots.Select((root, index) => new AgentWorkspacePathRecord(
                            $"path-{index}",
                            workspaceId,
                            root,
                            IsDefault: index == 0,
                            SortOrder: index,
                            now,
                            now))
                        .ToArray());
                var binding = CreateBinding(workspaceId, "binding");
                var configService = new LocalExecutionWorkspaceConfigService(scope.Context);
                await configService.SaveConfigAsync(binding.BindingId, new LocalExecutionWorkspaceConfig(null, []));
                var catalog = new RegressionTestExtensionCatalog();
                var target = new LocalExecutionTarget(
                    scope.Context,
                    configService,
                    new LocalShellCatalogService(scope.Context));
                catalog.AddExtension(
                    PackageExtensionPoints.ExecutionTargets,
                    target);
                return new Fixture(scope, roots, workspace, binding, configService, catalog, target);
            }
            catch
            {
                scope.Dispose();
                throw;
            }
        }

        public FilesToolSource CreateSource() => new(_catalog, _scope.Context);

        public async Task<ShellToolSource> CreateShellSourceAsync()
        {
            await _configService.SaveConfigAsync(Binding.BindingId, new LocalExecutionWorkspaceConfig(null, []));
            return new ShellToolSource(_catalog);
        }

        public async Task<AgentWorkspaceBindingRecord> CreateBindingAsync(string bindingId)
        {
            var binding = CreateBinding(Workspace.WorkspaceId, bindingId);
            await _configService.SaveConfigAsync(binding.BindingId, new LocalExecutionWorkspaceConfig(null, []));
            return binding;
        }

        public AgentPromptContextRequest PromptRequest(long epoch)
            => PromptRequest(SessionId, epoch);

        public AgentPromptContextRequest PromptRequest(Guid sessionId, long epoch, AgentWorkspaceBindingRecord? binding = null)
        {
            var now = DateTimeOffset.UtcNow;
            var session = new AgentSessionContextRecord(
                sessionId,
                "profile",
                "Test Profile",
                "Test Session",
                AgentSessionState.Active,
                null);
            var run = new AgentRunContextRecord(Guid.NewGuid(), 1, AgentRunStatus.Running, IsInterrupted: false, now);
            return new AgentPromptContextRequest(
                session,
                run,
                new AgentTurnContextRecord(session, run, "Test", null),
                [],
                [],
                new AgentPromptContextPlan("general", "Test"))
            {
                Workspace = Workspace,
                ExecutionBinding = binding ?? Binding,
                AvailableTools = [FilesTool],
                TranscriptEpoch = epoch,
            };
        }

        public AgentToolExecutionContext ToolContext(
            long epoch,
            AgentWorkspaceBindingRecord? binding = null,
            bool allowOutsideConfiguredScope = false)
            => ToolContext(SessionId, epoch, binding, allowOutsideConfiguredScope);

        public AgentToolExecutionContext ToolContext(
            Guid sessionId,
            long epoch,
            AgentWorkspaceBindingRecord? binding = null,
            bool allowOutsideConfiguredScope = false)
            => new(
                sessionId,
                Workspace: Workspace,
                ExecutionBinding: binding ?? Binding,
                AllowOutsideConfiguredScope: allowOutsideConfiguredScope)
            {
                TranscriptEpoch = epoch,
            };

        public async Task<AgentToolExecutionContext> ApprovedToolContextAsync(
            Guid sessionId,
            long epoch,
            string path,
            string actionId,
            int authorityUseCount = 1)
        {
            var runId = Guid.NewGuid();
            var toolCallId = Guid.NewGuid().ToString("N");
            var operation = new AgentResourceOperationContext(
                runId,
                1,
                toolCallId,
                actionId,
                ResourceIndex: 0,
                "workspace-generation",
                "binding-generation",
                "sunder.package.agent.tools.files",
                "sunder.package.agent.execution.local",
                Guid.NewGuid().ToString("N"),
                authorityUseCount,
                CanIssueOutsideAuthority: true);
            var context = ToolContext(sessionId, epoch, allowOutsideConfiguredScope: true) with
            {
                RunId = runId,
                RunRevision = 1,
                ToolCallId = toolCallId,
            };
            var target = _catalog.GetExtensions(PackageExtensionPoints.ExecutionTargets).Single();
            var resource = await target.ResolveFileResourceAsync(
                new AgentExecutionTargetContext(
                    sessionId,
                    null,
                    Workspace,
                    Binding,
                    AllowOutsideConfiguredScope: true)
                {
                    ResourceOperation = operation,
                },
                path);
            return context with
            {
                ApprovedResourceReferences = new[]
                    {
                        resource.CanonicalReference,
                        resource.DeleteCanonicalReference,
                    }
                    .Where(reference => !string.IsNullOrWhiteSpace(reference))
                    .Select(reference => reference!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                ApprovedResourceClaims = new[]
                    {
                        resource.ResourceClaim,
                        resource.DeleteResourceClaim,
                    }
                    .Where(claim => claim is not null)
                    .Select(claim => claim!)
                    .Distinct()
                    .ToArray(),
                ApprovedResourceCapabilities = resource.AuthorityReferences
                    .Concat(resource.DeleteAuthorityReferences)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                ResourceOperation = operation with { CanIssueOutsideAuthority = false },
            };
        }

        public string ClaimPath(Guid sessionId)
            => _scope.Context.Storage.RoleLocalWorkspace.GetLocalPath(
                $"files/scoped-instruction-claims/{sessionId:N}.json");

        public bool IsCurrent(
            string capability,
            AgentResourceClaim claim,
            AgentResourceOperationContext operation)
            => _target.ResourceReferences.IsCurrent(capability, claim, operation);

        public void Dispose()
        {
            _target.Dispose();
            _scope.Dispose();
        }

        private static AgentWorkspaceBindingRecord CreateBinding(string workspaceId, string bindingId)
        {
            var now = DateTimeOffset.UtcNow;
            return new AgentWorkspaceBindingRecord(
                bindingId,
                workspaceId,
                PackageExtensionPoints.ExecutionTargets.Id,
                "local",
                "primary-execution-target",
                IsEnabled: true,
                SortOrder: 0,
                now,
                now);
        }
    }

    private sealed class IncompleteDiscoveryExecutionTarget(string root)
        : IAgentScopedInstructionDiscoveryTarget, IAgentExecutionScopeProvider
    {
        public bool ReturnIncompleteDiscovery { get; set; }

        public AgentExecutionTargetDescriptor Descriptor { get; } = new(
            "local",
            "local",
            "Incomplete discovery target",
            null,
            SupportsShell: false,
            SupportsFiles: true);

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionTargetReadiness(
                "local",
                "local",
                AgentExecutionTargetReadinessStatus.Ready,
                "Ready."));

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentExecutionScopeDescriptor> GetExecutionScopeAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionScopeDescriptor(
                "Incomplete discovery target",
                [root],
                root));

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(
            AgentExecutionTargetContext context,
            string path,
            CancellationToken cancellationToken = default)
        {
            var resolved = Resolve(path);
            return ValueTask.FromResult(new AgentResolvedResource(
                "file",
                resolved,
                resolved,
                AgentPermissionBoundaryIds.ConfiguredScope,
                Exists: true));
        }

        public ValueTask<AgentScopedInstructionDiscoveryResult> DiscoverScopedInstructionsAsync(
            AgentExecutionTargetContext context,
            AgentScopedInstructionDiscoveryRequest request,
            CancellationToken cancellationToken = default)
        {
            var scopes = request.Probes.Select(probe =>
            {
                var resolved = Resolve(probe.Path);
                var directory = probe.IsDirectory
                    ? resolved
                    : Path.GetDirectoryName(resolved) ?? root;
                return new AgentScopedInstructionScope(
                    probe.Path,
                    directory,
                    root,
                    []);
            }).ToArray();
            return ValueTask.FromResult(new AgentScopedInstructionDiscoveryResult(
                new string('a', 64),
                new string('b', 64),
                scopes)
            {
                ProcessedProbeCount = ReturnIncompleteDiscovery
                    ? Math.Max(0, request.Probes.Count - 1)
                    : request.Probes.Count,
            });
        }

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(
            AgentExecutionTargetContext context,
            AgentShellCommandRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileReadResult> ReadFileAsync(
            AgentExecutionTargetContext context,
            AgentFileReadRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentFileReadResult(
                Resolve(request.Path),
                "INCOMPLETE_DISCOVERY_SECRET"));

        public ValueTask<AgentFileMutationResult> WriteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileWriteRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileDeleteRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        private string Resolve(string path)
            => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
    }

    private static void CreateFileSymlinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            throw SkipException.ForSkip($"Symbolic links are unavailable: {ex.Message}");
        }
    }

    private static void CreateDirectorySymlinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            throw SkipException.ForSkip($"Directory symbolic links are unavailable: {ex.Message}");
        }
    }
}
