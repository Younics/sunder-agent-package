using Microsoft.Win32.SafeHandles;
using Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.TestSupport;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public enum TestPlatform
{
    Windows,
    Linux,
    MacOS,
    Unix,
    MacCaseInsensitive,
    NonRootUnix,
    UnixWithDistinctDevice,
}

[XunitTestCaseDiscoverer(
    "Sunder.Package.Agent.Execution.Local.Tests.PlatformFactDiscoverer",
    "Sunder.Package.Agent.Execution.Local.Tests")]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PlatformFactAttribute : FactAttribute
{
    public PlatformFactAttribute(TestPlatform platform, string reason)
    {
    }

    public PlatformFactAttribute(
        TestPlatform platform,
        TestFileSystemCapability capability,
        string reason)
    {
    }
}

[XunitTestCaseDiscoverer(
    "Sunder.Package.Agent.Execution.Local.Tests.PlatformTheoryDiscoverer",
    "Sunder.Package.Agent.Execution.Local.Tests")]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PlatformTheoryAttribute : TheoryAttribute
{
    public PlatformTheoryAttribute(TestPlatform platform, string reason)
    {
    }

    public PlatformTheoryAttribute(
        TestPlatform platform,
        TestFileSystemCapability capability,
        string reason)
    {
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class FileSystemFactAttribute : FactAttribute
{
    public FileSystemFactAttribute(TestFileSystemCapability capability, string reason)
    {
        Skip = TestFileSystemCapabilities.GetUnavailableReason(capability, reason);
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class FileSystemTheoryAttribute : TheoryAttribute
{
    public FileSystemTheoryAttribute(TestFileSystemCapability capability, string reason)
    {
        Skip = TestFileSystemCapabilities.GetUnavailableReason(capability, reason);
    }
}

public sealed class PlatformFactDiscoverer(IMessageSink diagnosticMessageSink)
    : FactDiscoverer(diagnosticMessageSink)
{
    protected override IXunitTestCase CreateTestCase(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        ITestMethod testMethod,
        IAttributeInfo factAttribute)
        => new PlatformTestCase(
            DiagnosticMessageSink,
            discoveryOptions.MethodDisplayOrDefault(),
            discoveryOptions.MethodDisplayOptionsOrDefault(),
            testMethod);
}

public sealed class PlatformTheoryDiscoverer(IMessageSink diagnosticMessageSink)
    : TheoryDiscoverer(diagnosticMessageSink)
{
    public override IEnumerable<IXunitTestCase> Discover(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        ITestMethod testMethod,
        IAttributeInfo theoryAttribute)
    {
        if (PlatformTestConditions.GetSkipReason(theoryAttribute) is not null)
        {
            return
            [
                new PlatformTestCase(
                    DiagnosticMessageSink,
                    discoveryOptions.MethodDisplayOrDefault(),
                    discoveryOptions.MethodDisplayOptionsOrDefault(),
                    testMethod),
            ];
        }

        return base.Discover(discoveryOptions, testMethod, theoryAttribute);
    }
}

public sealed class PlatformTestCase : XunitTestCase
{
    [Obsolete("Called by the deserializer", error: true)]
    public PlatformTestCase()
    {
    }

    public PlatformTestCase(
        IMessageSink diagnosticMessageSink,
        TestMethodDisplay defaultMethodDisplay,
        TestMethodDisplayOptions defaultMethodDisplayOptions,
        ITestMethod testMethod)
        : base(
            diagnosticMessageSink,
            defaultMethodDisplay,
            defaultMethodDisplayOptions,
            testMethod,
            testMethodArguments: null)
    {
    }

    protected override string? GetSkipReason(IAttributeInfo factAttribute)
        => PlatformTestConditions.GetSkipReason(factAttribute)
           ?? base.GetSkipReason(factAttribute);
}

public static class PlatformTestConditions
{
    private static readonly Lazy<bool> DistinctUnixDevice = new(ProbeDistinctUnixDevice);
    private static readonly Lazy<bool> TempVolumeCaseSensitivity = new(ProbeTempVolumeCaseSensitivity);

    public static bool TempVolumeIsCaseInsensitive => TempVolumeCaseSensitivity.Value;

    public static string? GetSkipReason(IAttributeInfo attribute)
    {
        var arguments = attribute.GetConstructorArguments().ToArray();
        var platform = (TestPlatform)Convert.ToInt32(arguments[0], System.Globalization.CultureInfo.InvariantCulture);
        var reason = (string)arguments[^1];
        var platformSkipReason = platform switch
        {
            TestPlatform.Windows when !OperatingSystem.IsWindows() => reason,
            TestPlatform.Linux when !OperatingSystem.IsLinux() => reason,
            TestPlatform.MacOS when !OperatingSystem.IsMacOS() => reason,
            TestPlatform.Unix when OperatingSystem.IsWindows() => reason,
            TestPlatform.MacCaseInsensitive when !OperatingSystem.IsMacOS() => reason,
            TestPlatform.MacCaseInsensitive when !TempVolumeIsCaseInsensitive =>
                "The temporary volume is case-sensitive.",
            TestPlatform.NonRootUnix when OperatingSystem.IsWindows() => reason,
            TestPlatform.NonRootUnix when string.Equals(
                Environment.UserName,
                "root",
                StringComparison.Ordinal) =>
                "POSIX permission-boundary behavior cannot be observed while running as root.",
            TestPlatform.UnixWithDistinctDevice when OperatingSystem.IsWindows() => reason,
            TestPlatform.UnixWithDistinctDevice when !DistinctUnixDevice.Value =>
                "/dev does not expose a distinct device on this host.",
            _ => null,
        };
        if (platformSkipReason is not null)
        {
            return platformSkipReason;
        }

        return arguments.Length == 3
            ? TestFileSystemCapabilities.GetUnavailableReason(
                (TestFileSystemCapability)Convert.ToInt32(
                    arguments[1],
                    System.Globalization.CultureInfo.InvariantCulture),
                reason)
            : null;
    }

    public static bool IsSupported(TestPlatform platform) => platform switch
    {
        TestPlatform.Windows => OperatingSystem.IsWindows(),
        TestPlatform.Linux => OperatingSystem.IsLinux(),
        TestPlatform.MacOS => OperatingSystem.IsMacOS(),
        TestPlatform.Unix => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
        TestPlatform.MacCaseInsensitive => OperatingSystem.IsMacOS() && TempVolumeIsCaseInsensitive,
        TestPlatform.NonRootUnix => !OperatingSystem.IsWindows()
                                     && !string.Equals(Environment.UserName, "root", StringComparison.Ordinal),
        TestPlatform.UnixWithDistinctDevice => !OperatingSystem.IsWindows() && DistinctUnixDevice.Value,
        _ => false,
    };

    private static bool ProbeDistinctUnixDevice()
    {
        var rootDescriptor = LocalUnixNative.Open(
            "/",
            LocalUnixNative.ReadOnly | LocalUnixNative.DirectoryFlag | LocalUnixNative.NoFollowFlag | LocalUnixNative.CloseOnExecFlag);
        var deviceDescriptor = LocalUnixNative.Open(
            "/dev",
            LocalUnixNative.ReadOnly | LocalUnixNative.DirectoryFlag | LocalUnixNative.NoFollowFlag | LocalUnixNative.CloseOnExecFlag);
        if (rootDescriptor < 0 || deviceDescriptor < 0)
        {
            if (rootDescriptor >= 0)
            {
                new SafeFileHandle(new IntPtr(rootDescriptor), ownsHandle: true).Dispose();
            }
            if (deviceDescriptor >= 0)
            {
                new SafeFileHandle(new IntPtr(deviceDescriptor), ownsHandle: true).Dispose();
            }
            return false;
        }

        using var rootHandle = new SafeFileHandle(new IntPtr(rootDescriptor), ownsHandle: true);
        using var deviceHandle = new SafeFileHandle(new IntPtr(deviceDescriptor), ownsHandle: true);
        return LocalUnixNative.ReadMetadata(rootDescriptor).Device
               != LocalUnixNative.ReadMetadata(deviceDescriptor).Device;
    }

    private static bool ProbeTempVolumeCaseSensitivity()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-case-probe-" + Guid.NewGuid().ToString("N"));
        var lower = Path.Combine(root, "case-probe");
        try
        {
            Directory.CreateDirectory(lower);
            return Directory.Exists(Path.Combine(root, "CASE-PROBE"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
