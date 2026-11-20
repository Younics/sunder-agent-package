extern alias DockerWorker;

using System.Runtime.InteropServices;
using DockerWorker::Sunder.Agent.Execution.Common;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class WindowsMountIdentityChallengeTests : IDisposable
{
    private const string HostValue = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ResponseValue = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private readonly string _path = Directory.CreateTempSubdirectory("sunder-windows-mount-challenge-").FullName;

    [PlatformFact(TestPlatform.Windows, "Windows mount challenges separate deletion observations from retained root pins.")]
    public void CompleteChallenge_VerifiesExactRemovalWhileTheApprovedRootRemainsPinned()
    {
        using var root = HostSecurePathEngine.OpenRoot(_path);
        using var challenge = HostSecureMountChallenge.Create(root, HostValue, default);
        var directory = Path.Combine(_path, challenge.DirectoryName);
        Assert.Throws<IOException>(() => Directory.Move(_path, _path + "-moved"));
        Assert.Throws<IOException>(() => Directory.Move(directory, directory + "-moved"));
        CreateResponse(challenge, ResponseValue);
        challenge.VerifyContainerResponse(ResponseValue);
        RemoveContainerEntries(challenge);
        challenge.VerifyContainerCleanup();
        Assert.Empty(Directory.EnumerateFileSystemEntries(_path));
        Assert.Throws<IOException>(() => Directory.Move(_path, _path + "-moved"));
    }

    [PlatformTheory(TestPlatform.Windows, "Failed cleanup never adopts or truncates container-created response objects.")]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOrForgedCleanup_PreservesForeignResponseBytesAndSanitizesOnlyHostCreations(bool mismatch)
    {
        using var root = HostSecurePathEngine.OpenRoot(_path);
        var challenge = HostSecureMountChallenge.Create(root, HostValue, default);
        try
        {
            CreateResponse(challenge, ResponseValue);
            if (mismatch)
                Assert.Throws<LocalSecurePathException>(() => challenge.VerifyContainerResponse(HostValue));
            else
            {
                challenge.VerifyContainerResponse(ResponseValue);
                Assert.Throws<LocalSecurePathException>(challenge.VerifyContainerCleanup);
            }
        }
        finally { challenge.Dispose(); }
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_path), entry => HostSecurePathEngine.IsChallengeName(Path.GetFileName(entry)));
        Assert.Equal(ResponseValue, File.ReadAllText(Assert.Single(Directory.EnumerateFiles(_path, HostSecureMountChallenge.ContainerResponseFileName, SearchOption.AllDirectories))));
        Assert.All(Directory.EnumerateFiles(_path, HostSecureMountChallenge.HostChallengeFileName, SearchOption.AllDirectories), entry => Assert.Equal(0, new FileInfo(entry).Length));
        Assert.All(Directory.EnumerateFiles(_path, WindowsHostSecureMountChallenge.DeletionFileName, SearchOption.AllDirectories), entry => Assert.Equal(0, new FileInfo(entry).Length));
    }

    [PlatformFact(TestPlatform.Windows, "A new arrival after witness removal must not become cleanup authority.")]
    public void ReplacementEntryAfterWitnessRemoval_IsPreservedAndCleanupFailsExplicitly()
    {
        using var root = HostSecurePathEngine.OpenRoot(_path);
        var challenge = HostSecureMountChallenge.Create(root, HostValue, default);
        try
        {
            CreateResponse(challenge, ResponseValue);
            challenge.VerifyContainerResponse(ResponseValue);
            RemoveContainerEntries(challenge);
            var replacement = Directory.CreateDirectory(Path.Combine(_path, challenge.DirectoryName, HostSecureMountChallenge.ContainerDirectoryName)).FullName;
            File.WriteAllText(Path.Combine(replacement, "foreign.txt"), "unrelated replacement");
            Assert.Throws<LocalSecurePathException>(challenge.VerifyContainerCleanup);
        }
        finally { challenge.Dispose(); }
        Assert.Equal("unrelated replacement", File.ReadAllText(Assert.Single(Directory.EnumerateFiles(_path, "foreign.txt", SearchOption.AllDirectories))));
    }

    [PlatformFact(TestPlatform.Windows, "Pre-cancellation must leave no challenge or reserved entry behind.")]
    public void CanceledChallenge_DoesNotCreateAnyEntries()
    {
        using var root = HostSecurePathEngine.OpenRoot(_path);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => HostSecureMountChallenge.Create(root, HostValue, canceled.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_path));
    }

    [PlatformFact(TestPlatform.Windows, "Mount challenge responses must not bypass the shared single-link regular-file policy.")]
    public void HardLinkedResponse_IsRejectedWithoutAdoptingOrTruncatingEitherName()
    {
        using var root = HostSecurePathEngine.OpenRoot(_path);
        var challenge = HostSecureMountChallenge.Create(root, HostValue, default);
        try
        {
            CreateResponse(challenge, ResponseValue);
            var response = Path.Combine(_path, challenge.DirectoryName, HostSecureMountChallenge.ContainerDirectoryName, HostSecureMountChallenge.ContainerResponseFileName);
            Assert.True(CreateHardLink(Path.Combine(_path, "foreign-alias"), response, 0));
            Assert.Throws<LocalSecurePathException>(() => challenge.VerifyContainerResponse(ResponseValue));
        }
        finally { challenge.Dispose(); }
        Assert.Equal(ResponseValue, File.ReadAllText(Path.Combine(_path, "foreign-alias")));
        Assert.Equal(ResponseValue, File.ReadAllText(Assert.Single(Directory.EnumerateFiles(_path, HostSecureMountChallenge.ContainerResponseFileName, SearchOption.AllDirectories))));
    }

    private void CreateResponse(IHostSecureMountChallenge challenge, string value)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_path, challenge.DirectoryName, HostSecureMountChallenge.ContainerDirectoryName)).FullName;
        File.WriteAllText(Path.Combine(directory, HostSecureMountChallenge.ContainerResponseFileName), value);
    }

    private void RemoveContainerEntries(IHostSecureMountChallenge challenge)
    {
        foreach (var file in challenge.ContainerFilesToRemove)
            File.Delete(Path.Combine(_path, challenge.DirectoryName, file.Replace('/', Path.DirectorySeparatorChar)));
        foreach (var directory in challenge.ContainerDirectoriesToRemove)
            Directory.Delete(Path.Combine(_path, challenge.DirectoryName, directory.Replace('/', Path.DirectorySeparatorChar)));
    }

    public void Dispose() => Directory.Delete(_path, recursive: true);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingName, nint securityAttributes);
}
