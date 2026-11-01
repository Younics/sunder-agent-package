using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class TemporaryDirectoryTests
{
    [Theory]
    [InlineData("/var/folders/test/", true, "/private/var/folders/test")]
    [InlineData("/private/var/folders/test/", true, "/private/var/folders/test")]
    [InlineData("/tmp/test/", true, "/private/tmp/test")]
    [InlineData("/private/tmp/test/", true, "/private/tmp/test")]
    [InlineData("/var", true, "/private/var")]
    [InlineData("/variable/test/", true, "/variable/test")]
    [InlineData("/var/folders/test/", false, "/var/folders/test")]
    [InlineData("/private/tmp/test/", false, "/private/tmp/test")]
    public void PhysicalTemporaryRoot_OnlyNormalizesKnownAliasesAndIsIdempotent(string path, bool isMacOS, string expected)
    {
        var normalized = TemporaryDirectory.NormalizeTemporaryRoot(path, isMacOS);
        Assert.Equal(expected, normalized);
        Assert.Equal(normalized, TemporaryDirectory.NormalizeTemporaryRoot(normalized, isMacOS));
    }
}
