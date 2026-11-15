extern alias LocalWorker;

using LocalWorker::Sunder.Package.Agent.Execution.Local;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed partial class WindowsSecureMutationTests
{
    [PlatformFact(TestPlatform.Windows, "Operation-scoped Windows traversal must borrow creation objects before capture, without lending them to another invocation.")]
    public void CreatedAncestors_AreBorrowedBeforeSiblingCaptureAndSessionOwnsTheirLifetime()
    {
        var config = new LocalExecutionRuntimeConfig([_root], _root);
        using var references = new LocalResourceReference();
        using var approved = LocalResourceAuthorityTestContext.Approve(config, "created/a.txt", "files.mutate", resourceReferences: references);
        using var foreign = LocalResourceAuthorityTestContext.Approve(config, "created/b.txt", "files.mutate", resourceReferences: references);
        var siblingOperation = approved.Context.ResourceOperation! with { ResourceIndex = 1, CanIssueOutsideAuthority = true };
        var resource = LocalResourceResolver.ResolveFileResource(config, "created/b.txt", true,
            approved.Context with { ResourceOperation = siblingOperation }, references);
        var siblingContext = approved.Context with
        {
            ResourceOperation = siblingOperation with { CanIssueOutsideAuthority = false },
            ApprovedResourceClaims = [resource.ResourceClaim!],
            ApprovedResourceCapabilities = resource.AuthorityReferences,
        };
        var fingerprint = resource.ResourceClaim!.RootIdentityChainFingerprint;
        using var authority = LocalSecurePathEngine.ResolveApprovedAuthority(config, Path.Combine(_root, "created/a.txt"), "created/a.txt",
            true, null, approved.Context, resourceReferences: references);
        var ownership = authority!.CreatedDirectories!;
        using (var session = authority.OpenSession(true, null, CancellationToken.None))
        {
            authority.Dispose(); // The live session, not the consumed approval, owns the borrow.
            Assert.ThrowsAny<IOException>(() => Directory.Move(Path.Combine(_root, "created"), Path.Combine(_root, "foreign")));
            Assert.Throws<LocalSecureApprovalChangedException>(() => LocalSecurePathEngine.ResolveApprovedAuthority(config,
                Path.Combine(_root, "created/b.txt"), "created/b.txt", true, null, foreign.Context, resourceReferences: references));
            using var sibling = LocalSecurePathEngine.ResolveApprovedAuthority(config, Path.Combine(_root, "created/b.txt"), "created/b.txt",
                true, null, siblingContext, resourceReferences: references);
            Assert.Equal(session.Parent.Identity, sibling!.Binding.AnchorIdentity);
            Assert.False(sibling.Binding.Exists);
            Assert.Equal(fingerprint, resource.ResourceClaim.RootIdentityChainFingerprint);
            Assert.True(LocalSecurePathEngine.HasCurrentOutsideCapabilities(config, siblingContext, references));
            Assert.False(LocalSecurePathEngine.HasCurrentOutsideCapabilities(config, siblingContext with
            {
                ResourceOperation = siblingOperation with { AuthorityActivationId = Guid.NewGuid().ToString("N") },
            }, references));
            var recaptured = LocalResourceResolver.ResolveFileResource(config, "created/c.txt", true,
                siblingContext with { ResourceOperation = siblingOperation }, references);
            foreach (var capability in recaptured.AuthorityReferences) references.Revoke(capability);
            foreach (var capability in approved.Context.ApprovedResourceCapabilities.Concat(resource.AuthorityReferences)) references.Revoke(capability);
            Assert.False(ownership.IsDisposed);
            Assert.Null(references.BorrowDirectoryOperation(siblingContext));
        }
        Assert.True(ownership.IsDisposed);
        Assert.Empty(ownership.CleanupFailures);
        Assert.False(Directory.Exists(Path.Combine(_root, "created")));
        Assert.True(HostSecurePathEngine.IsQuarantineName(Path.GetFileName(Assert.Single(Directory.EnumerateDirectories(_root)))));
    }

    [PlatformFact(TestPlatform.Windows, "Concurrent Windows siblings must serialize ownership lookup with creation, not race an incompatible independent open.")]
    public async Task CreatedAncestors_ConcurrentApprovedSiblingsShareTheCreationObject()
    {
        var config = new LocalExecutionRuntimeConfig([_root], _root);
        using var references = new LocalResourceReference();
        using var approved = LocalResourceAuthorityTestContext.Approve(config, "created/a.txt", "files.mutate", resourceReferences: references);
        var operation = approved.Context.ResourceOperation! with { ResourceIndex = 1, CanIssueOutsideAuthority = true };
        var resource = LocalResourceResolver.ResolveFileResource(config, "created/b.txt", true, approved.Context with { ResourceOperation = operation }, references);
        var sibling = approved.Context with
        {
            ResourceOperation = operation with { CanIssueOutsideAuthority = false },
            ApprovedResourceClaims = [resource.ResourceClaim!],
            ApprovedResourceCapabilities = resource.AuthorityReferences,
        };
        using var barrier = new Barrier(2);
        var hooks = new BeforeCreatedAncestor(barrier);
        var identities = await Task.WhenAll(Task.Run(() => Create(approved.Context, "created/a.txt")),
            Task.Run(() => Create(sibling, "created/b.txt")));
        Assert.Equal(identities[0], identities[1]);

        LocalSecureIdentity Create(LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentExecutionTargetContext context, string path)
        {
            using var authority = LocalSecurePathEngine.ResolveApprovedAuthority(config, Path.Combine(_root, path), path,
                true, null, context, resourceReferences: references);
            using var session = authority!.OpenSession(true, hooks, CancellationToken.None);
            return session.Parent.Identity;
        }
    }

    private sealed class BeforeCreatedAncestor(Barrier barrier) : ILocalSecureFileSystemHooks
    {
        public void OnCheckpoint(LocalSecureFileSystemCheckpoint checkpoint, string path, string? entryName)
        {
            if (checkpoint == LocalSecureFileSystemCheckpoint.BeforeOpen && entryName == "created")
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)));
        }
    }

    [PlatformTheory(TestPlatform.Windows, "Expiry/revocation must drain the real Windows traversal borrower, including post-mutation authority, before cleanup.")]
    [InlineData(false)]
    [InlineData(true)]
    public void CreatedAncestors_ExpiredOrRevokedCapabilitiesCannotBorrowButTransferredAuthorityKeepsOwnership(bool expires)
    {
        var config = new LocalExecutionRuntimeConfig([_root], _root);
        var now = DateTimeOffset.UtcNow;
        using var references = new LocalResourceReference(TimeSpan.FromMinutes(30), () => now);
        using var approved = LocalResourceAuthorityTestContext.Approve(config, "created/a.txt", "files.mutate", resourceReferences: references);
        using var authority = LocalSecurePathEngine.ResolveApprovedAuthority(config, Path.Combine(_root, "created/a.txt"), "created/a.txt",
            true, null, approved.Context, resourceReferences: references);
        var ownership = authority!.CreatedDirectories!;
        LocalSecureApprovalLease post;
        using (var session = authority.OpenSession(true, null, CancellationToken.None))
        {
            authority.Dispose();
            if (expires) now += TimeSpan.FromHours(1);
            else foreach (var capability in approved.Context.ApprovedResourceCapabilities) references.Revoke(capability);
            Assert.Null(references.BorrowDirectoryOperation(approved.Context));
            Assert.False(ownership.IsDisposed);
            post = session.TakeDeletedPostMutationAuthority();
        }
        using (post)
        {
            Assert.False(ownership.IsDisposed);
            Assert.True(Directory.Exists(Path.Combine(_root, "created")));
            Assert.ThrowsAny<IOException>(() => Directory.Move(Path.Combine(_root, "created"), Path.Combine(_root, "foreign")));
        }
        Assert.True(ownership.IsDisposed);
        Assert.Empty(ownership.CleanupFailures);
        Assert.False(Directory.Exists(Path.Combine(_root, "created")));
    }

    [PlatformFact(TestPlatform.Windows, "Nested creation must use reduced duplicates while cleanup retains full creation authority.")]
    public void CreatedAncestors_NestedBorrowSupportsCreationAndCancellationWithoutReopeningOwnedDirectories()
    {
        using var ownership = new LocalCreatedDirectoryAuthority();
        using (var authority = HostSecurePathEngine.Capture([_root], Path.Combine(_root, "created/nested/a.txt"), allowMissingSuffix: true,
                   createdDirectories: ownership))
        using (var session = authority.OpenSession(true, null, CancellationToken.None))
        {
            Assert.EndsWith(Path.Combine("created", "nested"), session.Parent.FullPath);
            Assert.False(session.Parent.MutationExclusive);
            using var sibling = HostSecurePathEngine.Capture([_root], Path.Combine(_root, "created/nested/b.txt"), allowMissingSuffix: true,
                createdDirectories: ownership);
            Assert.Equal(session.Parent.Identity, sibling.Binding.AnchorIdentity);
        }
        using var canceled = new CancellationTokenSource();
        using (var authority = HostSecurePathEngine.Capture([_root], Path.Combine(_root, "created/canceled/deeper/a.txt"), allowMissingSuffix: true,
                   createdDirectories: ownership))
        {
            canceled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => authority.OpenSession(true, null, canceled.Token));
        }
        ownership.Dispose();
        Assert.Empty(ownership.CleanupFailures);
        Assert.False(Directory.Exists(Path.Combine(_root, "created/nested")));
        // Recovery entries remain retained; cleanup never recurses into their parent.
        Assert.True(Directory.Exists(Path.Combine(_root, "created")));
        Assert.True(HostSecurePathEngine.IsQuarantineName(Path.GetFileName(Assert.Single(Directory.EnumerateDirectories(Path.Combine(_root, "created"))))));
    }
}
