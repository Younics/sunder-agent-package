using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeOwnerRuntimeModels(string path, string text)
    {
        if (path.Replace('\\', '/').EndsWith("/src/Sunder.Package.Agent/OwnerProtocol/AgentOwnerRuntimeRpc.cs", StringComparison.Ordinal))
        {
            const string providerMarker = "internal interface IAgentOwnerRuntimeProvider";
            const string transportMarker = "internal static class AgentOwnerRuntimeRpc";
            Assert.DoesNotContain(providerMarker, text, StringComparison.Ordinal);
            Assert.Equal(2, text.Split(transportMarker, StringSplitOptions.None).Length);
            var provider = File.ReadAllText(Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName,
                    "tests/Sunder.Package.Agent.Tests/Fixtures/OrdinaryServices/IAgentOwnerRuntimeProvider.cs.txt"))
                .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            AssertProjectHash("0071ae3868a5e98409b661316bd8120b2db06b30988e840e4f4662a5b291c3ba", provider, "Retired authored owner port");
            Assert.Equal(2, provider.Split(providerMarker, StringSplitOptions.None).Length);
            text = text.Replace(transportMarker, provider[provider.IndexOf(providerMarker, StringComparison.Ordinal)..].TrimEnd() + "\n\n" + transportMarker, StringComparison.Ordinal);
            const string declaration = "internal sealed class AgentOwnerOperationException";
            Assert.DoesNotContain(declaration, text, StringComparison.Ordinal);
            var exception = File.ReadAllText(Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, AgentAppSourceMigrationTests.OperationErrorSource))
                .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            Assert.Equal(2, exception.Split(declaration, StringSplitOptions.None).Length);
            return text.TrimEnd() + "\n\n" + exception[exception.IndexOf(declaration, StringComparison.Ordinal)..];
        }
        if (!path.Replace('\\', '/').EndsWith("/src/Sunder.Package.Agent/Runtime/AgentRuntimeContracts.cs", StringComparison.Ordinal)) return text;
        const string marker = "internal sealed record AgentTransportProfileRequest";
        Assert.Equal(2, text.Split(marker, StringSplitOptions.None).Length);
        var legacy = ReadMigrationSourceText(Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, AgentAppSourceMigrationTests.LegacyOperations))
            .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var start = legacy.IndexOf("internal static class AgentRuntimeOperations", StringComparison.Ordinal);
        Assert.True(start > 0);
        var header = legacy[..start].Replace("using Wire = Sunder.Package.Agent.OwnerProtocol.Generated;\n", "", StringComparison.Ordinal);
        Assert.Equal(header, text[..text.IndexOf(marker, StringComparison.Ordinal)]);
        return legacy.TrimEnd() + "\n\n" + text[text.IndexOf(marker, StringComparison.Ordinal)..];
    }
}
