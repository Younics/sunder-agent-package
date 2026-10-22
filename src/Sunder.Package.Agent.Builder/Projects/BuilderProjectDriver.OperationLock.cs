using System.Security.Cryptography;
using System.Text;

namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderProjectDriver
{
    private FileStream AcquireProjectMutation(string project)
    {
        BuilderProjectSource.EnsureSafePath(project, project);
        var key = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? project.ToUpperInvariant() : project;
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var directory = options.ProjectMutationLockRootPath;
        BuilderProjectSource.EnsureSafePath(directory, directory);
        BuilderToolchainFiles.CreatePrivateDirectory(directory);
        var path = Path.Combine(directory, digest + ".lock");
        BuilderProjectSource.EnsureSafePath(directory, path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException)
        {
            throw new BuilderJobBusyException("Another Builder activation is mutating or watching this project. Stop that operation before retrying.");
        }
        // Lock files are deliberately retained: unlinking a released lock creates an inode race
        // with another process that has already opened the same stable lock path.
    }
}
