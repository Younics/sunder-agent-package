using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeShellBindings(string path, string text)
    {
        if (!path.Replace('\\', '/').EndsWith("/src/Sunder.Package.Agent.Tools.Shell/ShellToolSource.cs", StringComparison.Ordinal)) return text;
        const string timeout = "ProcessExecutionLimits.MaximumTimeoutSeconds";
        Assert.Equal(3, text.Split(timeout, StringSplitOptions.None).Length);
        text = text.Replace(timeout, "BoundedProcessRunner.MaximumTimeoutSeconds", StringComparison.Ordinal);
        const string start = "    private static async ValueTask<ShellExecutionLease?> AcquireTargetAsync(";
        const string end = "    private static bool IsShellToolId(string toolId)";
        Assert.Equal(2, text.Split(start, StringSplitOptions.None).Length); Assert.Equal(2, text.Split(end, StringSplitOptions.None).Length);
        // Files and Shell originally used this byte-identical target acquisition block.
        var original = File.ReadAllText(Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "tests/Sunder.Package.Agent.Tests/Fixtures/FilesToolSource.transport-before-source.cs.txt")).Replace("\r\n", "\n", StringComparison.Ordinal);
        text = text[..text.IndexOf(start, StringComparison.Ordinal)] + original.TrimEnd() + "\n\n" + text[text.IndexOf(end, StringComparison.Ordinal)..];
        var replacements = new (string After, string Before, int Count)[]
        {
            ("public sealed partial class ShellToolSource", "public sealed class ShellToolSource\n    : IAgentToolSource, IAgentPermissionAwareToolSource, IAgentPermissionSurface, IAgentPromptContextContributor, IAgentToolPresentationResolver", 1),
            ("    internal async ValueTask<", "    public async ValueTask<", 3),
            ("        ShellExecutionReference? selectedTarget,\n", "", 3),
            ("                selectedTarget,\n                context.ExecutionBinding,", "                context.ExecutionTargetReference,\n                context.ExecutionBinding,", 2),
            ("                selectedTarget,\n                request.ExecutionBinding,", "                request.ExecutionTargetReference,\n                request.ExecutionBinding,", 1),
            ("            await targetLease.DisposeAsync().ConfigureAwait(false);\n", "", 1),
        };
        foreach (var (after, before, count) in replacements)
        {
            Assert.Equal(count + 1, text.Split(after, StringSplitOptions.None).Length);
            text = text.Replace(after, before, StringComparison.Ordinal);
        }
        return text;
    }
}
