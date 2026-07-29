using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Execution.Local;
using Xunit;
using Xunit.Sdk;

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
            new AgentFileReadRequest("pinned/safe.txt"),
            false,
            CancellationToken.None,
            hooks: readHook);
        readHook.Restore();

        var listHook = fixture.CreateSwapHook(LocalSecureFileSystemCheckpoint.BeforeOpen, "dir");
        var listing = await LocalFileSystemExecutor.ReadFileAsync(
            fixture.Config,
            new AgentFileReadRequest("pinned/dir"),
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

    [Theory]
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
            new AgentFileWriteRequest(Path.Combine("pinned", targetName), "new", Overwrite: overwrite),
            false,
            CancellationToken.None,
            hooks: hook);
        hook.Restore();

        Assert.False(result.IsError, result.Summary);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(fixture.Pinned, targetName)));
        Assert.Equal("OUTSIDE", await File.ReadAllTextAsync(Path.Combine(fixture.Outside, targetName)));
        Assert.Empty(Directory.EnumerateFiles(fixture.Pinned, $".{targetName}.sunder-*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinuxWrite_AtEmptyPathEperm_UsesProcFallbackWithoutTemporaryLeak(bool overwrite)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

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
                new AgentFileWriteRequest(targetName, "new", Overwrite: overwrite),
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinuxWrite_WhenAnonymousLinksAreUnavailable_UsesNamedFallbackWithoutTemporaryLeak(bool overwrite)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

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
                new AgentFileWriteRequest("named-fallback.txt", "content", Overwrite: overwrite),
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

    [Fact]
    public async Task LinuxNamedFallback_RejectsTemporaryEntrySubstitutionAndRestoresOriginal()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

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
                    new AgentFileWriteRequest("named-attack.txt", "replacement"),
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

    [Fact]
    public async Task LinuxNamedFallback_TargetCreationRaceRemainsNoReplaceAndCleansTemporaryEntry()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var fixture = CreateFixture();
        var target = Path.Combine(fixture.Workspace, "named-race.txt");
        var hook = new CreateAtFinalPublishHook(target, "ATTACKER");
        AgentFileMutationResult result;
        using (LocalUnixNative.OverrideAnonymousPublishErrors(method =>
                   method == LocalUnixAnonymousPublishMethod.EmptyPath ? 1 : 2))
        {
            result = await LocalFileSystemExecutor.WriteFileAsync(
                fixture.Config,
                new AgentFileWriteRequest("named-race.txt", "new", Overwrite: false),
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

    [Fact]
    public async Task Write_RejectsTemporaryNameSubstitutionBeforePublish()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

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
                new AgentFileWriteRequest("pinned/target.txt", "new"),
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
            new AgentFileDeleteRequest(Path.Combine("pinned", targetName), Recursive: recursive),
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
            new AgentFileDeleteRequest("receipt-race.txt"),
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
            new AgentFileWriteRequest("receipt-race.txt", "original", Overwrite: false),
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
            new AgentFileDeleteRequest("pinned/empty"),
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
            new AgentFileReadRequest("pinned/search"),
            false,
            CancellationToken.None);
        var hook = fixture.CreateSwapHook(LocalSecureFileSystemCheckpoint.BeforeOpen, "search");

        var result = await LocalSecureFileSearch.ExecuteAsync(
            fixture.Config,
            new AgentFileSearchRequest("pinned/search", AgentFileSearchKind.Grep, "needle", "*.txt"),
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
            new AgentScopedInstructionDiscoveryRequest(
                [new AgentScopedInstructionProbe("pinned", IsDirectory: true)]),
            CancellationToken.None,
            hook);
        hook.Restore();

        Assert.Equal("safe policy", Assert.Single(Assert.Single(result.Scopes).Documents).Content);
        Assert.Equal("OUTSIDE policy", await File.ReadAllTextAsync(Path.Combine(fixture.Outside, "AGENTS.md")));
    }

    [Fact]
    public async Task RootAndChildLinksAreRejectedWithoutFollowing()
    {
        var fixture = CreateFixture();
        var rootLink = Path.Combine(_root, "workspace-link");
        CreateDirectoryLinkOrSkip(rootLink, fixture.Workspace);
        var rootResult = await LocalFileSystemExecutor.ReadFileAsync(
            new LocalExecutionRuntimeConfig([rootLink], rootLink),
            new AgentFileReadRequest("."),
            false,
            CancellationToken.None);

        CreateDirectoryLinkOrSkip(Path.Combine(fixture.Workspace, "child-link"), fixture.Outside);
        var childResult = await LocalFileSystemExecutor.ReadFileAsync(
            fixture.Config,
            new AgentFileReadRequest("child-link"),
            false,
            CancellationToken.None);

        Assert.Equal(AgentFileReadErrorCodes.PathCanonicalizationFailed, rootResult.ErrorCode);
        Assert.Equal(AgentFileReadErrorCodes.PathCanonicalizationFailed, childResult.ErrorCode);
    }

    [Fact]
    public void UnixDeviceTransition_IsRejectedWhenCapabilityIsObservable()
    {
        if (OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("st_dev transition validation is a Unix capability test.");
        }

        var rootDescriptor = LocalUnixNative.Open(
            "/",
            LocalUnixNative.ReadOnly | LocalUnixNative.DirectoryFlag | LocalUnixNative.NoFollowFlag | LocalUnixNative.CloseOnExecFlag);
        var deviceDescriptor = LocalUnixNative.Open(
            "/dev",
            LocalUnixNative.ReadOnly | LocalUnixNative.DirectoryFlag | LocalUnixNative.NoFollowFlag | LocalUnixNative.CloseOnExecFlag);
        Assert.True(rootDescriptor >= 0 && deviceDescriptor >= 0);
        using var rootHandle = new SafeFileHandle(new IntPtr(rootDescriptor), ownsHandle: true);
        using var deviceHandle = new SafeFileHandle(new IntPtr(deviceDescriptor), ownsHandle: true);
        if (LocalUnixNative.ReadMetadata(rootDescriptor).Device == LocalUnixNative.ReadMetadata(deviceDescriptor).Device)
        {
            throw SkipException.ForSkip("/dev does not expose a distinct device on this host.");
        }

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
                new AgentFileSearchRequest(".", AgentFileSearchKind.Glob, "**/*"),
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
            new AgentFileSearchRequest(fixture.Outside, AgentFileSearchKind.Glob, "**/*"),
            allowOutsideConfiguredScope: true,
            approvedResourceReferences: null,
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(AgentFileSearchErrorCodes.OutsideConfiguredScope, result.ErrorCode);
        Assert.Empty(result.Matches);
    }

    [Theory]
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
            new AgentFileSearchRequest(".", AgentFileSearchKind.Glob, pattern),
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
            new AgentFileSearchRequest("binary-late.dat", AgentFileSearchKind.Grep, "needle"),
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
            new AgentFileWriteRequest("cas.txt", "replacement")
            {
                ExpectedContentHash = ContentHash("original"),
            },
            false,
            CancellationToken.None);
        var delete = await LocalFileSystemExecutor.DeleteFileAsync(
            fixture.Config,
            new AgentFileDeleteRequest("cas.txt")
            {
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
        if (OperatingSystem.IsWindows())
        {
            return;
        }
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
                new AgentFileDeleteRequest("cas-race.txt")
                {
                    ExpectedContentHash = ContentHash("original"),
                },
                false,
                CancellationToken.None,
                hooks: hook)
            : await LocalFileSystemExecutor.WriteFileAsync(
                fixture.Config,
                new AgentFileWriteRequest("cas-race.txt", "replacement")
                {
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalMutationSyscall_DoesNotPublishOrDeleteReplacementEntry(bool delete)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
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
                new AgentFileDeleteRequest("final-race.txt"),
                false,
                CancellationToken.None,
                hooks: hook)
            : await LocalFileSystemExecutor.WriteFileAsync(
                fixture.Config,
                new AgentFileWriteRequest("final-race.txt", "new"),
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
            new AgentFileWriteRequest("created-at-final.txt", "new", Overwrite: false),
            false,
            CancellationToken.None,
            hooks: hook);

        Assert.True(hook.Invoked);
        Assert.True(result.IsError);
        Assert.Equal("ATTACKER", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task WindowsFinalMutationHandles_BlockInPlaceEntryReplacement()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
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
            new AgentFileWriteRequest("windows-write.txt", "new"),
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
            new AgentFileDeleteRequest("windows-delete.txt"),
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

    [Fact]
    public async Task HardLinkedRegularFiles_AreRejectedAcrossStructuredOperations()
    {
        var fixture = CreateFixture();
        var target = Path.Combine(fixture.Workspace, "linked.txt");
        var alias = Path.Combine(fixture.Workspace, "linked-alias.txt");
        await File.WriteAllTextAsync(target, "content");
        CreateHardLinkOrSkip(alias, target);

        var read = await LocalFileSystemExecutor.ReadFileAsync(
            fixture.Config,
            new AgentFileReadRequest("linked.txt"),
            false,
            CancellationToken.None);
        var write = await LocalFileSystemExecutor.WriteFileAsync(
            fixture.Config,
            new AgentFileWriteRequest("linked.txt", "replacement"),
            false,
            CancellationToken.None);
        var delete = await LocalFileSystemExecutor.DeleteFileAsync(
            fixture.Config,
            new AgentFileDeleteRequest("linked.txt"),
            false,
            CancellationToken.None);
        var search = await LocalSecureFileSearch.ExecuteAsync(
            fixture.Config,
            new AgentFileSearchRequest("linked.txt", AgentFileSearchKind.Grep, "content"),
            false,
            approvedResourceReferences: null,
            CancellationToken.None);
        var instruction = Path.Combine(fixture.Workspace, "AGENTS.md");
        await File.WriteAllTextAsync(instruction, "policy");
        CreateHardLinkOrSkip(Path.Combine(fixture.Workspace, "policy-alias.md"), instruction);
        var discovery = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await LocalScopedInstructionDiscovery.DiscoverAsync(
                fixture.Config,
                new AgentScopedInstructionDiscoveryRequest(
                    [new AgentScopedInstructionProbe(".", IsDirectory: true)]),
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
            new AgentFileDeleteRequest(approvedDirectory, Recursive: true),
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
            new AgentFileDeleteRequest("parent", Recursive: true),
            false,
            CancellationToken.None);

        Assert.Equal(AgentFileReadErrorCodes.OutsideConfiguredScope, result.ErrorCode);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(nested, "keep.txt")));
    }

    [Fact]
    public async Task RecursiveDelete_RejectsCaseFoldedAliasOfNestedConfiguredRoot()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        var outer = Path.Combine(_root, "CaseConfiguredOuter");
        var parent = Path.Combine(outer, "Parent");
        var nested = Path.Combine(parent, "NestedRoot");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "keep.txt"), "keep");
        var caseFoldedNested = Path.Combine(outer.ToLowerInvariant(), "parent", "nestedroot");
        if (!Directory.Exists(caseFoldedNested))
        {
            return;
        }
        var config = new LocalExecutionRuntimeConfig([outer, caseFoldedNested], outer);

        var result = await LocalFileSystemExecutor.DeleteFileAsync(
            config,
            new AgentFileDeleteRequest("Parent", Recursive: true),
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
                new AgentFileDeleteRequest("pinned/tree", Recursive: true),
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

    private static void CreateDirectoryLinkOrSkip(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            throw SkipException.ForSkip($"Directory links are unavailable: {ex.Message}");
        }
    }

    private static void CreateHardLinkOrSkip(string link, string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!CreateHardLinkWindows(link, target, IntPtr.Zero))
                {
                    throw new IOException(
                        new System.ComponentModel.Win32Exception(
                            System.Runtime.InteropServices.Marshal.GetLastPInvokeError()).Message);
                }
                return;
            }

            var directory = Path.GetDirectoryName(target)!;
            using var root = LocalSecurePathEngine.OpenRoot(directory);
            LocalUnixNative.Link(
                root.Handle.Handle.DangerousGetHandle().ToInt32(),
                Path.GetFileName(target),
                Path.GetFileName(link));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            throw SkipException.ForSkip($"Hard links are unavailable: {ex.Message}");
        }
    }

    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string fileName, string existingFileName, IntPtr securityAttributes);

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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
            {
                File.Move(parkedPath, temporaryPath);
                throw SkipException.ForSkip($"File links are unavailable: {ex.Message}");
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
            {
                File.Move(parkedPath, temporaryPath);
                throw SkipException.ForSkip($"File links are unavailable: {ex.Message}");
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
