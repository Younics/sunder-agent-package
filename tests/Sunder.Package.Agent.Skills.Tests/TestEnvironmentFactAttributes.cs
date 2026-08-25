using Sunder.Package.Agent.TestSupport;
using Xunit;

namespace Sunder.Package.Agent.Skills.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class FileSystemFactAttribute : FactAttribute
{
    public FileSystemFactAttribute(TestFileSystemCapability capability, string reason)
    {
        Skip = TestFileSystemCapabilities.GetUnavailableReason(capability, reason);
    }
}
