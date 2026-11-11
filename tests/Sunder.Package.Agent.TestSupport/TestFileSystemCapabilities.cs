using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sunder.Package.Agent.TestSupport;

public enum TestFileSystemCapability
{
    FileSymbolicLinks,
    DirectorySymbolicLinks,
    HardLinks,
    UnixDirectorySymbolicLinks,
    CaseSensitiveDirectories,
}

public static class TestFileSystemCapabilities
{
    private static readonly IReadOnlyDictionary<TestFileSystemCapability, Lazy<string?>> Probes =
        new Dictionary<TestFileSystemCapability, Lazy<string?>>
        {
            [TestFileSystemCapability.FileSymbolicLinks] = new(() => Probe(root =>
            {
                var target = Path.Combine(root, "target.txt");
                File.WriteAllText(target, "target");
                File.CreateSymbolicLink(Path.Combine(root, "link.txt"), target);
            })),
            [TestFileSystemCapability.DirectorySymbolicLinks] = new(() => Probe(root =>
            {
                var target = Path.Combine(root, "target");
                Directory.CreateDirectory(target);
                Directory.CreateSymbolicLink(Path.Combine(root, "link"), target);
            })),
            [TestFileSystemCapability.HardLinks] = new(() => Probe(root =>
            {
                var target = Path.Combine(root, "target.txt");
                File.WriteAllText(target, "target");
                CreateHardLink(Path.Combine(root, "link.txt"), target);
            })),
            [TestFileSystemCapability.UnixDirectorySymbolicLinks] = new(() => OperatingSystem.IsWindows()
                ? null
                : Probe(root =>
                {
                    var target = Path.Combine(root, "target");
                    Directory.CreateDirectory(target);
                    Directory.CreateSymbolicLink(Path.Combine(root, "link"), target);
                })),
            [TestFileSystemCapability.CaseSensitiveDirectories] = new(() => Probe(EnableCaseSensitiveDirectory)),
        };

    public static string? GetUnavailableReason(TestFileSystemCapability capability, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var failure = Probes[capability].Value;
        if (failure is null)
        {
            return null;
        }
        var unavailable = $"{reason} Required filesystem capability '{capability}' is unavailable.";
        TestPrerequisites.ThrowIfRequired(unavailable);
        return unavailable;
    }

    public static void CreateHardLink(string linkPath, string existingPath)
    {
        var result = OperatingSystem.IsWindows()
            ? CreateHardLinkWindows(linkPath, existingPath, 0) ? 0 : Marshal.GetLastPInvokeError()
            : CreateHardLinkUnix(existingPath, linkPath) == 0 ? 0 : Marshal.GetLastPInvokeError();
        if (result != 0)
        {
            throw new IOException(new Win32Exception(result).Message);
        }
    }

    public static void EnableCaseSensitiveDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var handle = OpenDirectory(path, 0x100, 7, 0, 3, 0x02000000, 0);
            if (handle.IsInvalid) throw new IOException(new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            uint flags = 1;
            if (!SetCaseSensitiveInformation(handle, 23, ref flags, sizeof(uint)))
                throw new IOException(new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        }
        var original = Path.Combine(path, "case-sensitive-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (new FileStream(original, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            if (File.Exists(Path.Combine(path, Path.GetFileName(original).ToUpperInvariant())))
                throw new PlatformNotSupportedException("The selected temporary directory does not have case-sensitive names.");
        }
        finally { File.Delete(original); }
    }

    private static string? Probe(Action<string> probe)
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-capability-probe-" + Guid.NewGuid().ToString("N"));
        string? failure = null;
        try
        {
            Directory.CreateDirectory(root);
            probe(root);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or PlatformNotSupportedException
                                          or NotSupportedException)
        {
            failure = exception.Message;
        }

        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failure ??= $"Capability probe cleanup failed: {exception.Message}";
        }

        return failure;
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(string existingPath, string linkPath);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string fileName, string existingFileName, nint securityAttributes);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string path, uint access, uint share, nint security,
        uint disposition, uint attributes, nint template);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCaseSensitiveInformation(SafeFileHandle handle, int informationClass, ref uint flags, uint size);
}
