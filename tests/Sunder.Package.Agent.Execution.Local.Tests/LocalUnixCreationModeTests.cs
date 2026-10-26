using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class LocalUnixCreationModeTests
{
    [PlatformTheory(TestPlatform.Unix, "Unix creation permissions require a Unix filesystem.")]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenAndOpenAtPassTheCreationModeUsingThePlatformAbi(bool relative)
    {
        var root = Directory.CreateTempSubdirectory("sunder-unix-create-").FullName;
        try
        {
            const string name = "private.txt";
            var path = Path.Combine(root, name);
            using var parent = new SafeFileHandle((IntPtr)LocalUnixNative.Open(root,
                LocalUnixNative.ReadOnly | LocalUnixNative.DirectoryFlag | LocalUnixNative.CloseOnExecFlag), ownsHandle: true);
            Assert.False(parent.IsInvalid);
            var flags = LocalUnixNative.WriteOnly | LocalUnixNative.CreateFlag | LocalUnixNative.ExclusiveFlag
                | LocalUnixNative.NoFollowFlag | LocalUnixNative.CloseOnExecFlag;
            var descriptor = relative
                ? LocalUnixNative.OpenAt(parent.DangerousGetHandle().ToInt32(), name, flags, 0x180)
                : LocalUnixNative.Open(path, flags, 0x180);
            using var created = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            Assert.False(created.IsInvalid);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            RandomAccess.Write(created, "private file"u8, 0);
            Assert.Equal("private file", File.ReadAllText(path));
            Assert.Equal(-1, relative
                ? LocalUnixNative.OpenAt(parent.DangerousGetHandle().ToInt32(), name, flags, 0x180)
                : LocalUnixNative.Open(path, flags, 0x180));
            Assert.Equal(17, Marshal.GetLastPInvokeError());
            Assert.Equal("private file", File.ReadAllText(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
