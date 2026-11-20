extern alias LocalWorker;

using LocalWorker::Sunder.Package.Agent.Execution.Local;
using LocalModels = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class OperationCreatedParentTests : IDisposable
{
    private readonly string _root = Path.Combine(TemporaryDirectory.GetPhysicalTemporaryRoot(),
        "sunder-operation-parents", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PreapprovedSiblingPathsReuseOnlyTheirOwnCreatedAncestorsWithoutChangingClaims()
    {
        Directory.CreateDirectory(_root);
        var config = new LocalExecutionRuntimeConfig([_root], _root);
        using var references = new LocalResourceReference();
        using var first = LocalResourceAuthorityTestContext.Approve(config, "new/a.txt", "files.mutate", resourceReferences: references);
        var second = ApproveSibling(config, first.Context, references, "new/sub/b.txt");
        var original = Assert.Single(second.ApprovedResourceClaims).RootIdentityChainFingerprint;
        using var foreign = LocalResourceAuthorityTestContext.Approve(config, "new/sub/foreign.txt", "files.mutate", resourceReferences: references);

        Assert.False((await Write(config, first.Context, references, "new/a.txt")).IsError);
        var rejected = await Write(config, foreign.Context, references, "new/sub/foreign.txt");
        Assert.True(rejected.IsError);
        Assert.Equal(LocalResourceReference.ReapprovalRequiredErrorCode, rejected.ErrorCode);
        var written = await Write(config, second, references, "new/sub/b.txt");
        Assert.False(written.IsError, written.Summary);
        Assert.Equal("approved", File.ReadAllText(Path.Combine(_root, "new/sub/b.txt")));
        Assert.Equal(original, Assert.Single(second.ApprovedResourceClaims).RootIdentityChainFingerprint);
        Assert.False(File.Exists(Path.Combine(_root, "new/sub/foreign.txt")));
    }

    [Fact]
    public async Task PostMutationReceipt_KeepsOwnedParentsBorrowableAfterPlanningAuthorityIsReleased()
    {
        Directory.CreateDirectory(_root);
        var config = new LocalExecutionRuntimeConfig([_root], _root);
        using var references = new LocalResourceReference();
        using var approved = LocalResourceAuthorityTestContext.Approve(config, "new/sub/a.txt", "files.mutate", resourceReferences: references);
        var context = approved.Context with { CapturePostMutationResource = true };
        var written = await Write(config, context, references, "new/sub/a.txt");
        Assert.False(written.IsError, written.Summary);
        var receipt = Assert.IsType<LocalModels.AgentResolvedResource>(written.PostMutationResource);
        Assert.NotEmpty(receipt.AuthorityReferences);
        foreach (var capability in approved.Context.ApprovedResourceCapabilities) references.Revoke(capability);
        var compensation = context with
        {
            ApprovedResourceReferences = [receipt.CanonicalReference],
            ApprovedResourceClaims = [receipt.ResourceClaim!],
            ApprovedResourceCapabilities = receipt.AuthorityReferences,
            CapturePostMutationResource = false,
        };
        var wrongActivation = compensation with
        {
            ResourceOperation = compensation.ResourceOperation! with { AuthorityActivationId = Guid.NewGuid().ToString("N") },
        };
        Assert.False(LocalSecurePathEngine.HasCurrentOutsideCapabilities(config, wrongActivation, references));
        var rejected = await Delete(wrongActivation);
        Assert.Equal(LocalResourceReference.ReapprovalRequiredErrorCode, rejected.ErrorCode);
        Assert.Equal("approved", File.ReadAllText(Path.Combine(_root, "new/sub/a.txt")));
        Assert.True(LocalSecurePathEngine.HasCurrentOutsideCapabilities(config, compensation, references));
        var deleted = await Delete(compensation);
        Assert.False(deleted.IsError, deleted.Summary);
        Assert.False(File.Exists(Path.Combine(_root, "new/sub/a.txt")));
        foreach (var capability in receipt.AuthorityReferences.Concat(receipt.DeleteAuthorityReferences)) references.Revoke(capability);
        var retained = Assert.Single(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
        Assert.True(HostSecurePathEngine.IsQuarantineName(Path.GetFileName(retained)));
        Assert.Equal("approved", File.ReadAllText(retained));

        ValueTask<LocalModels.AgentFileMutationResult> Delete(LocalModels.AgentExecutionTargetContext deleteContext)
            => LocalFileSystemExecutor.DeleteFileAsync(config, new LocalModels.AgentFileDeleteRequest
            {
                Path = "new/sub/a.txt",
                Recursive = false,
                ExpectedContentHash = FileOperation.ComputeContentHash("approved"),
            }, true, CancellationToken.None, authorizationContext: deleteContext, resourceReferences: references);
    }

    [Fact]
    public Task IndependentlyCreatedAncestorIsNeverAdopted()
        => AssertForeignAncestorIsRejected(replaceOwnedParent: false);

    [PlatformFact(TestPlatform.Unix, "This fixture reparents a retained ancestor; Windows instead denies reparenting through handle sharing.")]
    public Task ReplacedOwnedAncestorIsNeverAdopted()
        => AssertForeignAncestorIsRejected(replaceOwnedParent: true);

    private async Task AssertForeignAncestorIsRejected(bool replaceOwnedParent)
    {
        Directory.CreateDirectory(_root);
        var config = new LocalExecutionRuntimeConfig([_root], _root);
        using var references = new LocalResourceReference();
        using var first = LocalResourceAuthorityTestContext.Approve(config, "new/a.txt", "files.mutate", resourceReferences: references);
        var second = ApproveSibling(config, first.Context, references, "new/sub/b.txt");
        Assert.False((await Write(config, first.Context, references, "new/a.txt")).IsError);
        if (replaceOwnedParent)
        {
            Directory.Move(Path.Combine(_root, "new"), Path.Combine(_root, "retained-original"));
            Directory.CreateDirectory(Path.Combine(_root, "new"));
        }
        else Directory.CreateDirectory(Path.Combine(_root, "new/sub"));
        var rejected = await Write(config, second, references, "new/sub/b.txt");
        Assert.True(rejected.IsError);
        Assert.Equal(LocalResourceReference.ReapprovalRequiredErrorCode, rejected.ErrorCode);
        Assert.False(File.Exists(Path.Combine(_root, "new/sub/b.txt")));
    }

    [Fact]
    public async Task PrecommitCancellation_CreatesNoFileAndRetiresOnlyOwnedEmptyParentsAfterRelease()
    {
        Directory.CreateDirectory(_root);
        var config = new LocalExecutionRuntimeConfig([_root], _root);
        using var references = new LocalResourceReference();
        using var approved = LocalResourceAuthorityTestContext.Approve(config, "new/sub/a.txt", "files.mutate", resourceReferences: references);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await LocalFileSystemExecutor.WriteFileAsync(config,
            new LocalModels.AgentFileWriteRequest { Path = "new/sub/a.txt", Content = "canceled", Overwrite = false }, true,
            CancellationToken.None, faultInjector: new CancelBeforeContent(), authorizationContext: approved.Context, resourceReferences: references));
        Assert.True(Directory.Exists(Path.Combine(_root, "new/sub")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "new/sub")));
        foreach (var capability in approved.Context.ApprovedResourceCapabilities) references.Revoke(capability);
        Assert.False(Directory.Exists(Path.Combine(_root, "new/sub")));
        // The deletion backend retains the retired empty directory. Its parent
        // is now nonempty, so cleanup must not recurse through that parent.
        var retained = Assert.Single(Directory.EnumerateDirectories(Path.Combine(_root, "new")));
        Assert.True(HostSecurePathEngine.IsQuarantineName(Path.GetFileName(retained)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(retained));
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyOwnedParentIsRetiredOnlyAfterTheLastBorrowAndExpiredCapabilitiesCannotBorrow(bool expires)
    {
        Directory.CreateDirectory(_root);
        var config = new LocalExecutionRuntimeConfig([_root], _root);
        var now = DateTimeOffset.UtcNow;
        using var references = new LocalResourceReference(TimeSpan.FromMinutes(30), () => now);
        using var approved = LocalResourceAuthorityTestContext.Approve(config, "new/a.txt", "files.mutate", resourceReferences: references);
        using (var authority = LocalSecurePathEngine.ResolveApprovedAuthority(config, Path.Combine(_root, "new/a.txt"), "new/a.txt", true,
                   null, approved.Context, resourceReferences: references))
        using (var session = authority!.OpenSession(true, null, CancellationToken.None))
        {
            Assert.True(Directory.Exists(Path.Combine(_root, "new")));
            if (expires) now += TimeSpan.FromHours(1);
            else foreach (var capability in approved.Context.ApprovedResourceCapabilities) references.Revoke(capability);
            Assert.Null(references.BorrowDirectoryOperation(approved.Context));
            // An in-flight invocation owns a borrow, even after release/expiry.
            Assert.True(Directory.Exists(Path.Combine(_root, "new")));
        }
        Assert.False(Directory.Exists(Path.Combine(_root, "new")));
        // Secure deletion retires the name; it does not bypass the platform's
        // existing quarantine-only physical-deletion policy.
        var retired = Assert.Single(Directory.EnumerateDirectories(_root));
        Assert.StartsWith(".sunder-secure-quarantine-", Path.GetFileName(retired));
        Assert.Empty(Directory.EnumerateFileSystemEntries(retired));
    }

    [Fact]
    public async Task ConcurrentApprovedSiblingsShareCreatedParentsWithoutAdoptingForeignAncestors()
    {
        Directory.CreateDirectory(_root);
        var config = new LocalExecutionRuntimeConfig([_root], _root);
        using var references = new LocalResourceReference();
        using var first = LocalResourceAuthorityTestContext.Approve(config, "new/a.txt", "files.mutate", resourceReferences: references);
        var second = ApproveSibling(config, first.Context, references, "new/b.txt");
        using var barrier = new Barrier(2);
        var hooks = new BeforeSharedParent(barrier);
        var results = await Task.WhenAll(Task.Run(async () => await WriteWithHooks(first.Context, "new/a.txt")),
            Task.Run(async () => await WriteWithHooks(second, "new/b.txt")));
        Assert.All(results, result => Assert.False(result.IsError, result.Summary));
        Assert.All(new[] { "a.txt", "b.txt" }, path => Assert.Equal("approved", File.ReadAllText(Path.Combine(_root, "new", path))));

        ValueTask<LocalModels.AgentFileMutationResult> WriteWithHooks(LocalModels.AgentExecutionTargetContext context, string path)
            => LocalFileSystemExecutor.WriteFileAsync(config, new LocalModels.AgentFileWriteRequest { Path = path, Content = "approved", Overwrite = false },
                true, CancellationToken.None, hooks: hooks, authorizationContext: context, resourceReferences: references);
    }

    private sealed class BeforeSharedParent(Barrier barrier) : ILocalSecureFileSystemHooks
    {
        public void OnCheckpoint(LocalSecureFileSystemCheckpoint checkpoint, string path, string? childName = null)
        {
            if (checkpoint == LocalSecureFileSystemCheckpoint.BeforeOpen && childName == "new")
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)));
        }
    }

    private static LocalModels.AgentExecutionTargetContext ApproveSibling(LocalExecutionRuntimeConfig config,
        LocalModels.AgentExecutionTargetContext original, LocalResourceReference references, string path)
    {
        var operation = original.ResourceOperation! with { ResourceIndex = 1, CanIssueOutsideAuthority = true };
        var resource = LocalResourceResolver.ResolveFileResource(config, path, true, original with { ResourceOperation = operation }, references);
        return original with
        {
            ResourceOperation = operation with { CanIssueOutsideAuthority = false },
            ApprovedResourceReferences = [resource.CanonicalReference],
            ApprovedResourceClaims = [resource.ResourceClaim!],
            ApprovedResourceCapabilities = resource.AuthorityReferences,
        };
    }

    private static ValueTask<LocalModels.AgentFileMutationResult> Write(LocalExecutionRuntimeConfig config,
        LocalModels.AgentExecutionTargetContext context, LocalResourceReference references, string path)
        => LocalFileSystemExecutor.WriteFileAsync(config, new LocalModels.AgentFileWriteRequest { Path = path, Content = "approved", Overwrite = false },
            true, CancellationToken.None, authorizationContext: context, resourceReferences: references);

    private sealed class CancelBeforeContent : ILocalFileWriteFaultInjector
    {
        public void OnFaultPoint(LocalFileWriteFaultPoint point)
        {
            if (point == LocalFileWriteFaultPoint.ContentPrepared) throw new OperationCanceledException();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
