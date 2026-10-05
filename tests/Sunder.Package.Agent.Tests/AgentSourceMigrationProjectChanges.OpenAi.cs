using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeOpenAiRelocation(string path, string text)
    {
        if (!path.Replace('\\', '/').Contains("/src/Sunder.Package.Agent.Provider.OpenAI/", StringComparison.Ordinal)) return text;
        if (Path.GetFileName(path) == "OpenAiManagementRpcProvider.cs")
        {
            const string marker = "internal sealed class OpenAiAuthOperationHandler";
            Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
            var operation = File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "OpenAiAuthOperationHandler.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.Equal(2, operation.Split(marker, StringSplitOptions.None).Length);
            text = text.TrimEnd() + "\n\n" + operation[operation.IndexOf(marker, StringComparison.Ordinal)..];
        }
        if (Path.GetFileName(path) == "OpenAiAppManagement.cs")
        {
            const string prefix = "namespace Sunder.Package.Agent.Provider.OpenAI;\n\n";
            Assert.StartsWith(prefix, text, StringComparison.Ordinal);
            var legacy = ReadMigrationSourceText(Path.Combine(Path.GetDirectoryName(path)!, "OpenAiOwnerManagementRpcClient.Legacy.cs"));
            text = legacy.TrimEnd() + "\n\n" + text[prefix.Length..];
        }
        return text;
    }
}
