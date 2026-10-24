extern alias LocalWorker;

using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using LocalWorker::Sunder.Package.Agent.Execution.Local;
using Xunit;
using AgentFileDeleteRequest = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileDeleteRequest;
using AgentFileMutationResult = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileMutationResult;
using AgentFileReadErrorCodes = Sunder.Package.Agent.Contracts.Models.AgentFileReadErrorCodes;
using AgentFileReadRequest = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileReadRequest;
using AgentFileSearchErrorCodes = Sunder.Package.Agent.Contracts.Models.AgentFileSearchErrorCodes;
using AgentFileSearchKind = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileSearchKind;
using AgentFileSearchRequest = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileSearchRequest;
using AgentFileWriteRequest = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileWriteRequest;
using AgentResourceOperationContext = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentResourceOperationContext;
using AgentResolvedResource = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentResolvedResource;
using AgentScopedInstructionDiscoveryRequest = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentScopedInstructionDiscoveryRequest;
using AgentScopedInstructionProbe = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentScopedInstructionProbe;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class LocalSecureFileSystemRaceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        OperatingSystem.IsMacOS() ? "/private" + Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) : Path.GetTempPath(),
        "sunder-secure-races",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ResourceCapability_IsOpaqueInvocationBoundAndSingleUse()
    {
        var workspace = Path.Combine(_root, "workspace");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, "target");
        File.WriteAllText(target, "content");
        using var approved = LocalResourceAuthorityTestContext.Approve(
            new LocalExecutionRuntimeConfig([workspace], workspace),
            target,
            "files.read");
        var reference = Assert.Single(approved.Context.ApprovedResourceCapabilities);
        var claim = Assert.Single(approved.Context.ApprovedResourceClaims);
        var operation = Assert.IsType<AgentResourceOperationContext>(approved.Context.ResourceOperation);

        Assert.StartsWith("local-resource-authority-v4:", reference, StringComparison.Ordinal);
        Assert.DoesNotContain(target, reference, StringComparison.Ordinal);
        Assert.True(approved.ResourceReferences.IsCurrent(reference, claim, operation));
        Assert.True(approved.ResourceReferences.TryRedeem(reference, claim, operation, out var authority));
        Assert.NotNull(authority);
        Assert.False(approved.ResourceReferences.IsCurrent(reference, claim, operation));
        authority.Dispose();
        Assert.Throws<LocalSecureApprovalChangedException>(() =>
            authority.OpenSession(createParents: false, hooks: null, CancellationToken.None));
        Assert.False(approved.ResourceReferences.TryRedeem(reference, claim, operation, out _));
        Assert.False(approved.ResourceReferences.TryRedeem("local-resource-v3:legacy", claim, operation, out _));
    }

    [Fact]
    public void ApprovalLease_IsStableUntilUseAndFailsAfterExpiryOrProcessRestart()
    {
        var now = DateTimeOffset.UtcNow;
        using var leases = new HostApprovalLeaseStore<string>(TimeSpan.FromMinutes(1), () => now);
        var token = leases.Issue("resource", "binding");

        Assert.NotEqual(token, leases.Issue("resource", "binding"));
        Assert.False(leases.TryRedeem(token, value => value == "other", out _));
        Assert.True(leases.TryRedeem(token, value => value == "binding", out var redeemed));
        Assert.Equal("binding", redeemed);
        Assert.False(leases.TryRedeem(token, static _ => true, out _));

        var expiring = leases.Issue("expiring", "value");
        now += TimeSpan.FromMinutes(2);
        Assert.False(leases.TryRedeem(expiring, static _ => true, out _));

        var persisted = leases.Issue("persisted", "value");
        using var restartedStore = new HostApprovalLeaseStore<string>();
        Assert.False(restartedStore.TryRedeem(persisted, static _ => true, out _));
    }

    [Fact]
    public void OutsideResourceCapability_ExpiresWithinItsActivationStore()
    {
        var workspace = Path.Combine(_root, "expiry-workspace");
        var outside = Path.Combine(_root, "expiry-outside");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, "target.txt");
        File.WriteAllText(target, "content");
        var now = DateTimeOffset.UtcNow;
        using var resourceReferences = new LocalResourceReference(TimeSpan.FromMinutes(1), () => now);
        using var approved = LocalResourceAuthorityTestContext.Approve(
            new LocalExecutionRuntimeConfig([workspace], workspace),
            target,
            "files.read",
            resourceReferences: resourceReferences);
        var reference = Assert.Single(approved.Context.ApprovedResourceCapabilities);
        var claim = Assert.Single(approved.Context.ApprovedResourceClaims);
        var operation = Assert.IsType<AgentResourceOperationContext>(approved.Context.ResourceOperation);

        Assert.True(resourceReferences.IsCurrent(reference, claim, operation));

        now += TimeSpan.FromMinutes(2);

        Assert.False(resourceReferences.IsCurrent(reference, claim, operation));
        Assert.False(resourceReferences.TryRedeem(reference, claim, operation, out _));
    }

    [Fact]
    public async Task ReadAndList_UsePinnedParentAfterPathReplacement()
    {
        var fixture = CreateFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Pinned, "safe.txt"), "safe");
        Directory.CreateDirectory(Path.Combine(fixture.Pinned, "dir"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Pinned, "dir", "inside.txt"), "inside");
        await File.WriteAllTextAsync(Path.Combine(fixture.Outside, "safe.txt"), "OUTSIDE");
        Directory.CreateDirectory(Path.Combine(fixture.Outside, "dir"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Outside, "dir", "sentinel.txt"), "OUTSIDE");

        var readHook = fixture.CreateSwapHook(LocalSecureFileSystemCheckpoint.BeforeOpen, "safe.txt");
        var read = await LocalFileSystemExecutor.ReadFileAsync(
            fixture.Config,
            new AgentFileReadRequest { Path = "pinned/safe.txt" },
            false,
            CancellationToken.None,
            hooks: readHook);
        readHook.Restore();

        var listHook = fixture.CreateSwapHook(LocalSecureFileSystemCheckpoint.BeforeOpen, "dir");
        var listing = await LocalFileSystemExecutor.ReadFileAsync(
            fixture.Config,
            new AgentFileReadRequest { Path = "pinned/dir" },
            false,
            CancellationToken.None,
            hooks: listHook);
        listHook.Restore();

        Assert.False(read.IsError, read.ErrorMessage);
        Assert.Equal("safe", read.Content);
        Assert.False(listing.IsError, listing.ErrorMessage);
        Assert.Contains("inside.txt", listing.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("sentinel.txt", listing.Content, StringComparison.Ordinal);
        Assert.Equal("OUTSIDE", await File.ReadAllTextAsync(Path.Combine(fixture.Outside, "safe.txt")));
        Assert.Equal("OUTSIDE", await File.ReadAllTextAsync(Path.Combine(fixture.Outside, "dir", "sentinel.txt")));
    }

    [PlatformTheory(TestPlatform.Linux, "Anonymous file publication fallback is Linux-specific.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Write_PublishesOnlyInPinnedParent(bool overwrite)
    {
        var fixture = CreateFixture();
        var targetName = overwrite ? "existing.txt" : "created.txt";
        if (overwrite)
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Pinned, targetName), "old");
        }
        await File.WriteAllTextAsync(Path.Combine(fixture.Outside, targetName), "OUTSIDE");
        var hook = fixture.CreateSwapHook(LocalSecureFileSystemCheckpoint.BeforePublish, targetName);

        var result = await LocalFileSystemExecutor.WriteFileAsync(
            fixture.Config,
            new AgentFileWriteRequest { Path = Path.Combine("pinned", targetName), Content = "new", Overwrite = overwrite },
            false,
            CancellationToken.None,
            hooks: hook);
        hook.Restore();

        Assert.False(result.IsError, result.Summary);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(fixture.Pinned, targetName)));
        Assert.Equal("OUTSIDE", await File.ReadAllTextAsync(Path.Combine(fixture.Outside, targetName)));
        Assert.Empty(Directory.EnumerateFiles(fixture.Pinned, $".{targetName}.sunder-*.tmp"));
    }

    [PlatformTheory(TestPlatform.Linux, "Anonymous publication through /proc/self/fd is Linux-specific.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinuxWrite_AtEmptyPathEperm_UsesProcFallbackWithoutTemporaryLeak(bool overwrite)
    {
        var fixture = CreateFixture();
        var targetName = overwrite ? "proc-existing.txt" : "proc-created.txt";
        var target = Path.Combine(fixture.Workspace, targetName);
        if (overwrite)
        {
            await File.WriteAllTextAsync(target, "old");
        }
        var methods = new List<LocalUnixAnonymousPublishMethod>();
        AgentFileMutationResult result;
        using (LocalUnixNative.OverrideAnonymousPublishErrors(method =>
               {
                   methods.Add(method);
                   return method == LocalUnixAnonymousPublishMethod.EmptyPath ? 1 : null;
               }))
        {
            result = await LocalFileSystemExecutor.WriteFileAsync(
                fixture.Config,
                new AgentFileWriteRequest { Path = targetName, Content = "new", Overwrite = overwrite },
                false,
                CancellationToken.None);
        }

        Assert.False(result.IsError, result.Summary);
        Assert.Equal("new", await File.ReadAllTextAsync(target));
        Assert.Equal(
            [LocalUnixAnonymousPublishMethod.EmptyPath, LocalUnixAnonymousPublishMethod.ProcSelfFd],
            methods);
        AssertNoTemporaryEntries(fixture.Workspace);
    }

    [PlatformTheory(TestPlatform.Linux, "Named fallback publication is Linux-specific.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinuxWrite_WhenAnonymousLinksAreUnavailable_UsesNamedFallbackWithoutTemporaryLeak(bool overwrite)
    {
        var fixture = CreateFixture();
        var target = Path.Combine(fixture.Workspace, "named-fallback.txt");
        if (overwrite)
        {
            await File.WriteAllTextAsync(target, "old");
        }
        var methods = new List<LocalUnixAnonymousPublishMethod>();
        AgentFileMutationResult result;
        using (LocalUnixNative.OverrideAnonymousPublishErrors(method =>
               {
                   methods.Add(method);
                   return method == LocalUnixAnonymousPublishMethod.EmptyPath ? 1 : 2;
               }))
        {
            result = await LocalFileSystemExecutor.WriteFileAsync(
                fixture.Config,
                new AgentFileWriteRequest { Path = "named-fallback.txt", Content = "content", Overwrite = overwrite },
                false,
                CancellationToken.None);
        }

        Assert.False(result.IsError, result.Summary);
        Assert.Equal("content", await File.ReadAllTextAsync(target));
        Assert.Equal(
            [LocalUnixAnonymousPublishMethod.EmptyPath, LocalUnixAnonymousPublishMethod.ProcSelfFd],
            methods);
        AssertNoTemporaryEntries(fixture.Workspace);
    }

    [PlatformFact(
        TestPlatform.Linux,
        TestFileSystemCapability.FileSymbolicLinks,
        "Named fallback substitution requires Linux file symbolic links.")]
    public async Task LinuxNamedFallback_RejectsTemporaryEntrySubstitutionAndRestoresOriginal()
    {
        var fixture = CreateFixture();
        var target = Path.Combine(fixture.Workspace, "named-attack.txt");
        var outside = Path.Combine(fixture.Outside, "outside.txt");
        await File.WriteAllTextAsync(target, "original");
        await File.WriteAllTextAsync(outside, "OUTSIDE");
        var hook = new NamedTemporaryEntrySwapHook(target, outside, fixture.Outside);
        AgentFileMutationResult result;
        try
        {
            using (LocalUnixNative.OverrideAnonymousPublishErrors(method =>
                       method == LocalUnixAnonymousPublishMethod.EmptyPath ? 1 : 2))
            {
                result = await LocalFileSystemExecutor.WriteFileAsync(
                    fixture.Config,
                    new AgentFileWriteRequest { Path = "named-attack.txt", Content = "replacement", Overwrite = true },
                    false,
                    CancellationToken.None,
                    hooks: hook);
            }
        }
        finally
        {
            hook.Restore();
        }

        Assert.True(hook.Invoked);
        Assert.True(result.IsError);
        Assert.Equal(FileOperation.StrictMutationRecoveryRequiredErrorCode, result.ErrorCode);
        Assert.Equal("original", await File.ReadAllTextAsync(target));
        Assert.Equal("OUTSIDE", await File.ReadAllTextAsync(outside));
        AssertNoTemporaryEntries(fixture.Workspace);
    }

    [PlatformFact(TestPlatform.Linux, "Named fallback no-replace behavior is Linux-specific.")]
    public async Task LinuxNamedFallback_TargetCreationRaceRemainsNoReplaceAndCleansTemporaryEntry()
    {
        var fixture = CreateFixture();
        var target = Path.Combine(fixture.Workspace, "named-race.txt");
        var hook = new CreateAtFinalPublishHook(target, "ATTACKER");
        AgentFileMutationResult result;
        using (LocalUnixNative.OverrideAnonymousPublishErrors(method =>
                   method == LocalUnixAnonymousPublishMethod.EmptyPath ? 1 : 2))
        {
            result = await LocalFileSystemExecutor.WriteFileAsync(
                fixture.Config,
                new AgentFileWriteRequest { Path = "named-race.txt", Content = "new", Overwrite = false },
                false,
                CancellationToken.None,
                hooks: hook);
        }

        Assert.True(hook.Invoked);
        Assert.True(result.IsError);
        Assert.Equal(FileOperation.FileExistsErrorCode, result.ErrorCode);
        Assert.Equal("ATTACKER", await File.ReadAllTextAsync(target));
        AssertNoTemporaryEntries(fixture.Workspace);
    }

    [PlatformFact(
        TestPlatform.MacOS,
        TestFileSystemCapability.FileSymbolicLinks,
        "Named temporary substitution requires macOS file symbolic links.")]
    public async Task Write_RejectsTemporaryNameSubstitutionBeforePublish()
    {
        var fixture = CreateFixture();
        var target = Path.Combine(fixture.Pinned, "target.txt");
        var outside = Path.Combine(fixture.Outside, "outside.txt");
        await File.WriteAllTextAsync(target, "old");
        await File.WriteAllTextAsync(outside, "OUTSIDE");
        var hook = new TemporaryEntrySwapHook(target, outside);
        try
        {
            var result = await LocalFileSystemExecutor.WriteFileAsync(
                fixture.Config,
                new AgentFileWriteRequest { Path = "pinned/target.txt", Content = "new", Overwrite = true },
                false,
                CancellationToken.None,
                hooks: hook);

            Assert.True(result.IsError);
            Assert.Equal(FileOperation.StrictMutationRecoveryRequiredErrorCode, result.ErrorCode);
            Assert.Equal("old", await File.ReadAllTextAsync(target));
            Assert.Equal("OUTSIDE", await File.ReadAllTextAsync(outside));
        }
        finally
        {
            hook.Restore();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_UnlinksOnlyFromPinnedParent(bool recursive)
    {
        var fixture = CreateFixture();
        var targetName = recursive ? "tree" : "victim.txt";
        if (recursive)
        {
            Directory.CreateDirectory(Path.Combine(fixture.Pinned, targetName, "child"));
            await File.WriteAllTextAsync(Path.Combine(fixture.Pinned, targetName, "child", "safe.txt"), "safe");
            Directory.CreateDirectory(Path.Combine(fixture.Outside, targetName));
            await File.WriteAllTextAsync(Path.Combine(fixture.Outside, targetName, "sentinel.txt"), "OUTSIDE");
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Pinned, targetName), "safe");
            await File.WriteAllTextAsync(Path.Combine(fixture.Outside, targetName), "OUTSIDE");
        }
        var hook = fixture.CreateSwapHook(
            recursive ? LocalSecureFileSystemCheckpoint.BeforeOpen : LocalSecureFileSystemCheckpoint.BeforeUnlink,
            targetName);

        var result = await LocalFileSystemExecutor.DeleteFileAsync(
            fixture.Config,
            new AgentFileDeleteRequest { Path = Path.Combine("pinned", targetName), Recursive = recursive },
            false,
            CancellationToken.None,
            hooks: hook);
        hook.Restore();

        Assert.False(result.IsError, result.Summary);
        Assert.False(File.Exists(Path.Combine(fixture.Pinned, targetName)));
        Assert.False(Directory.Exists(Path.Combine(fixture.Pinned, targetName)));
        if (recursive)
        {
            Assert.Equal("OUTSIDE", await File.ReadAllTextAsync(Path.Combine(fixture.Outside, targetName, "sentinel.txt")));
        }
        else
        {
            Assert.Equal("OUTSIDE", await File.ReadAllTextAsync(Path.Combine(fixture.Outside, targetName)));
        }
    }

    [Fact]
    public async Task Delete_RecreatedBeforeReceipt_IsReportedAsCommittedAndCompensationRejectsReplacement()
    {
        var fixture = CreateFixture();
        var target = Path.Combine(fixture.Workspace, "receipt-race.txt");
        await File.WriteAllTextAsync(target, "original");
        using var approved = LocalResourceAuthorityTestContext.Approve(
            fixture.Config,
            target,
            "files.mutate");
        var executionContext = approved.Context with { CapturePostMutationResource = true };
        var hook = new RecreateAfterDeleteHook(target, "replacement");

        var deletion = await LocalFileSystemExecutor.DeleteFileAsync(
            fixture.Config,
            new AgentFileDeleteRequest { Path = "receipt-race.txt", Recursive = false },
            allowOutsideConfiguredScope: false,
            CancellationToken.None,
            approvedResourceReferences: executionContext.ApprovedResourceReferences,
            hooks: hook,
            authorizationContext: executionContext,
            resourceReferences: approved.ResourceReferences);

        Assert.True(hook.Invoked);
        Assert.False(deletion.IsError, deletion.Summary);
        var receipt = Assert.IsType<AgentResolvedResource>(deletion.PostMutationResource);
        Assert.False(receipt.Exists);
        Assert.Equal("replacement", await File.ReadAllTextAsync(target));

        var compensationContext = executionContext with
        {
            ApprovedResourceReferences = [receipt.CanonicalReference],
            ApprovedResourceClaims = receipt.ResourceClaim is null ? [] : [receipt.ResourceClaim],
            ApprovedResourceCapabilities = receipt.AuthorityReferences,
            CapturePostMutationResource = false,
        };
        var compensation = await LocalFileSystemExecutor.WriteFileAsync(
            fixture.Config,
            new AgentFileWriteRequest { Path = "receipt-race.txt", Content = "original", Overwrite = false },
            allowOutsideConfiguredScope: false,
            CancellationToken.None,
            approvedResourceReferences: compensationContext.ApprovedResourceReferences,
            authorizationContext: compensationContext,
            resourceReferences: approved.ResourceReferences);

        Assert.True(compensation.IsError);
        Assert.Equal("replacement", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task NonRecursiveDirectoryDelete_FailsClosedEvenWhenEmpty()
    {
        var fixture = CreateFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Pinned, "empty"));
        Directory.CreateDirectory(Path.Combine(fixture.Outside, "empty"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Outside, "empty", "sentinel.txt"), "OUTSIDE");
        var result = await LocalFileSystemExecutor.DeleteFileAsync(
            fixture.Config,
            new AgentFileDeleteRequest { Path = "pinned/empty", Recursive = false },
            false,
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("recursive-directory-delete-required", result.ErrorCode);
        Assert.True(Directory.Exists(Path.Combine(fixture.Pinned, "empty")));
        Assert.Equal("OUTSIDE", await File.ReadAllTextAsync(Path.Combine(fixture.Outside, "empty", "sentinel.txt")));
    }

    [Fact]
    public async Task Search_TraversesPinnedDirectoryAndPreservesNewlineNames()
    {
        var fixture = CreateFixture();
        var search = Path.Combine(fixture.Pinned, "search");
        Directory.CreateDirectory(search);
        await File.WriteAllTextAsync(Path.Combine(search, "line\nbreak.txt"), "needle");
        Directory.CreateDirectory(Path.Combine(fixture.Outside, "search"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Outside, "search", "secret.txt"), "needle OUTSIDE");
        var listing = await LocalFileSystemExecutor.ReadFileAsync(
            fixture.Config,
            new AgentFileReadRequest { Path = "pinned/search" },
            false,
            CancellationToken.None);
        var hook = fixture.CreateSwapHook(LocalSecureFileSystemCheckpoint.BeforeOpen, "search");

        var result = await LocalSecureFileSearch.ExecuteAsync(
            fixture.Config,
            new AgentFileSearchRequest { Path = "pinned/search", Kind = AgentFileSearchKind.Grep, Pattern = "needle", Include = "*.txt", Literal = false, Hidden = false },
            false,
            approvedResourceReferences: null,
            CancellationToken.None,
            hook);
        hook.Restore();

        var match = Assert.Single(result.Matches);
        Assert.Contains("line\\nbreak.txt", listing.Content, StringComparison.Ordinal);
        Assert.False(result.IsError, result.ErrorMessage);
        Assert.Contains("line\nbreak.txt", match.Path, StringComparison.Ordinal);
        Assert.DoesNotContain("OUTSIDE", match.Text, StringComparison.Ordinal);
        Assert.Equal("needle OUTSIDE", await File.ReadAllTextAsync(Path.Combine(fixture.Outside, "search", "secret.txt")));
    }

    [Fact]
    public async Task ScopedInstructions_ReadPinnedAgentsFile()
    {
        var fixture = CreateFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Pinned, "AGENTS.md"), "safe policy");
        await File.WriteAllTextAsync(Path.Combine(fixture.Outside, "AGENTS.md"), "OUTSIDE policy");
        var hook = fixture.CreateSwapHook(LocalSecureFileSystemCheckpoint.BeforeOpen, "AGENTS.md");

        var result = await LocalScopedInstructionDiscovery.DiscoverAsync(
            fixture.Config,
            new AgentScopedInstructionDiscoveryRequest
            {
                Probes = [new AgentScopedInstructionProbe { Path = "pinned", IsDirectory = true, FollowFinalSymbolicLink = true }],
            },
            CancellationToken.None,
            hook);
        hook.Restore();

        Assert.Equal("safe policy", Assert.Single(Assert.Single(result.Scopes).Documents).Content);
        Assert.Equal("OUTSIDE policy", await File.ReadAllTextAsync(Path.Combine(fixture.Outside, "AGENTS.md")));
    }

    [FileSystemFact(TestFileSystemCapability.FileSymbolicLinks, "Search must skip npm-style symlinks without following their targets.")]
    public async Task GlobPrunesUnrelatedTreesSkipsLinksAndFindsDirectoriesWithoutWeakeningExplicitPaths()
    {
        var fixture = CreateFixture();
        var app = Path.Combine(fixture.Workspace, "app");
        var bin = Path.Combine(fixture.Workspace, "node_modules", ".bin");
        Directory.CreateDirectory(Path.Combine(app, "src"));
        Directory.CreateDirectory(bin);
        var main = Path.Combine(app, "src", "main.ts");
        await File.WriteAllTextAsync(main, "safe");
        var secret = Path.Combine(fixture.Outside, "secret.ts");
        await File.WriteAllTextAsync(secret, "OUTSIDE");
        File.CreateSymbolicLink(Path.Combine(bin, "tsserver"), secret);
        File.CreateSymbolicLink(Path.Combine(bin, "dangling"), Path.Combine(fixture.Outside, "missing"));
        File.CreateSymbolicLink(Path.Combine(bin, "cycle"), Path.Combine(bin, "cycle"));

        var narrowed = await Search("app/**/*", new SearchPruningHook());
        Assert.False(narrowed.IsError, narrowed.ErrorMessage);
        Assert.Equal(main, Assert.Single(narrowed.Matches).Path);
        var broad = await Search("**/*.ts");
        Assert.False(broad.IsError, broad.ErrorMessage);
        Assert.Equal(main, Assert.Single(broad.Matches).Path);
        var directories = await Search("*/");
        Assert.False(directories.IsError, directories.ErrorMessage);
        Assert.Contains(directories.Matches, match => match.Path == app);
        Assert.Contains(directories.Matches, match => match.Path == Path.Combine(fixture.Workspace, "node_modules"));
        Assert.DoesNotContain(directories.Matches, match => match.Path == Path.Combine(app, "src"));
        var explicitLink = await LocalSecureFileSearch.ExecuteAsync(fixture.Config,
            new AgentFileSearchRequest { Path = "node_modules/.bin/tsserver", Kind = AgentFileSearchKind.Glob, Pattern = "*", Literal = false, Hidden = true },
            false, null, CancellationToken.None);
        Assert.True(explicitLink.IsError);
        Assert.Equal(AgentFileSearchErrorCodes.PathUnresolvable, explicitLink.ErrorCode);
        Assert.Empty(explicitLink.Matches);
        Assert.Equal("OUTSIDE", await File.ReadAllTextAsync(secret));

        async Task<LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileSearchResult> Search(string pattern, ILocalSecureFileSystemHooks? hook = null)
            => await LocalSecureFileSearch.ExecuteAsync(fixture.Config,
                new AgentFileSearchRequest { Path = ".", Kind = AgentFileSearchKind.Glob, Pattern = pattern, Literal = false, Hidden = true },
                false, null, CancellationToken.None, hook);
    }

    private sealed class SearchPruningHook : ILocalSecureFileSystemHooks
    {
        public void OnCheckpoint(LocalSecureFileSystemCheckpoint checkpoint, string parentPath, string? entryName)
        {
            if (checkpoint == LocalSecureFileSystemCheckpoint.BeforeOpen && entryName == "node_modules")
                throw new InvalidOperationException("The app-only search must not open node_modules.");
        }
    }

    [FileSystemFact(
        TestFileSystemCapability.DirectorySymbolicLinks,
        "Root and child link containment requires directory symbolic links.")]
    public async Task RootAndChildLinksAreRejectedWithoutFollowing()
    {
        var fixture = CreateFixture();
        var rootLink = Path.Combine(_root, "workspace-link");
        Directory.CreateSymbolicLink(rootLink, fixture.Workspace);
        var rootResult = await LocalFileSystemExecutor.ReadFileAsync(
            new LocalExecutionRuntimeConfig([rootLink], rootLink),
            new AgentFileReadRequest { Path = "." },
            false,
            CancellationToken.None);

        Directory.CreateSymbolicLink(Path.Combine(fixture.Workspace, "child-link"), fixture.Outside);
        var childResult = await LocalFileSystemExecutor.ReadFileAsync(
            fixture.Config,
            new AgentFileReadRequest { Path = "child-link" },
            false,
            CancellationToken.None);

        Assert.Equal(AgentFileReadErrorCodes.PathCanonicalizationFailed, rootResult.ErrorCode);
        Assert.Equal(AgentFileReadErrorCodes.PathCanonicalizationFailed, childResult.ErrorCode);
    }

    [PlatformFact(TestPlatform.UnixWithDistinctDevice, "st_dev transition validation requires distinct Unix devices.")]
    public void UnixDeviceTransition_IsRejectedWhenCapabilityIsObservable()
    {
        var rootDescriptor = LocalUnixNative.Open(
            "/",
            LocalUnixNative.ReadOnly | LocalUnixNative.DirectoryFlag | LocalUnixNative.NoFollowFlag | LocalUnixNative.CloseOnExecFlag);
        var deviceDescriptor = LocalUnixNative.Open(
            "/dev",
            LocalUnixNative.ReadOnly | LocalUnixNative.DirectoryFlag | LocalUnixNative.NoFollowFlag | LocalUnixNative.CloseOnExecFlag);
        Assert.True(rootDescriptor >= 0 && deviceDescriptor >= 0);
        using var rootHandle = new SafeFileHandle(new IntPtr(rootDescriptor), ownsHandle: true);
        using var deviceHandle = new SafeFileHandle(new IntPtr(deviceDescriptor), ownsHandle: true);
        Assert.Throws<LocalSecurePathException>(() => LocalSecurePathEngine.OpenRoot("/dev"));
    }

    [Fact]
    public void LinuxNativeConstantsAndStatLayouts_AreArchitectureSpecific()
    {
        Assert.Equal(1 << 16, LocalUnixNative.GetLinuxDirectoryFlag(Architecture.X64));
        Assert.Equal(1 << 17, LocalUnixNative.GetLinuxNoFollowFlag(Architecture.X64));
        Assert.Equal(1 << 14, LocalUnixNative.GetLinuxDirectoryFlag(Architecture.Arm64));
        Assert.Equal(1 << 15, LocalUnixNative.GetLinuxNoFollowFlag(Architecture.Arm64));

        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            for (var offset = 0; offset < 256; offset++)
            {
                Marshal.WriteByte(buffer, offset, 0);
            }
            Marshal.WriteInt64(buffer, 0, 11);
            Marshal.WriteInt64(buffer, 8, 12);
            Marshal.WriteInt64(buffer, 16, 13);
            Marshal.WriteInt32(buffer, 24, 14);
            Marshal.WriteInt32(buffer, 28, 15);
            Marshal.WriteInt32(buffer, 32, 16);
            Marshal.WriteInt64(buffer, 48, 17);
            Assert.Equal(
                new UnixMetadata(11, 12, 14, 17, 13, 15, 16),
                LocalUnixNative.ReadLinuxMetadata(buffer, Architecture.X64));

            for (var offset = 0; offset < 256; offset++)
            {
                Marshal.WriteByte(buffer, offset, 0);
            }
            Marshal.WriteInt64(buffer, 0, 21);
            Marshal.WriteInt64(buffer, 8, 22);
            Marshal.WriteInt32(buffer, 16, 23);
            Marshal.WriteInt32(buffer, 20, 24);
            Marshal.WriteInt32(buffer, 24, 25);
            Marshal.WriteInt32(buffer, 28, 26);
            Marshal.WriteInt64(buffer, 48, 27);
            Assert.Equal(
                new UnixMetadata(21, 22, 23, 27, 24, 25, 26),
                LocalUnixNative.ReadLinuxMetadata(buffer, Architecture.Arm64));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public async Task SearchHonorsCancellation()
    {
        var fixture = CreateFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await LocalSecureFileSearch.ExecuteAsync(
                fixture.Config,
                new AgentFileSearchRequest { Path = ".", Kind = AgentFileSearchKind.Glob, Pattern = "**/*", Literal = false, Hidden = true },
                false,
                approvedResourceReferences: null,
                cancellation.Token));
    }

    [Fact]
    public async Task SearchOutsideConfiguredScopeWithoutExactReferenceReturnsStructuredFailure()
    {
        var fixture = CreateFixture();

        var result = await LocalSecureFileSearch.ExecuteAsync(
            fixture.Config,
            new AgentFileSearchRequest { Path = fixture.Outside, Kind = AgentFileSearchKind.Glob, Pattern = "**/*", Literal = false, Hidden = true },
            allowOutsideConfiguredScope: true,
            approvedResourceReferences: null,
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(AgentFileSearchErrorCodes.OutsideConfiguredScope, result.ErrorCode);
        Assert.Empty(result.Matches);
    }

    [PlatformTheory(TestPlatform.Unix, "Mutation checkpoint CAS races require Unix file-descriptor semantics.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchRejectsUnboundedExpressions(bool braceExpansion)
    {
        var fixture = CreateFixture();
        var pattern = braceExpansion
            ? string.Concat(Enumerable.Repeat("{a,b}", 9))
            : new string('x', 4097);

        var result = await LocalSecureFileSearch.ExecuteAsync(
            fixture.Config,
            new AgentFileSearchRequest { Path = ".", Kind = AgentFileSearchKind.Glob, Pattern = pattern, Literal = false, Hidden = true },
            false,
            approvedResourceReferences: null,
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(AgentFileSearchErrorCodes.InvalidRequest, result.ErrorCode);
        Assert.Empty(result.Matches);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)0xff)]
    public async Task Search_DiscardsBufferedMatchesWhenLaterContentIsBinary(byte invalidByte)
    {
        var fixture = CreateFixture();
        var prefix = System.Text.Encoding.UTF8.GetBytes("needle\n" + new string('x', 9000));
        await File.WriteAllBytesAsync(
            Path.Combine(fixture.Workspace, "binary-late.dat"),
            [.. prefix, invalidByte]);

        var result = await LocalSecureFileSearch.ExecuteAsync(
            fixture.Config,
            new AgentFileSearchRequest { Path = "binary-late.dat", Kind = AgentFileSearchKind.Grep, Pattern = "needle", Literal = false, Hidden = false },
            false,
            approvedResourceReferences: null,
            CancellationToken.None);

        Assert.False(result.IsError, result.ErrorMessage);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public async Task ExpectedContentCas_AllowsMatchingWriteAndDelete()
    {
        var fixture = CreateFixture();
        var path = Path.Combine(fixture.Workspace, "cas.txt");
        await File.WriteAllTextAsync(path, "original");

        var write = await LocalFileSystemExecutor.WriteFileAsync(
            fixture.Config,
            new AgentFileWriteRequest
            {
                Path = "cas.txt",
                Content = "replacement",
                Overwrite = true,
                ExpectedContentHash = ContentHash("original"),
            },
            false,
            CancellationToken.None);
        var delete = await LocalFileSystemExecutor.DeleteFileAsync(
            fixture.Config,
            new AgentFileDeleteRequest
            {
                Path = "cas.txt",
                Recursive = false,
                ExpectedContentHash = ContentHash("replacement"),
            },
            false,
            CancellationToken.None);

        Assert.False(write.IsError, write.Summary);
        Assert.False(delete.IsError, delete.Summary);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpectedContentCas_RejectsContentChangedAtMutationCheckpoint(bool delete)
    {
        var fixture = CreateFixture();
        var path = Path.Combine(fixture.Workspace, "cas-race.txt");
        await File.WriteAllTextAsync(path, "original");
        var hook = new ContentMutationHook(
            delete ? LocalSecureFileSystemCheckpoint.BeforeUnlink : LocalSecureFileSystemCheckpoint.BeforePublish,
            path,
            "changed concurrently");

        var result = delete
            ? await LocalFileSystemExecutor.DeleteFileAsync(
                fixture.Config,
                new AgentFileDeleteRequest
                {
                    Path = "cas-race.txt",
                    Recursive = false,
                    ExpectedContentHash = ContentHash("original"),
                },
                false,
                CancellationToken.None,
                hooks: hook)
            : await LocalFileSystemExecutor.WriteFileAsync(
                fixture.Config,
                new AgentFileWriteRequest
                {
                    Path = "cas-race.txt",
                    Content = "replacement",
                    Overwrite = true,
                    ExpectedContentHash = ContentHash("original"),
                },
                false,
                CancellationToken.None,
                hooks: hook);

        Assert.True(hook.Invoked);
        Assert.True(result.IsError);
        Assert.Equal("file-content-changed", result.ErrorCode);
        Assert.Equal("changed concurrently", await File.ReadAllTextAsync(path));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(fixture.Workspace)
                .Where(file => HostSecurePathEngine.IsQuarantineName(Path.GetFileName(file)))
                .Select(File.ReadAllText),
            content => content == "changed concurrently");
    }

    [PlatformTheory(TestPlatform.Unix, "Final mutation syscall races require Unix file-descriptor semantics.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalMutationSyscall_DoesNotPublishOrDeleteReplacementEntry(bool delete)
    {
        var fixture = CreateFixture();
        var target = Path.Combine(fixture.Workspace, "final-race.txt");
        var parked = Path.Combine(fixture.Workspace, "approved-parked.txt");
        var replacement = Path.Combine(fixture.Workspace, "replacement-source.txt");
        await File.WriteAllTextAsync(target, "approved");
        await File.WriteAllTextAsync(replacement, "ATTACKER");
        var hook = new FinalRetargetingHook(
            delete ? LocalSecureFileSystemCheckpoint.BeforeUnlinkSyscall : LocalSecureFileSystemCheckpoint.BeforePublishSyscall,
            target,
            replacement,
            parked);

        var result = delete
            ? await LocalFileSystemExecutor.DeleteFileAsync(
                fixture.Config,
                new AgentFileDeleteRequest { Path = "final-race.txt", Recursive = false },
                false,
                CancellationToken.None,
                hooks: hook)
            : await LocalFileSystemExecutor.WriteFileAsync(
                fixture.Config,
                new AgentFileWriteRequest { Path = "final-race.txt", Content = "new", Overwrite = true },
                false,
                CancellationToken.None,
                hooks: hook);

        Assert.True(hook.Invoked);
        Assert.True(result.IsError);
        Assert.Equal("approved", await File.ReadAllTextAsync(parked));
        Assert.Equal("ATTACKER", await File.ReadAllTextAsync(target));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(fixture.Workspace)
                .Where(file => HostSecurePathEngine.IsQuarantineName(Path.GetFileName(file)))
                .Select(File.ReadAllText),
            content => content == "ATTACKER");
    }

    [Fact]
    public async Task MissingWriteTarget_CannotBeReplacedAtFinalPublishSyscall()
    {
        var fixture = CreateFixture();
        var target = Path.Combine(fixture.Workspace, "created-at-final.txt");
        var hook = new CreateAtFinalPublishHook(target, "ATTACKER");

        var result = await LocalFileSystemExecutor.WriteFileAsync(
            fixture.Config,
            new AgentFileWriteRequest { Path = "created-at-final.txt", Content = "new", Overwrite = false },
            false,
            CancellationToken.None,
            hooks: hook);

        Assert.True(hook.Invoked);
        Assert.True(result.IsError);
        Assert.Equal("ATTACKER", await File.ReadAllTextAsync(target));
    }

    [PlatformFact(TestPlatform.Windows, "Final mutation handle replacement is Windows-specific.")]
    public async Task WindowsFinalMutationHandles_BlockInPlaceEntryReplacement()
    {
        var fixture = CreateFixture();
        var outside = Path.Combine(fixture.Outside, "outside.txt");
        await File.WriteAllTextAsync(outside, "OUTSIDE");
        var writeTarget = Path.Combine(fixture.Workspace, "windows-write.txt");
        await File.WriteAllTextAsync(writeTarget, "old");
        var writeHook = new WindowsReplacementAttemptHook(
            LocalSecureFileSystemCheckpoint.BeforePublishSyscall,
            writeTarget,
            outside);

        var write = await LocalFileSystemExecutor.WriteFileAsync(
            fixture.Config,
            new AgentFileWriteRequest { Path = "windows-write.txt", Content = "new", Overwrite = true },
            false,
            CancellationToken.None,
            hooks: writeHook);

        var deleteTarget = Path.Combine(fixture.Workspace, "windows-delete.txt");
        await File.WriteAllTextAsync(deleteTarget, "delete");
        var deleteHook = new WindowsReplacementAttemptHook(
            LocalSecureFileSystemCheckpoint.BeforeUnlinkSyscall,
            deleteTarget,
            outside);
        var delete = await LocalFileSystemExecutor.DeleteFileAsync(
            fixture.Config,
            new AgentFileDeleteRequest { Path = "windows-delete.txt", Recursive = false },
            false,
            CancellationToken.None,
            hooks: deleteHook);

        Assert.False(write.IsError, write.Summary);
        Assert.True(writeHook.Invoked && writeHook.Blocked);
        Assert.Equal("new", await File.ReadAllTextAsync(writeTarget));
        Assert.False(delete.IsError, delete.Summary);
        Assert.True(deleteHook.Invoked && deleteHook.Blocked);
        Assert.Equal("OUTSIDE", await File.ReadAllTextAsync(outside));
    }

    [FileSystemFact(
        TestFileSystemCapability.HardLinks,
        "Structured-operation hard-link containment requires hard-link support.")]
    public async Task HardLinkedRegularFiles_AreRejectedAcrossStructuredOperations()
    {
        var fixture = CreateFixture();
        var target = Path.Combine(fixture.Workspace, "linked.txt");
        var alias = Path.Combine(fixture.Workspace, "linked-alias.txt");
        await File.WriteAllTextAsync(target, "content");
        TestFileSystemCapabilities.CreateHardLink(alias, target);

        var read = await LocalFileSystemExecutor.ReadFileAsync(
            fixture.Config,
            new AgentFileReadRequest { Path = "linked.txt" },
            false,
            CancellationToken.None);
        var write = await LocalFileSystemExecutor.WriteFileAsync(
            fixture.Config,
            new AgentFileWriteRequest { Path = "linked.txt", Content = "replacement", Overwrite = true },
            false,
            CancellationToken.None);
        var delete = await LocalFileSystemExecutor.DeleteFileAsync(
            fixture.Config,
            new AgentFileDeleteRequest { Path = "linked.txt", Recursive = false },
            false,
            CancellationToken.None);
        var search = await LocalSecureFileSearch.ExecuteAsync(
            fixture.Config,
            new AgentFileSearchRequest { Path = "linked.txt", Kind = AgentFileSearchKind.Grep, Pattern = "content", Literal = false, Hidden = false },
            false,
            approvedResourceReferences: null,
            CancellationToken.None);
        var instruction = Path.Combine(fixture.Workspace, "AGENTS.md");
        await File.WriteAllTextAsync(instruction, "policy");
        TestFileSystemCapabilities.CreateHardLink(Path.Combine(fixture.Workspace, "policy-alias.md"), instruction);
        var discovery = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await LocalScopedInstructionDiscovery.DiscoverAsync(
                fixture.Config,
                new AgentScopedInstructionDiscoveryRequest
                {
                    Probes = [new AgentScopedInstructionProbe { Path = ".", IsDirectory = true, FollowFinalSymbolicLink = true }],
                },
                CancellationToken.None));

        Assert.Equal(AgentFileReadErrorCodes.PathCanonicalizationFailed, read.ErrorCode);
        Assert.Equal(AgentFileReadErrorCodes.PathCanonicalizationFailed, write.ErrorCode);
        Assert.Equal(AgentFileReadErrorCodes.PathCanonicalizationFailed, delete.ErrorCode);
        Assert.Equal(AgentFileSearchErrorCodes.PathUnresolvable, search.ErrorCode);
        Assert.Contains("Hard-linked", discovery.InnerException?.Message ?? discovery.Message, StringComparison.Ordinal);
        Assert.Equal("content", await File.ReadAllTextAsync(alias));
    }

    [Fact]
    public async Task ExactOutsideApproval_AllowsDeletingTheApprovedDirectory()
    {
        var fixture = CreateFixture();
        var approvedDirectory = Path.Combine(fixture.Outside, "approved-directory");
        Directory.CreateDirectory(approvedDirectory);
        await File.WriteAllTextAsync(Path.Combine(approvedDirectory, "file.txt"), "content");
        using var approved = LocalResourceAuthorityTestContext.Approve(
            fixture.Config,
            approvedDirectory,
            "files.mutate");

        var result = await LocalFileSystemExecutor.DeleteFileAsync(
            fixture.Config,
            new AgentFileDeleteRequest { Path = approvedDirectory, Recursive = true },
            allowOutsideConfiguredScope: true,
            CancellationToken.None,
            approvedResourceReferences: approved.Context.ApprovedResourceReferences,
            authorizationContext: approved.Context,
            resourceReferences: approved.ResourceReferences);

        Assert.False(result.IsError, result.Summary);
        Assert.False(Directory.Exists(approvedDirectory));
    }

    [Fact]
    public async Task RecursiveDelete_RejectsAncestorOfNestedConfiguredRoot()
    {
        var outer = Path.Combine(_root, "configured-outer");
        var parent = Path.Combine(outer, "parent");
        var nested = Path.Combine(parent, "nested-root");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "keep.txt"), "keep");
        var config = new LocalExecutionRuntimeConfig([outer, nested], outer);

        var result = await LocalFileSystemExecutor.DeleteFileAsync(
            config,
            new AgentFileDeleteRequest { Path = "parent", Recursive = true },
            false,
            CancellationToken.None);

        Assert.Equal(AgentFileReadErrorCodes.OutsideConfiguredScope, result.ErrorCode);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(nested, "keep.txt")));
    }

    [PlatformFact(TestPlatform.MacCaseInsensitive, "This verifies case-insensitive macOS configured-root aliases.")]
    public async Task RecursiveDelete_RejectsCaseFoldedAliasOfNestedConfiguredRoot()
    {
        var outer = Path.Combine(_root, "CaseConfiguredOuter");
        var parent = Path.Combine(outer, "Parent");
        var nested = Path.Combine(parent, "NestedRoot");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "keep.txt"), "keep");
        var caseFoldedNested = Path.Combine(outer.ToLowerInvariant(), "parent", "nestedroot");
        var config = new LocalExecutionRuntimeConfig([outer, caseFoldedNested], outer);

        var result = await LocalFileSystemExecutor.DeleteFileAsync(
            config,
            new AgentFileDeleteRequest { Path = "Parent", Recursive = true },
            false,
            CancellationToken.None);

        Assert.Equal(AgentFileReadErrorCodes.OutsideConfiguredScope, result.ErrorCode);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(nested, "keep.txt")));
    }

    [Fact]
    public async Task RecursiveDeleteCancellation_LeavesGateAndOutsideSentinelUntouched()
    {
        var fixture = CreateFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Pinned, "tree"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Pinned, "tree", "file.txt"), "safe");
        await File.WriteAllTextAsync(Path.Combine(fixture.Outside, "sentinel.txt"), "OUTSIDE");
        using var root = HostSecurePathEngine.OpenRoot(fixture.Pinned);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await LocalFileSystemExecutor.DeleteFileAsync(
                fixture.Config,
                new AgentFileDeleteRequest { Path = "pinned/tree", Recursive = true },
                false,
                cancellation.Token));

        Assert.False(HostMutationCoordinator.IsEntered(root.Handle.Identity));
        Assert.Equal("OUTSIDE", await File.ReadAllTextAsync(Path.Combine(fixture.Outside, "sentinel.txt")));
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
        return new Fixture(workspace, pinned, outside, new LocalExecutionRuntimeConfig([workspace], workspace));
    }

    private static string ContentHash(string content)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static void AssertNoTemporaryEntries(string directory)
        => Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(directory),
            path => HostSecurePathEngine.IsTemporaryName(Path.GetFileName(path)));

    private sealed record Fixture(
        string Workspace,
        string Pinned,
        string Outside,
        LocalExecutionRuntimeConfig Config)
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
                || !LocalSecurePathEngine.PathComparer.Equals(parentPath, parent)
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
                // A held ancestor without FILE_SHARE_DELETE must block replacement on Windows.
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

    private sealed class ContentMutationHook(
        LocalSecureFileSystemCheckpoint checkpoint,
        string targetPath,
        string replacement) : ILocalSecureFileSystemHooks
    {
        public bool Invoked { get; private set; }

        public void OnCheckpoint(LocalSecureFileSystemCheckpoint current, string parentPath, string? entryName)
        {
            if (Invoked
                || current != checkpoint
                || !LocalSecurePathEngine.PathComparer.Equals(
                    Path.Combine(parentPath, entryName ?? string.Empty),
                    targetPath))
            {
                return;
            }

            Invoked = true;
            File.WriteAllText(targetPath, replacement);
        }
    }

    private sealed class TemporaryEntrySwapHook(
        string targetPath,
        string outsidePath) : ILocalSecureFileSystemHooks
    {
        private string? _parkedPath;
        private string? _temporaryPath;

        public void OnCheckpoint(LocalSecureFileSystemCheckpoint current, string parentPath, string? entryName)
        {
            if (_temporaryPath is not null
                || current != LocalSecureFileSystemCheckpoint.BeforePublish
                || !LocalSecurePathEngine.PathComparer.Equals(
                    Path.Combine(parentPath, entryName ?? string.Empty),
                    targetPath))
            {
                return;
            }

            var temporaryPath = Assert.Single(Directory.EnumerateFiles(parentPath, ".sunder-secure-temp-*"));
            var parkedPath = temporaryPath + ".parked";
            File.Move(temporaryPath, parkedPath);
            try
            {
                File.CreateSymbolicLink(temporaryPath, outsidePath);
            }
            catch
            {
                File.Move(parkedPath, temporaryPath);
                throw;
            }
            _temporaryPath = temporaryPath;
            _parkedPath = parkedPath;
        }

        public void Restore()
        {
            if (_temporaryPath is null)
            {
                return;
            }
            File.Delete(_temporaryPath);
            File.Delete(_parkedPath!);
        }
    }

    private sealed class NamedTemporaryEntrySwapHook(
        string targetPath,
        string outsidePath,
        string parkingDirectory) : ILocalSecureFileSystemHooks
    {
        private string? _parkedPath;
        private string? _temporaryPath;

        public bool Invoked { get; private set; }

        public void OnCheckpoint(LocalSecureFileSystemCheckpoint current, string parentPath, string? entryName)
        {
            if (Invoked
                || current != LocalSecureFileSystemCheckpoint.BeforeNamedTemporaryPublish
                || !LocalSecurePathEngine.PathComparer.Equals(
                    Path.Combine(parentPath, entryName ?? string.Empty),
                    targetPath))
            {
                return;
            }

            Invoked = true;
            var temporaryPath = Assert.Single(
                Directory.EnumerateFileSystemEntries(parentPath),
                path => HostSecurePathEngine.IsTemporaryName(Path.GetFileName(path)));
            var parkedPath = Path.Combine(parkingDirectory, "parked-" + Guid.NewGuid().ToString("N"));
            File.Move(temporaryPath, parkedPath);
            try
            {
                File.CreateSymbolicLink(temporaryPath, outsidePath);
            }
            catch
            {
                File.Move(parkedPath, temporaryPath);
                throw;
            }
            _temporaryPath = temporaryPath;
            _parkedPath = parkedPath;
        }

        public void Restore()
        {
            if (_temporaryPath is not null)
            {
                File.Delete(_temporaryPath);
            }
            if (_parkedPath is not null)
            {
                File.Delete(_parkedPath);
            }
        }
    }

    private sealed class FinalRetargetingHook(
        LocalSecureFileSystemCheckpoint checkpoint,
        string target,
        string replacement,
        string parked) : ILocalSecureFileSystemHooks
    {
        public bool Invoked { get; private set; }

        public void OnCheckpoint(LocalSecureFileSystemCheckpoint current, string parentPath, string? entryName)
        {
            if (Invoked || current != checkpoint)
            {
                return;
            }
            Invoked = true;
            File.Move(target, parked);
            File.Move(replacement, target);
        }
    }

    private sealed class CreateAtFinalPublishHook(string target, string content) : ILocalSecureFileSystemHooks
    {
        public bool Invoked { get; private set; }

        public void OnCheckpoint(LocalSecureFileSystemCheckpoint checkpoint, string parentPath, string? entryName)
        {
            if (Invoked || checkpoint != LocalSecureFileSystemCheckpoint.BeforePublishSyscall)
            {
                return;
            }
            Invoked = true;
            File.WriteAllText(target, content);
        }
    }

    private sealed class WindowsReplacementAttemptHook(
        LocalSecureFileSystemCheckpoint checkpoint,
        string target,
        string outside) : ILocalSecureFileSystemHooks
    {
        public bool Invoked { get; private set; }

        public bool Blocked { get; private set; }

        public void OnCheckpoint(LocalSecureFileSystemCheckpoint current, string parentPath, string? entryName)
        {
            if (Invoked || current != checkpoint)
            {
                return;
            }
            Invoked = true;
            try
            {
                File.Delete(target);
                File.CreateSymbolicLink(target, outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Blocked = true;
            }
        }
    }

    private sealed class RecreateAfterDeleteHook(string target, string content) : ILocalSecureFileSystemHooks
    {
        public bool Invoked { get; private set; }

        public void OnCheckpoint(LocalSecureFileSystemCheckpoint checkpoint, string parentPath, string? entryName)
        {
            if (Invoked || checkpoint != LocalSecureFileSystemCheckpoint.AfterDeleteBeforeAuthorityCapture)
            {
                return;
            }
            Invoked = true;
            File.WriteAllText(target, content);
        }
    }
}
