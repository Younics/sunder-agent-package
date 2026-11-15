using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class LocalSecureHandleTests
{
    [Fact]
    public void RetainedHandleCanBeRetainedAgainAfterOriginalIsDisposed()
    {
        var next = 10;
        var sources = new List<nint>();
        SafeFileHandle Duplicate(SafeFileHandle source)
        {
            Assert.False(source.IsClosed);
            sources.Add(source.DangerousGetHandle());
            return new SafeFileHandle(++next, ownsHandle: false);
        }

        using var original = new LocalSecureHandle(new SafeFileHandle(10, ownsHandle: false), "file",
            LocalSecureNodeKind.RegularFile, new LocalSecureIdentity(1, 2), 3, 1, 0, 0, 0, Duplicate);
        using var retained = original.Retain();
        original.Dispose();
        using var retainedAgain = retained.Retain();
        retained.Dispose();
        using var final = retainedAgain.DuplicateHandle();

        Assert.Equal(new nint[] { 10, 11, 12 }, sources);
        Assert.Equal(original.Identity, retainedAgain.Identity);
    }

    [Fact]
    public void PlatformTraversalDuplicationRelaxesOnlyTheTraversalCopy()
    {
        var ordinaryCopies = 0;
        var traversalCopies = 0;
        using var original = new LocalSecureHandle(new SafeFileHandle(10, ownsHandle: false), "directory",
            LocalSecureNodeKind.Directory, new LocalSecureIdentity(1, 2), 0, 1, 0, 0, 0,
            _ => { ordinaryCopies++; return new SafeFileHandle(11, ownsHandle: false); },
            mutationExclusive: true,
            duplicateForTraversal: _ => { traversalCopies++; return new SafeFileHandle(12, ownsHandle: false); });

        using var exclusive = original.Retain();
        using var traversal = exclusive.RetainForTraversal();
        exclusive.Dispose();
        using var traversalAgain = traversal.RetainForTraversal();

        Assert.True(original.MutationExclusive);
        Assert.False(traversal.MutationExclusive);
        Assert.False(traversalAgain.MutationExclusive);
        Assert.Equal(1, traversalCopies);
        Assert.Equal(2, ordinaryCopies);
    }
}
