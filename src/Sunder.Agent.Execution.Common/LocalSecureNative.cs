namespace Sunder.Agent.Execution.Common;

internal static class LocalSecureNative
{
    public static bool StrictMutationsAvailable => !OperatingSystem.IsWindows();

    public static ILocalSecureFileSystemPlatform CreatePlatform()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return new LocalUnixFileSystemPlatform();
        }

        if (OperatingSystem.IsWindows())
        {
            return new LocalWindowsFileSystemPlatform();
        }

        throw new PlatformNotSupportedException(
            "Strict Local filesystem operations are supported only on Linux, macOS, and Windows.");
    }
}
