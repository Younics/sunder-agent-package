extern alias LocalWorker;

using System.Runtime.Versioning;
using LocalWorker::Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.TestSupport;
using Xunit;
using AgentFileDeleteRequest = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileDeleteRequest;
using AgentFileMutationResult = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileMutationResult;
using AgentFileReadRequest = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileReadRequest;
using AgentFileSearchKind = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileSearchKind;
using AgentFileSearchRequest = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileSearchRequest;
using AgentFileWriteRequest = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileWriteRequest;
using AgentFileSearchErrorCodes = Sunder.Package.Agent.Contracts.Models.AgentFileSearchErrorCodes;
using AgentScopedInstructionDiscoveryRequest = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentScopedInstructionDiscoveryRequest;
using AgentScopedInstructionProbe = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentScopedInstructionProbe;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class StrictMutationReauditTests : IDisposable
{
    private readonly string _root = Path.Combine(
        TemporaryDirectory.GetPhysicalTemporaryRoot(),
        "sunder-strict-reaudit",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ApprovalLeaseStore_IssuesDistinctValuesAndDisposesThemOnExpiryAndShutdown()
    {
        var now = DateTimeOffset.UtcNow;
        using var store = new HostApprovalLeaseStore<DisposableProbe>(TimeSpan.FromMinutes(1), () => now);
        var first = new DisposableProbe();
        var duplicate = new DisposableProbe();
        var token = store.Issue("same", first);
        var duplicateToken = store.Issue("same", duplicate);

        Assert.NotEqual(token, duplicateToken);
        Assert.False(duplicate.IsDisposed);
        Assert.False(first.IsDisposed);

        now += TimeSpan.FromMinutes(2);
        Assert.False(store.TryRedeem(token, static _ => true, out _));
        Assert.True(first.IsDisposed);
        Assert.True(duplicate.IsDisposed);

        var shutdown = new DisposableProbe();
        store.Issue("shutdown", shutdown);
        var revoked = new DisposableProbe();
        var revokedToken = store.Issue("revoked", revoked);
        Assert.True(store.TryRevoke(revokedToken));
        Assert.True(revoked.IsDisposed);
        store.Dispose();
        Assert.True(shutdown.IsDisposed);
    }

    [Fact]
    public void ExpiredApproval_DisposesRetainedFilesystemAuthority()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "retained.txt");
        File.WriteAllText(path, "secret");
        var authority = HostSecurePathEngine.Capture([_root], path);
        var now = DateTimeOffset.UtcNow;
        using var store = new HostApprovalLeaseStore<LocalSecureApprovalLease>(TimeSpan.FromMinutes(1), () => now);
        var token = store.Issue("authority", authority);

        now += TimeSpan.FromMinutes(2);
        Assert.False(store.TryRedeem(token, static _ => true, out _));
        Assert.Throws<LocalSecureApprovalChangedException>(() =>
            authority.OpenSession(createParents: false, hooks: null, CancellationToken.None));
    }

    [Fact]
    public void MissingIntermediateOutsideApproval_IsRejectedBeforeLease()
    {
        var workspace = Path.Combine(_root, "workspace");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        var path = Path.Combine(outside, "missing-parent", "target.txt");
        var config = new LocalExecutionRuntimeConfig([workspace], workspace);

        Assert.Throws<LocalSecurePathNotFoundException>(() =>
            LocalResourceResolver.ResolveFileResource(config, path, allowOutsideConfiguredScope: true));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task MissingFinalOutsideApproval_PublishesNoReplaceOnlyAtApprovedName()
    {
        var workspace = Path.Combine(_root, "workspace");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        var config = new LocalExecutionRuntimeConfig([workspace], workspace);
        var approvedPath = Path.Combine(outside, "approved.txt");
        using var approved = LocalResourceAuthorityTestContext.Approve(config, approvedPath, "files.mutate");

        var written = await LocalFileSystemExecutor.WriteFileAsync(
            config,
            new AgentFileWriteRequest { Path = approvedPath, Content = "approved", Overwrite = false },
            allowOutsideConfiguredScope: true,
            CancellationToken.None,
            approvedResourceReferences: approved.Context.ApprovedResourceReferences,
            authorizationContext: approved.Context,
            resourceReferences: approved.ResourceReferences);

        var racedPath = Path.Combine(outside, "raced.txt");
        using var raced = LocalResourceAuthorityTestContext.Approve(config, racedPath, "files.mutate");
        await File.WriteAllTextAsync(racedPath, "ATTACKER");
        var rejected = await LocalFileSystemExecutor.WriteFileAsync(
            config,
            new AgentFileWriteRequest { Path = racedPath, Content = "must-not-replace", Overwrite = false },
            allowOutsideConfiguredScope: true,
            CancellationToken.None,
            approvedResourceReferences: raced.Context.ApprovedResourceReferences,
            authorizationContext: raced.Context,
            resourceReferences: raced.ResourceReferences);

        Assert.False(written.IsError, written.Summary);
        Assert.Equal("approved", await File.ReadAllTextAsync(approvedPath));
        Assert.True(rejected.IsError);
        Assert.Equal(LocalResourceReference.ReapprovalRequiredErrorCode, rejected.ErrorCode);
        Assert.Equal("ATTACKER", await File.ReadAllTextAsync(racedPath));
    }

    [Fact]
    public async Task MissingFinalOutsideApproval_CannotBeRedirectedThroughReparenting()
    {
        var workspace = Path.Combine(_root, "workspace");
        var outside = Path.Combine(_root, "outside");
        var approvedParent = Path.Combine(outside, "approved-parent");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(approvedParent);
        var config = new LocalExecutionRuntimeConfig([workspace], workspace);
        var approvedPath = Path.Combine(approvedParent, "target.txt");
        using var approved = LocalResourceAuthorityTestContext.Approve(config, approvedPath, "files.mutate");

        var originalParent = Path.Combine(outside, "original-parent");
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<IOException>(() => Directory.Move(approvedParent, originalParent));
            Assert.False(Directory.Exists(originalParent));
        }
        else
        {
            Directory.Move(approvedParent, originalParent);
            Directory.CreateDirectory(approvedParent);
        }
        var rejected = await LocalFileSystemExecutor.WriteFileAsync(
            config,
            new AgentFileWriteRequest { Path = approvedPath, Content = "must-not-write", Overwrite = false },
            allowOutsideConfiguredScope: true,
            CancellationToken.None,
            approvedResourceReferences: approved.Context.ApprovedResourceReferences,
            authorizationContext: approved.Context,
            resourceReferences: approved.ResourceReferences);

        if (OperatingSystem.IsWindows())
        {
            Assert.False(rejected.IsError, rejected.Summary);
            Assert.Equal("must-not-write", await File.ReadAllTextAsync(approvedPath));
        }
        else
        {
            Assert.Equal(LocalResourceReference.ReapprovalRequiredErrorCode, rejected.ErrorCode);
            Assert.False(File.Exists(approvedPath));
        }
        Assert.False(File.Exists(Path.Combine(originalParent, "target.txt")));
    }

    [Fact]
    public async Task ReservedEntries_AreHiddenFromListGrepGlobAndScopedTraversal()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var quarantine = Path.Combine(workspace, ".sunder-secure-quarantine-stale");
        Directory.CreateDirectory(quarantine);
        await File.WriteAllTextAsync(Path.Combine(quarantine, "AGENTS.md"), "quarantine-secret");
        await File.WriteAllTextAsync(Path.Combine(workspace, ".sunder-secure-temp-stale"), "temporary-secret");
        await File.WriteAllTextAsync(Path.Combine(workspace, "visible.txt"), "visible");
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, ".SUNDER-SECURE-TEMP-UPPER"), "uppercase-secret");
        }
        var config = new LocalExecutionRuntimeConfig([workspace], workspace);

        var listing = await LocalFileSystemExecutor.ReadFileAsync(
            config,
            new AgentFileReadRequest { Path = "." },
            false,
            CancellationToken.None);
        var grep = await LocalSecureFileSearch.ExecuteAsync(
            config,
            new AgentFileSearchRequest { Path = ".", Kind = AgentFileSearchKind.Grep, Pattern = "secret", Literal = false, Hidden = true },
            false,
            approvedResourceReferences: null,
            CancellationToken.None);
        var glob = await LocalSecureFileSearch.ExecuteAsync(
            config,
            new AgentFileSearchRequest { Path = ".", Kind = AgentFileSearchKind.Glob, Pattern = "**/*", Literal = false, Hidden = true },
            false,
            approvedResourceReferences: null,
            CancellationToken.None);
        var direct = await LocalFileSystemExecutor.ReadFileAsync(
            config,
            new AgentFileReadRequest { Path = ".sunder-secure-temp-stale" },
            false,
            CancellationToken.None);

        Assert.False(listing.IsError, listing.ErrorMessage);
        Assert.Contains("visible.txt", listing.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("sunder-secure", listing.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(grep.Matches);
        Assert.DoesNotContain(glob.Matches, match => match.Path.Contains("sunder-secure", StringComparison.OrdinalIgnoreCase));
        Assert.True(direct.IsError);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await LocalScopedInstructionDiscovery.DiscoverAsync(
                config,
                new AgentScopedInstructionDiscoveryRequest
                {
                    Probes = [new AgentScopedInstructionProbe { Path = ".sunder-secure-quarantine-stale", IsDirectory = true, FollowFinalSymbolicLink = true }],
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task NonRecursiveNonemptyDirectoryDelete_FailsWithoutHidingDirectory()
    {
        var workspace = Path.Combine(_root, "workspace");
        var directory = Path.Combine(workspace, "nonempty");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "keep.txt"), "keep");
        var config = new LocalExecutionRuntimeConfig([workspace], workspace);

        var result = await LocalFileSystemExecutor.DeleteFileAsync(
            config,
            new AgentFileDeleteRequest { Path = "nonempty", Recursive = false },
            false,
            CancellationToken.None);

        Assert.Equal("recursive-directory-delete-required", result.ErrorCode);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(directory, "keep.txt")));
    }

    [Fact]
    public async Task ContentWrites_DoNotRequireDeletionQuarantineCapacity()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        for (var index = 0; index < HostSecurePathEngine.MaxQuarantineEntriesPerDirectory; index++)
        {
            File.WriteAllText(
                Path.Combine(workspace, $".sunder-secure-quarantine-{index:x8}"),
                string.Empty);
        }
        var target = Path.Combine(workspace, "target.txt");
        await File.WriteAllTextAsync(target, "old");
        var config = new LocalExecutionRuntimeConfig([workspace], workspace);

        using var before = HostSecurePathEngine.Capture([workspace], target);
        var result = await WriteContentAsync(
            config,
            new AgentFileWriteRequest { Path = "target.txt", Content = "new", Overwrite = true },
            CancellationToken.None);
        var created = await WriteContentAsync(config,
            new AgentFileWriteRequest { Path = "created.txt", Content = "created", Overwrite = false }, CancellationToken.None);
        Assert.False(result.IsError, result.Summary);
        Assert.False(created.IsError, created.Summary);
        Assert.Equal("new", await File.ReadAllTextAsync(target));
        Assert.Equal("created", await File.ReadAllTextAsync(Path.Combine(workspace, "created.txt")));
        using var after = HostSecurePathEngine.Capture([workspace], target);
        Assert.Equal(before.Binding.TargetIdentity, after.Binding.TargetIdentity);
        Assert.Equal(HostSecurePathEngine.MaxQuarantineEntriesPerDirectory,
            Directory.EnumerateFileSystemEntries(workspace).Count(path => HostSecurePathEngine.IsReservedName(Path.GetFileName(path))));
    }

    [Fact]
    public void QuarantineAccounting_CancelsAndRejectsCapacityBeforeMutation()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        using var root = HostSecurePathEngine.OpenRoot(workspace);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            root.Platform.ReserveMutationSlots(root.Handle, 1, cancellation.Token));
        Assert.False(HostMutationCoordinator.IsEntered(root.Handle.Identity));
        for (var index = 0; index < HostSecurePathEngine.MaxQuarantineEntriesPerDirectory; index++)
            File.WriteAllText(Path.Combine(workspace, $".sunder-secure-quarantine-{index:x8}"), string.Empty);
        Assert.Throws<LocalSecurePathException>(() => root.Platform.ReserveMutationSlots(root.Handle, 1, CancellationToken.None));
        Assert.False(HostMutationCoordinator.IsEntered(root.Handle.Identity));
    }

    [Fact]
    public void QuarantineAccounting_RejectsDirectoryBeyondScanBound()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        for (var index = 0; index <= HostSecurePathEngine.MaxQuarantineAccountingEntries; index++)
        {
            File.WriteAllText(Path.Combine(workspace, $"entry-{index:D5}"), string.Empty);
        }
        using var root = HostSecurePathEngine.OpenRoot(workspace);

        var exception = Assert.Throws<LocalSecurePathException>(() =>
            root.Platform.ReserveMutationSlots(root.Handle, 1, CancellationToken.None));

        Assert.Contains(
            HostSecurePathEngine.MaxQuarantineAccountingEntries.ToString(),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuarantineCapacity_IsAtomicAcrossConcurrentDeletions()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        for (var index = 0; index < HostSecurePathEngine.MaxQuarantineEntriesPerDirectory - 1; index++)
        {
            File.WriteAllText(
                Path.Combine(workspace, $".sunder-secure-quarantine-{index:x8}"),
                string.Empty);
        }
        await File.WriteAllTextAsync(Path.Combine(workspace, "first.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(workspace, "second.txt"), "second");
        using var root = HostSecurePathEngine.OpenRoot(workspace);
        var results = await Task.WhenAll(Task.Run(() => Delete("first.txt")), Task.Run(() => Delete("second.txt")));
        Assert.Single(results, success => success);
        var remaining = Assert.Single(Directory.EnumerateFiles(workspace),
            path => !HostSecurePathEngine.IsReservedName(Path.GetFileName(path)));
        Assert.Contains(await File.ReadAllTextAsync(remaining), new[] { "first", "second" });
        Assert.True(
            Directory.EnumerateFileSystemEntries(workspace)
                .Count(path => HostSecurePathEngine.IsReservedName(Path.GetFileName(path)))
            <= HostSecurePathEngine.MaxQuarantineEntriesPerDirectory);
        Assert.False(HostMutationCoordinator.IsEntered(root.Handle.Identity));

        bool Delete(string name)
        {
            try
            {
                using var reservation = root.Platform.ReserveMutationSlots(root.Handle, 1, CancellationToken.None);
                using var expected = root.Platform.OpenChild(root.Handle, name, root.Volume, false);
                root.Platform.DeleteEntry(root.Handle, name, false, expected, null, reservation,
                    static () => { }, static () => { }, CancellationToken.None);
                return true;
            }
            catch (LocalSecurePathException exception)
            {
                Assert.Contains("quarantine exceeds", exception.Message, StringComparison.Ordinal);
                return false;
            }
        }
    }

    [Fact]
    public void DeleteRecoveryConflict_LeavesReplacementAtItsNameAndRetainsTheOriginal()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "target.txt");
        File.WriteAllText(path, "original");
        using var root = HostSecurePathEngine.OpenRoot(_root);
        using var expected = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        using var reservation = root.Platform.ReserveMutationSlots(root.Handle, 2, CancellationToken.None);
        LocalSecureApprovalLease? replacement = null;
        try
        {
            Assert.Throws<LocalSecureMutationRecoveryException>(() => root.Platform.DeleteEntry(root.Handle, "target.txt", false,
                expected, null, reservation, static () => { }, () =>
                {
                    File.WriteAllText(path, "unrelated replacement");
                    replacement = HostSecurePathEngine.Capture([_root], path);
                    throw new IOException("Injected deletion failure after a replacement arrived.");
                }, CancellationToken.None));
            Assert.NotNull(replacement);
            using var current = HostSecurePathEngine.Capture([_root], path);
            Assert.Equal(replacement.Binding.TargetIdentity, current.Binding.TargetIdentity);
            Assert.Equal("unrelated replacement", File.ReadAllText(path));
            var retainedPath = Assert.Single(Directory.EnumerateFiles(_root), entry => HostSecurePathEngine.IsQuarantineName(Path.GetFileName(entry)));
            using var retained = HostSecurePathEngine.OpenRoot(_root);
            using var original = retained.Platform.OpenChild(retained.Handle, Path.GetFileName(retainedPath), retained.Volume, false);
            Assert.Equal(expected.Identity, original.Identity);
            Assert.Equal("original", File.ReadAllText(retainedPath));
        }
        finally { replacement?.Dispose(); }
    }

    [Fact]
    public async Task GlobMatcher_CollapsesRecursiveWildcardsAndRejectsBacktrackingRegexFeatures()
    {
        Assert.True(LocalGlobMatcher.Matches("**/**/**/target.txt", "a/b/target.txt"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            LocalGlobMatcher.Matches("**/*", "a/b", cancellation.Token));

        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "value.txt"), "aa");
        var result = await LocalSecureFileSearch.ExecuteAsync(
            new LocalExecutionRuntimeConfig([workspace], workspace),
            new AgentFileSearchRequest { Path = ".", Kind = AgentFileSearchKind.Grep, Pattern = "(a)\\1", Literal = false, Hidden = false },
            false,
            approvedResourceReferences: null,
            CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Equal(AgentFileSearchErrorCodes.InvalidRequest, result.ErrorCode);
    }

    [Fact]
    public async Task GlobSearch_UsesOneAggregateStateBudget()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var name = new string('a', 200);
        for (var index = 0; index < 700; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, $"{index:D4}-{name}"), string.Empty);
        }

        var result = await LocalSecureFileSearch.ExecuteAsync(
            new LocalExecutionRuntimeConfig([workspace], workspace),
            new AgentFileSearchRequest { Path = ".", Kind = AgentFileSearchKind.Glob, Pattern = "????-" + new string('?', 199), Literal = false, Hidden = false },
            false,
            approvedResourceReferences: null,
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("aggregate", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [PlatformFact(TestPlatform.Unix, "Content editing retains the current file mode without replacing or copying the inode.")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task ContentUpdate_RetainsTheCurrentModeOnTheSameInode()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var path = Path.Combine(workspace, "mode.txt");
        await File.WriteAllTextAsync(path, "old");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var expectedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        var hook = new UnixModeHook(path, expectedMode);
        using var before = HostSecurePathEngine.Capture([workspace], path);

        var result = await LocalFileSystemExecutor.WriteFileAsync(
            new LocalExecutionRuntimeConfig([workspace], workspace),
            new AgentFileWriteRequest { Path = "mode.txt", Content = "new", Overwrite = true },
            false,
            CancellationToken.None,
            hooks: hook);

        Assert.False(result.IsError, result.Summary);
        Assert.Equal(expectedMode, File.GetUnixFileMode(path) & (UnixFileMode)0x0fff);
        using var after = HostSecurePathEngine.Capture([workspace], path);
        Assert.Equal(before.Binding.TargetIdentity, after.Binding.TargetIdentity);
    }

    [PlatformFact(TestPlatform.Unix, "This rollback fixture adds a POSIX hard link to the retained quarantine object.")]
    public async Task DeleteQuarantineRollbackFailure_ReturnsStableRecoveryRequiredError()
    {
        var workspace = Path.Combine(_root, "rollback-failure-workspace");
        Directory.CreateDirectory(workspace);
        var path = Path.Combine(workspace, "delete.txt");
        var alias = Path.Combine(workspace, "delete-alias.txt");
        await File.WriteAllTextAsync(path, "recoverable-original");
        var hook = new RollbackHardLinkHook(
            LocalSecureFileSystemCheckpoint.AfterValidationBeforeDelete,
            workspace,
            alias);
        var config = new LocalExecutionRuntimeConfig([workspace], workspace);

        var result = await LocalFileSystemExecutor.DeleteFileAsync(config,
            new AgentFileDeleteRequest { Path = Path.GetFileName(path), Recursive = false },
            false, CancellationToken.None, hooks: hook);

        Assert.True(hook.Invoked);
        Assert.Equal(FileOperation.StrictMutationRecoveryRequiredErrorCode, result.ErrorCode);
        Assert.Equal(FileOperation.StrictMutationRecoveryRequiredMessage, result.Summary);
        Assert.Equal("recoverable-original", await File.ReadAllTextAsync(path));
        Assert.Equal("recoverable-original", await File.ReadAllTextAsync(alias));
    }

    [Theory]
    [InlineData((int)LocalSecureFileSystemCheckpoint.BeforeContentUpdateOpen)]
    [InlineData((int)LocalSecureFileSystemCheckpoint.BeforeContentWrite)]
    public async Task PrecommitFailure_LeavesExactTargetUnchanged(
        int checkpointValue)
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var path = Path.Combine(workspace, "rollback.txt");
        await File.WriteAllTextAsync(path, "old");
        var checkpoint = (LocalSecureFileSystemCheckpoint)checkpointValue;
        var hook = new ThrowingHook(checkpoint);

        using var before = HostSecurePathEngine.Capture([workspace], path);
        var result = await WriteContentAsync(
            new LocalExecutionRuntimeConfig([workspace], workspace),
            new AgentFileWriteRequest
            {
                Path = "rollback.txt",
                Content = "new",
                Overwrite = true,
                ExpectedContentHash = FileOperation.ComputeContentHash("old"),
            },
            CancellationToken.None,
            hooks: hook);

        Assert.True(hook.Invoked);
        Assert.True(result.IsError);
        Assert.NotEqual(FileOperation.StrictMutationRecoveryRequiredErrorCode, result.ErrorCode);
        Assert.Equal("old", await File.ReadAllTextAsync(path));
        using var after = HostSecurePathEngine.Capture([workspace], path);
        Assert.Equal(before.Binding.TargetIdentity, after.Binding.TargetIdentity);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(workspace)
                .Where(file => HostSecurePathEngine.IsQuarantineName(Path.GetFileName(file)))
                .Select(File.ReadAllText),
            content => content == "old");
    }

    [Theory]
    [InlineData((int)LocalSecureFileSystemCheckpoint.AfterContentLength)]
    [InlineData((int)LocalSecureFileSystemCheckpoint.AfterContentFileSync)]
    public async Task CompletionFailure_ReportsVisibleModifiedStateAndRequiresAnExplicitRetry(
        int checkpointValue)
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var path = Path.Combine(workspace, "published-rollback.txt");
        await File.WriteAllTextAsync(path, "old-bytes");
        using var before = HostSecurePathEngine.Capture([workspace], path);
        var config = new LocalExecutionRuntimeConfig([workspace], workspace);
        var hook = new ThrowingHook((LocalSecureFileSystemCheckpoint)checkpointValue);

        var failed = await WriteContentAsync(
            config,
            new AgentFileWriteRequest { Path = "published-rollback.txt", Content = "NEW-SECRET", Overwrite = true },
            CancellationToken.None,
            hooks: hook);

        Assert.True(hook.Invoked);
        Assert.True(failed.IsError);
        Assert.Equal(FileOperation.StrictMutationRecoveryRequiredErrorCode, failed.ErrorCode);
        Assert.Equal("NEW-SECRET", await File.ReadAllTextAsync(path));
        using var after = HostSecurePathEngine.Capture([workspace], path);
        Assert.Equal(before.Binding.TargetIdentity, after.Binding.TargetIdentity);
        var listing = await LocalFileSystemExecutor.ReadFileAsync(
            config,
            new AgentFileReadRequest { Path = "." },
            false,
            CancellationToken.None);
        var search = await LocalSecureFileSearch.ExecuteAsync(
            config,
            new AgentFileSearchRequest { Path = ".", Kind = AgentFileSearchKind.Grep, Pattern = "NEW-SECRET", Literal = false, Hidden = true },
            false,
            approvedResourceReferences: null,
            CancellationToken.None);
        Assert.DoesNotContain("sunder-secure", listing.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(path, Assert.Single(search.Matches).Path);
        Assert.Equal([path], Directory.EnumerateFiles(workspace));

        var retry = await WriteContentAsync(
            config,
            new AgentFileWriteRequest { Path = "published-rollback.txt", Content = "retry", Overwrite = true,
                ExpectedContentHash = FileOperation.ComputeContentHash("NEW-SECRET") },
            CancellationToken.None);
        Assert.False(retry.IsError, retry.Summary);
        Assert.Equal("retry", await File.ReadAllTextAsync(path));
    }

    [PlatformFact(TestPlatform.Unix, "This fixture modifies the quarantined object; Windows denies independent writable opens through handle sharing.")]
    public async Task DeletePostQuarantineValidatorFailure_RestoresFreshlyValidatedTarget()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var path = Path.Combine(workspace, "validator.txt");
        await File.WriteAllTextAsync(path, "old");
        var hook = new QuarantineContentMutationHook(workspace, "changed-after-quarantine");

        var result = await LocalFileSystemExecutor.DeleteFileAsync(
            new LocalExecutionRuntimeConfig([workspace], workspace),
            new AgentFileDeleteRequest
            {
                Path = "validator.txt",
                Recursive = false,
                ExpectedContentHash = FileOperation.ComputeContentHash("old"),
            },
            false,
            CancellationToken.None,
            hooks: hook);

        Assert.True(hook.Invoked);
        Assert.Equal("file-content-changed", result.ErrorCode);
        Assert.Equal("changed-after-quarantine", await File.ReadAllTextAsync(path));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(workspace)
                .Where(file => HostSecurePathEngine.IsQuarantineName(Path.GetFileName(file)))
                .Select(File.ReadAllText),
            content => content == "changed-after-quarantine");
    }

    [Fact]
    public async Task DeleteCancellationAfterQuarantine_CompletesExactMutationWithoutStranding()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var path = Path.Combine(workspace, "cancel.txt");
        await File.WriteAllTextAsync(path, "old");
        using var before = HostSecurePathEngine.Capture([workspace], path);
        using var cancellation = new CancellationTokenSource();
        var hook = new CancellationHook(
            LocalSecureFileSystemCheckpoint.AfterValidationBeforeDelete,
            cancellation);
        var config = new LocalExecutionRuntimeConfig([workspace], workspace);

        var result = await LocalFileSystemExecutor.DeleteFileAsync(
                config,
                new AgentFileDeleteRequest { Path = "cancel.txt", Recursive = false, ExpectedContentHash = FileOperation.ComputeContentHash("old") },
                false,
                cancellation.Token,
                hooks: hook);

        Assert.True(hook.Invoked);
        Assert.False(result.IsError, result.Summary);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(File.Exists(path));
        var retainedPath = Assert.Single(Directory.EnumerateFiles(workspace));
        Assert.True(HostSecurePathEngine.IsQuarantineName(Path.GetFileName(retainedPath)));
        using var root = HostSecurePathEngine.OpenRoot(workspace);
        using var retained = root.Platform.OpenChild(root.Handle, Path.GetFileName(retainedPath), root.Volume, false);
        Assert.Equal(before.Binding.TargetIdentity, retained.Identity);
        Assert.Equal("old", await File.ReadAllTextAsync(retainedPath));
    }

    [PlatformFact(TestPlatform.Windows, "Public Windows content mutation preserves identity and deletion retains the exact recovery object.")]
    public async Task WindowsStructuredMutations_PreserveTheObjectAndOutsideSentinel()
    {
        Assert.True(LocalSecureNative.StrictMutationsAvailable);
        var workspace = Path.Combine(_root, "workspace");
        var outside = Path.Combine(_root, "outside.txt");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "target.txt"), "old");
        await File.WriteAllTextAsync(outside, "OUTSIDE");
        var config = new LocalExecutionRuntimeConfig([workspace], workspace);
        using var before = HostSecurePathEngine.Capture([workspace], Path.Combine(workspace, "target.txt"));

        var write = await LocalFileSystemExecutor.WriteFileAsync(
            config,
            new AgentFileWriteRequest { Path = "target.txt", Content = "new", Overwrite = true },
            false,
            CancellationToken.None);
        Assert.False(write.IsError, write.Summary);
        using var after = HostSecurePathEngine.Capture([workspace], Path.Combine(workspace, "target.txt"));
        Assert.Equal(before.Binding.TargetIdentity, after.Binding.TargetIdentity);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(workspace, "target.txt")));
        var delete = await LocalFileSystemExecutor.DeleteFileAsync(
            config,
            new AgentFileDeleteRequest { Path = "target.txt", Recursive = false },
            false,
            CancellationToken.None);

        Assert.False(delete.IsError, delete.Summary);
        Assert.False(File.Exists(Path.Combine(workspace, "target.txt")));
        Assert.Equal("OUTSIDE", await File.ReadAllTextAsync(outside));
        var retainedPath = Assert.Single(Directory.EnumerateFiles(workspace));
        Assert.True(HostSecurePathEngine.IsQuarantineName(Path.GetFileName(retainedPath)));
        using var root = HostSecurePathEngine.OpenRoot(workspace);
        using var retained = root.Platform.OpenChild(root.Handle, Path.GetFileName(retainedPath), root.Volume, false);
        Assert.Equal(before.Binding.TargetIdentity, retained.Identity);
        Assert.Equal("new", await File.ReadAllTextAsync(retainedPath));
    }

    private static ValueTask<AgentFileMutationResult> WriteContentAsync(LocalExecutionRuntimeConfig config,
        AgentFileWriteRequest request, CancellationToken cancellationToken, ILocalSecureFileSystemHooks? hooks = null)
        => LocalFileSystemExecutor.WriteFileAsync(config, request, false, cancellationToken, hooks: hooks);

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

    private sealed class DisposableProbe : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private sealed class UnixModeHook(string path, UnixFileMode mode) : ILocalSecureFileSystemHooks
    {
        public void OnCheckpoint(LocalSecureFileSystemCheckpoint checkpoint, string parentPath, string? entryName)
        {
            if (checkpoint == LocalSecureFileSystemCheckpoint.BeforeContentUpdateOpen)
            {
                File.SetUnixFileMode(path, mode);
            }
        }
    }

    private sealed class CancellationHook(
        LocalSecureFileSystemCheckpoint checkpoint,
        CancellationTokenSource cancellation) : ILocalSecureFileSystemHooks
    {
        public bool Invoked { get; private set; }

        public void OnCheckpoint(LocalSecureFileSystemCheckpoint current, string parentPath, string? entryName)
        {
            if (!Invoked && current == checkpoint)
            {
                Invoked = true;
                cancellation.Cancel();
            }
        }
    }

    private sealed class ThrowingHook(LocalSecureFileSystemCheckpoint checkpoint) : ILocalSecureFileSystemHooks
    {
        public bool Invoked { get; private set; }

        public void OnCheckpoint(LocalSecureFileSystemCheckpoint current, string parentPath, string? entryName)
        {
            if (!Invoked && current == checkpoint)
            {
                Invoked = true;
                throw new LocalSecurePathException($"Injected {checkpoint} failure.");
            }
        }
    }

    private sealed class QuarantineContentMutationHook(string parentPath, string content) : ILocalSecureFileSystemHooks
    {
        public bool Invoked { get; private set; }

        public void OnCheckpoint(LocalSecureFileSystemCheckpoint checkpoint, string currentParentPath, string? entryName)
        {
            if (Invoked || checkpoint != LocalSecureFileSystemCheckpoint.AfterValidationBeforeDelete)
            {
                return;
            }
            Invoked = true;
            var quarantine = Directory.EnumerateFiles(parentPath)
                .Single(path => HostSecurePathEngine.IsQuarantineName(Path.GetFileName(path))
                                && File.ReadAllText(path) == "old");
            File.WriteAllText(quarantine, content);
        }
    }

    private sealed class RollbackHardLinkHook(
        LocalSecureFileSystemCheckpoint checkpoint,
        string parentPath,
        string aliasPath) : ILocalSecureFileSystemHooks
    {
        public bool Invoked { get; private set; }

        public void OnCheckpoint(
            LocalSecureFileSystemCheckpoint current,
            string currentParentPath,
            string? entryName)
        {
            if (Invoked || current != checkpoint)
            {
                return;
            }

            Invoked = true;
            var quarantine = Assert.Single(
                Directory.EnumerateFiles(parentPath),
                path => HostSecurePathEngine.IsQuarantineName(Path.GetFileName(path))
                        && File.ReadAllText(path) == "recoverable-original");
            TestFileSystemCapabilities.CreateHardLink(aliasPath, quarantine);
            throw new LocalSecurePathException("Injected failure requiring quarantine rollback.");
        }
    }
}
