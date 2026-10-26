using Sunder.Package.Agent.Shared.Importing;
using Xunit;

namespace Sunder.Package.Agent.Skills.Tests;

public sealed class ImportTreeWriterTests
{
    [Fact]
    public async Task CreatedFilesArePrivateReadableAndWritableWithoutChangingExistingTargets()
    {
        var root = Directory.CreateTempSubdirectory("sunder-import-writer-").FullName;
        try
        {
            using var writer = BoundedImportIO.OpenTreeWriter(root);
            var path = Path.Combine(root, "SKILL.md");
            await using (var target = writer.CreateFile("SKILL.md"))
                await target.WriteAsync("private skill"u8.ToArray());
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal("private skill", await File.ReadAllTextAsync(path));
            writer.Complete();
            Assert.Throws<IOException>(() => writer.CreateFile("SKILL.md"));
            Assert.Equal("private skill", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
