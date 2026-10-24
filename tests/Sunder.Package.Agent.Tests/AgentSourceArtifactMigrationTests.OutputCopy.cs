using Sunder.Package.Agent.TestSupport;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    [Fact]
    public void CopyOutputs_ReusesOnlyIdenticalBytesWhileSymbolsAreInUse()
    {
        using var temporary = TemporaryDirectory.Create("source-artifact-output-copy");
        var source = temporary.GetPath("source");
        var destination = temporary.GetPath("destination");
        Directory.CreateDirectory(source);
        var symbols = Path.Combine(source, "role.pdb");
        byte[] original = [1, 2, 3, 4];
        byte[] changed = [4, 3, 2, 1];
        File.WriteAllBytes(symbols, original);
        CopyDirectory(source, destination);
        var copied = Path.Combine(destination, "role.pdb");

        using (var loadedSymbols = File.Open(copied, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            CopyDirectory(source, destination);
            Assert.Equal(original, File.ReadAllBytes(copied));
        }

        File.WriteAllBytes(symbols, changed);
        CopyDirectory(source, destination);
        Assert.Equal(changed, File.ReadAllBytes(copied));
    }
}
