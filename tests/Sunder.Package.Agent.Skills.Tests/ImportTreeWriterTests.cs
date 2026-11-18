extern alias SkillsWorker;

using SkillsWorker::Sunder.Package.Agent.Shared.Importing;
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
            await using (var target = writer.CreateFile("nested/文-😀.txt"))
                await target.WriteAsync("native entry"u8.ToArray());
            writer.CreateDirectory("empty");
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal("private skill", await File.ReadAllTextAsync(path));
            writer.Complete();
            var files = BoundedImportIO.ValidateTree(root, new ImportTreeLimits(2, 3, 4, 1024, 2048));
            Assert.Equal(new[] { "SKILL.md", "nested/文-😀.txt" }, files.Select(file => file.RelativePath));
            Assert.All(files, file => Assert.Equal(ImportNodeKind.RegularFile, file.Identity.Kind));
            Assert.Equal((long)"private skill"u8.Length, files[0].Length);
            Assert.Equal(System.Security.Cryptography.SHA256.HashData("private skill"u8), files[0].Sha256);
            Assert.Equal((long)"native entry"u8.Length, files[1].Length);
            Assert.Equal(System.Security.Cryptography.SHA256.HashData("native entry"u8), files[1].Sha256);
            Assert.Throws<IOException>(() => writer.CreateFile("SKILL.md"));
            Assert.Equal("private skill", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
