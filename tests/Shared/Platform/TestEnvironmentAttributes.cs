using Sunder.Package.Agent.TestSupport;
using Xunit;

namespace Sunder.Package.Agent.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(string reason)
    {
        if (!OperatingSystem.IsWindows()) Skip = reason;
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute(string reason)
    {
        if (!OperatingSystem.IsWindows()) Skip = reason;
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute(string reason)
    {
        if (OperatingSystem.IsWindows()) Skip = reason;
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class UnixTheoryAttribute : TheoryAttribute
{
    public UnixTheoryAttribute(string reason)
    {
        if (OperatingSystem.IsWindows()) Skip = reason;
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class FileSystemFactAttribute : FactAttribute
{
    public FileSystemFactAttribute(TestFileSystemCapability capability, string reason,
        params TestFileSystemCapability[] additionalCapabilities)
        => Skip = new[] { capability }.Concat(additionalCapabilities)
            .Select(item => TestFileSystemCapabilities.GetUnavailableReason(item, reason)).FirstOrDefault(value => value is not null);
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class FileSystemTheoryAttribute : TheoryAttribute
{
    public FileSystemTheoryAttribute(TestFileSystemCapability capability, string reason,
        params TestFileSystemCapability[] additionalCapabilities)
        => Skip = new[] { capability }.Concat(additionalCapabilities)
            .Select(item => TestFileSystemCapabilities.GetUnavailableReason(item, reason)).FirstOrDefault(value => value is not null);
}
