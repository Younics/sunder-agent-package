using Sunder.Agent.Execution.Common;
using System.Runtime.Versioning;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Execution.Local;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class StrictMutationReauditTests : IDisposable
{
    private readonly string _root = Path.Combine(
        OperatingSystem.IsMacOS() ? "/private" + Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) : Path.GetTempPath(),
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
            new AgentFileWriteRequest(approvedPath, "approved", Overwrite: false),
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
            new AgentFileWriteRequest(racedPath, "must-not-replace", Overwrite: false),
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
    public async Task MissingFinalOutsideApproval_RejectsReplacedParentIdentity()
    {
        var workspace = Path.Combine(_root, "workspace");
        var outside = Path.Combine(_root, "outside");
        var approvedParent = Path.Combine(outside, "approved-parent");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(approvedParent);
        var config = new LocalExecutionRuntimeConfig([workspace], workspace);
        var approvedPath = Path.Combine(approvedParent, "target.txt");
        using var approved = LocalResourceAuthorityTestContext.Approve(config, approvedPath, "files.mutate");

        Directory.Move(approvedParent, Path.Combine(outside, "original-parent"));
        Directory.CreateDirectory(approvedParent);
        var rejected = await LocalFileSystemExecutor.WriteFileAsync(
            config,
            new AgentFileWriteRequest(approvedPath, "must-not-write", Overwrite: false),
            allowOutsideConfiguredScope: true,
            CancellationToken.None,
            approvedResourceReferences: approved.Context.ApprovedResourceReferences,
            authorizationContext: approved.Context,
            resourceReferences: approved.ResourceReferences);

        Assert.True(rejected.IsError);
        Assert.Equal(LocalResourceReference.ReapprovalRequiredErrorCode, rejected.ErrorCode);
        Assert.False(File.Exists(approvedPath));
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
            new AgentFileReadRequest("."),
            false,
            CancellationToken.None);
        var grep = await LocalSecureFileSearch.ExecuteAsync(
            config,
            new AgentFileSearchRequest(".", AgentFileSearchKind.Grep, "secret"),
            false,
            approvedResourceReferences: null,
            CancellationToken.None);
        var glob = await LocalSecureFileSearch.ExecuteAsync(
            config,
            new AgentFileSearchRequest(".", AgentFileSearchKind.Glob, "**/*"),
            false,
            approvedResourceReferences: null,
            CancellationToken.None);
        var direct = await LocalFileSystemExecutor.ReadFileAsync(
            config,
            new AgentFileReadRequest(".sunder-secure-temp-stale"),
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
                new AgentScopedInstructionDiscoveryRequest(
                    [new AgentScopedInstructionProbe(".sunder-secure-quarantine-stale", IsDirectory: true)]),
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
            new AgentFileDeleteRequest("nonempty", Recursive: false),
            false,
            CancellationToken.None);

        Assert.Equal("recursive-directory-delete-required", result.ErrorCode);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(directory, "keep.txt")));
    }

    [Fact]
    public async Task QuarantineCapacity_IsReservedBeforeTemporaryCreation()
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

        var result = await LocalFileSystemExecutor.WriteFileAsync(
            config,
            new AgentFileWriteRequest("target.txt", "new"),
            false,
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("old", await File.ReadAllTextAsync(target));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(workspace),
            path => HostSecurePathEngine.IsTemporaryName(Path.GetFileName(path)));
    }

    [Fact]
    public void QuarantineAccounting_IsCancellableAndBounded()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        using var root = HostSecurePathEngine.OpenRoot(workspace);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            root.Platform.ReserveMutationSlots(root.Handle, 1, cancellation.Token));
    }

    [Fact]
    public void QuarantineAccounting_RejectsDirectoryBeyondScanBound()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

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
    public async Task QuarantineCapacity_IsAtomicAcrossConcurrentMutations()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        const int requiredSlots = 4;
        for (var index = 0; index < HostSecurePathEngine.MaxQuarantineEntriesPerDirectory - requiredSlots; index++)
        {
            File.WriteAllText(
                Path.Combine(workspace, $".sunder-secure-quarantine-{index:x8}"),
                string.Empty);
        }
        await File.WriteAllTextAsync(Path.Combine(workspace, "first.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(workspace, "second.txt"), "second");
        var config = new LocalExecutionRuntimeConfig([workspace], workspace);
        using var root = HostSecurePathEngine.OpenRoot(workspace);

        var results = await Task.WhenAll(
            LocalFileSystemExecutor.WriteFileAsync(
                config,
                new AgentFileWriteRequest("first.txt", "changed-first"),
                false,
                CancellationToken.None).AsTask(),
            LocalFileSystemExecutor.WriteFileAsync(
                config,
                new AgentFileWriteRequest("second.txt", "changed-second"),
                false,
                CancellationToken.None).AsTask());

        Assert.Single(results, result => !result.IsError);
        Assert.True(
            Directory.EnumerateFileSystemEntries(workspace)
                .Count(path => HostSecurePathEngine.IsReservedName(Path.GetFileName(path)))
            <= HostSecurePathEngine.MaxQuarantineEntriesPerDirectory);
        Assert.False(HostMutationCoordinator.IsEntered(root.Handle.Identity));
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
            new AgentFileSearchRequest(".", AgentFileSearchKind.Grep, "(a)\\1"),
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
            new AgentFileSearchRequest(".", AgentFileSearchKind.Glob, "????-" + new string('?', 199)),
            false,
            approvedResourceReferences: null,
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("aggregate", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Overwrite_CopiesMetadataFromFreshlyQuarantinedHandle()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var path = Path.Combine(workspace, "mode.txt");
        await File.WriteAllTextAsync(path, "old");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var expectedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        var hook = new UnixModeHook(path, expectedMode);

        var result = await LocalFileSystemExecutor.WriteFileAsync(
            new LocalExecutionRuntimeConfig([workspace], workspace),
            new AgentFileWriteRequest("mode.txt", "new"),
            false,
            CancellationToken.None,
            hooks: hook);

        Assert.False(result.IsError, result.Summary);
        Assert.Equal(expectedMode, File.GetUnixFileMode(path) & (UnixFileMode)0x0fff);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuarantineRollbackFailure_ReturnsStableRecoveryRequiredError(bool delete)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var workspace = Path.Combine(_root, "rollback-failure-workspace");
        Directory.CreateDirectory(workspace);
        var path = Path.Combine(workspace, delete ? "delete.txt" : "write.txt");
        var alias = Path.Combine(workspace, delete ? "delete-alias.txt" : "write-alias.txt");
        await File.WriteAllTextAsync(path, "recoverable-original");
        var hook = new RollbackHardLinkHook(
            delete
                ? LocalSecureFileSystemCheckpoint.AfterValidationBeforeDelete
                : LocalSecureFileSystemCheckpoint.AfterQuarantineBeforeMetadata,
            workspace,
            alias);
        var config = new LocalExecutionRuntimeConfig([workspace], workspace);

        var result = delete
            ? await LocalFileSystemExecutor.DeleteFileAsync(
                config,
                new AgentFileDeleteRequest(Path.GetFileName(path)),
                false,
                CancellationToken.None,
                hooks: hook)
            : await LocalFileSystemExecutor.WriteFileAsync(
                config,
                new AgentFileWriteRequest(Path.GetFileName(path), "new-content"),
                false,
                CancellationToken.None,
                hooks: hook);

        Assert.True(hook.Invoked);
        Assert.Equal(FileOperation.StrictMutationRecoveryRequiredErrorCode, result.ErrorCode);
        Assert.Equal(FileOperation.StrictMutationRecoveryRequiredMessage, result.Summary);
        Assert.Equal("recoverable-original", await File.ReadAllTextAsync(path));
        Assert.Equal("recoverable-original", await File.ReadAllTextAsync(alias));
    }

    [Theory]
    [InlineData((int)LocalSecureFileSystemCheckpoint.AfterQuarantineBeforeMetadata)]
    [InlineData((int)LocalSecureFileSystemCheckpoint.BeforePostQuarantineSync)]
    public async Task PostQuarantineMetadataOrSyncFailure_RestoresExactTarget(
        int checkpointValue)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var path = Path.Combine(workspace, "rollback.txt");
        await File.WriteAllTextAsync(path, "old");
        var checkpoint = (LocalSecureFileSystemCheckpoint)checkpointValue;
        var hook = new ThrowingHook(checkpoint);

        var result = await LocalFileSystemExecutor.WriteFileAsync(
            new LocalExecutionRuntimeConfig([workspace], workspace),
            new AgentFileWriteRequest("rollback.txt", "new")
            {
                ExpectedContentHash = FileOperation.ComputeContentHash("old"),
            },
            false,
            CancellationToken.None,
            hooks: hook);

        Assert.True(hook.Invoked);
        Assert.True(result.IsError);
        Assert.Equal("old", await File.ReadAllTextAsync(path));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(workspace)
                .Where(file => HostSecurePathEngine.IsQuarantineName(Path.GetFileName(file)))
                .Select(File.ReadAllText),
            content => content == "old");
    }

    [Theory]
    [InlineData((int)LocalSecureFileSystemCheckpoint.AfterPublishedFileSync)]
    [InlineData((int)LocalSecureFileSystemCheckpoint.AfterPublishedParentSync)]
    public async Task PostPublicationSyncFailure_DurablyRestoresOriginalAndAllowsCleanRetry(
        int checkpointValue)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var path = Path.Combine(workspace, "published-rollback.txt");
        await File.WriteAllTextAsync(path, "old-bytes");
        var oldMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(path, oldMode);
        var config = new LocalExecutionRuntimeConfig([workspace], workspace);
        var hook = new ThrowingHook((LocalSecureFileSystemCheckpoint)checkpointValue);

        var failed = await LocalFileSystemExecutor.WriteFileAsync(
            config,
            new AgentFileWriteRequest("published-rollback.txt", "NEW-SECRET"),
            false,
            CancellationToken.None,
            hooks: hook);

        Assert.True(hook.Invoked);
        Assert.True(failed.IsError);
        Assert.NotEqual(FileOperation.StrictMutationRecoveryRequiredErrorCode, failed.ErrorCode);
        Assert.Equal("old-bytes", await File.ReadAllTextAsync(path));
        Assert.Equal(oldMode, File.GetUnixFileMode(path) & (UnixFileMode)0x0fff);
        var listing = await LocalFileSystemExecutor.ReadFileAsync(
            config,
            new AgentFileReadRequest("."),
            false,
            CancellationToken.None);
        var search = await LocalSecureFileSearch.ExecuteAsync(
            config,
            new AgentFileSearchRequest(".", AgentFileSearchKind.Grep, "NEW-SECRET"),
            false,
            approvedResourceReferences: null,
            CancellationToken.None);
        Assert.DoesNotContain("sunder-secure", listing.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(search.Matches);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(workspace)
                .Where(file => HostSecurePathEngine.IsReservedName(Path.GetFileName(file)))
                .Select(File.ReadAllText),
            content => content.Contains("NEW-SECRET", StringComparison.Ordinal));

        var retry = await LocalFileSystemExecutor.WriteFileAsync(
            config,
            new AgentFileWriteRequest("published-rollback.txt", "retry"),
            false,
            CancellationToken.None);
        Assert.False(retry.IsError, retry.Summary);
        Assert.Equal("retry", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task PostPublicationRollbackFailure_ReturnsStableFatalErrorAndHidesSensitiveState()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var path = Path.Combine(workspace, "fatal-rollback.txt");
        await File.WriteAllTextAsync(path, "old-recoverable");
        var config = new LocalExecutionRuntimeConfig([workspace], workspace);
        var hook = new ThrowingRecoveryHook();

        var result = await LocalFileSystemExecutor.WriteFileAsync(
            config,
            new AgentFileWriteRequest("fatal-rollback.txt", "FATAL-NEW-SECRET"),
            false,
            CancellationToken.None,
            hooks: hook);

        Assert.Equal(FileOperation.StrictMutationRecoveryRequiredErrorCode, result.ErrorCode);
        Assert.Equal(FileOperation.StrictMutationRecoveryRequiredMessage, result.Summary);
        Assert.True(hook.PublishFailureInvoked);
        Assert.True(hook.RecoveryFailureInvoked);
        var listing = await LocalFileSystemExecutor.ReadFileAsync(
            config,
            new AgentFileReadRequest("."),
            false,
            CancellationToken.None);
        var search = await LocalSecureFileSearch.ExecuteAsync(
            config,
            new AgentFileSearchRequest(".", AgentFileSearchKind.Grep, "FATAL-NEW-SECRET|old-recoverable"),
            false,
            approvedResourceReferences: null,
            CancellationToken.None);
        Assert.DoesNotContain("sunder-secure", listing.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(search.Matches);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(workspace)
                .Where(file => HostSecurePathEngine.IsReservedName(Path.GetFileName(file)))
                .Select(File.ReadAllText),
            content => content.Contains("FATAL-NEW-SECRET", StringComparison.Ordinal));
        Assert.Contains(
            Directory.EnumerateFiles(workspace)
                .Where(file => HostSecurePathEngine.IsQuarantineName(Path.GetFileName(file)))
                .Select(File.ReadAllText),
            content => content == "old-recoverable");
    }

    [Fact]
    public async Task PostQuarantineValidatorFailure_RestoresFreshlyValidatedTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var path = Path.Combine(workspace, "validator.txt");
        await File.WriteAllTextAsync(path, "old");
        var hook = new QuarantineContentMutationHook(workspace, "changed-after-quarantine");

        var result = await LocalFileSystemExecutor.WriteFileAsync(
            new LocalExecutionRuntimeConfig([workspace], workspace),
            new AgentFileWriteRequest("validator.txt", "new")
            {
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterQuarantine_CompletesExactMutationWithoutStranding(bool delete)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var path = Path.Combine(workspace, "cancel.txt");
        await File.WriteAllTextAsync(path, "old");
        using var cancellation = new CancellationTokenSource();
        var hook = new CancellationHook(
            delete
                ? LocalSecureFileSystemCheckpoint.AfterValidationBeforeDelete
                : LocalSecureFileSystemCheckpoint.AfterValidationBeforePublish,
            cancellation);
        var config = new LocalExecutionRuntimeConfig([workspace], workspace);

        var result = delete
            ? await LocalFileSystemExecutor.DeleteFileAsync(
                config,
                new AgentFileDeleteRequest("cancel.txt") { ExpectedContentHash = FileOperation.ComputeContentHash("old") },
                false,
                cancellation.Token,
                hooks: hook)
            : await LocalFileSystemExecutor.WriteFileAsync(
                config,
                new AgentFileWriteRequest("cancel.txt", "new") { ExpectedContentHash = FileOperation.ComputeContentHash("old") },
                false,
                cancellation.Token,
                hooks: hook);

        Assert.True(hook.Invoked);
        Assert.False(result.IsError, result.Summary);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(delete, !File.Exists(path));
        if (!delete)
        {
            Assert.Equal("new", await File.ReadAllTextAsync(path));
        }
    }

    [Fact]
    public async Task PostValidationPrePublishReplacement_IsNeverOverwritten()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var path = Path.Combine(workspace, "publish-race.txt");
        await File.WriteAllTextAsync(path, "approved");
        var hook = new PublishReplacementHook(path);

        var result = await LocalFileSystemExecutor.WriteFileAsync(
            new LocalExecutionRuntimeConfig([workspace], workspace),
            new AgentFileWriteRequest("publish-race.txt", "new"),
            false,
            CancellationToken.None,
            hooks: hook);

        Assert.True(hook.Invoked);
        Assert.True(result.IsError);
        Assert.Equal("approved", await File.ReadAllTextAsync(path));
        Assert.Contains(
            Directory.EnumerateFiles(workspace)
                .Where(file => HostSecurePathEngine.IsQuarantineName(Path.GetFileName(file)))
                .Select(File.ReadAllText),
            content => content == "ATTACKER");
    }

    [Fact]
    public async Task WindowsStructuredMutations_FailClosedWithoutTemporaryOrOutsideChanges()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(LocalSecureNative.StrictMutationsAvailable);
            return;
        }
        Assert.False(LocalSecureNative.StrictMutationsAvailable);
        var workspace = Path.Combine(_root, "workspace");
        var outside = Path.Combine(_root, "outside.txt");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "target.txt"), "old");
        await File.WriteAllTextAsync(outside, "OUTSIDE");
        var config = new LocalExecutionRuntimeConfig([workspace], workspace);

        var write = await LocalFileSystemExecutor.WriteFileAsync(
            config,
            new AgentFileWriteRequest("target.txt", "new"),
            false,
            CancellationToken.None);
        var delete = await LocalFileSystemExecutor.DeleteFileAsync(
            config,
            new AgentFileDeleteRequest("target.txt"),
            false,
            CancellationToken.None);

        Assert.Equal(FileOperation.StrictPlatformMutationUnavailableErrorCode, write.ErrorCode);
        Assert.Equal(FileOperation.StrictPlatformMutationUnavailableErrorCode, delete.ErrorCode);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(workspace, "target.txt")));
        Assert.Equal("OUTSIDE", await File.ReadAllTextAsync(outside));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(workspace),
            path => HostSecurePathEngine.IsReservedName(Path.GetFileName(path)));
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
            if (checkpoint == LocalSecureFileSystemCheckpoint.BeforePublish)
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

    private sealed class PublishReplacementHook(string path) : ILocalSecureFileSystemHooks
    {
        public bool Invoked { get; private set; }

        public void OnCheckpoint(LocalSecureFileSystemCheckpoint checkpoint, string parentPath, string? entryName)
        {
            if (!Invoked && checkpoint == LocalSecureFileSystemCheckpoint.AfterValidationBeforePublish)
            {
                Invoked = true;
                File.WriteAllText(path, "ATTACKER");
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

    private sealed class ThrowingRecoveryHook : ILocalSecureFileSystemHooks
    {
        public bool PublishFailureInvoked { get; private set; }

        public bool RecoveryFailureInvoked { get; private set; }

        public void OnCheckpoint(LocalSecureFileSystemCheckpoint checkpoint, string parentPath, string? entryName)
        {
            if (!PublishFailureInvoked && checkpoint == LocalSecureFileSystemCheckpoint.AfterPublishedFileSync)
            {
                PublishFailureInvoked = true;
                throw new LocalSecurePathException("Injected post-publication failure.");
            }
            if (!RecoveryFailureInvoked && checkpoint == LocalSecureFileSystemCheckpoint.BeforePostPublicationRestore)
            {
                RecoveryFailureInvoked = true;
                throw new LocalSecurePathException("Injected recovery failure.");
            }
        }
    }

    private sealed class QuarantineContentMutationHook(string parentPath, string content) : ILocalSecureFileSystemHooks
    {
        public bool Invoked { get; private set; }

        public void OnCheckpoint(LocalSecureFileSystemCheckpoint checkpoint, string currentParentPath, string? entryName)
        {
            if (Invoked || checkpoint != LocalSecureFileSystemCheckpoint.BeforePostQuarantineSync)
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
            using var root = HostSecurePathEngine.OpenRoot(parentPath);
            LocalUnixNative.Link(
                root.Handle.Handle.DangerousGetHandle().ToInt32(),
                Path.GetFileName(quarantine),
                Path.GetFileName(aliasPath));
            throw new LocalSecurePathException("Injected failure requiring quarantine rollback.");
        }
    }
}
