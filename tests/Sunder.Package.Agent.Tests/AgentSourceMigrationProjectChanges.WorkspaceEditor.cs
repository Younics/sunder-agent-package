using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeAgentWorkspaceEditorBindings(string path, string text)
    {
        var normalized = path.Replace('\\', '/');
        const string prefix = "Sunder.Package.Agent.Runtime.Rpc.AgentWorkspaceEditorBindings.";
        if (normalized.EndsWith("/src/Sunder.Package.Agent/PackageViews/AgentWorkspacesViewModel.Editor.cs", StringComparison.Ordinal))
            return Replace(text, prefix + "Service", "AgentRpcServices.WorkspaceEditors");
        if (normalized.EndsWith("/src/Sunder.Package.Agent/PackageViews/AgentWorkspaceEditorInvocation.cs", StringComparison.Ordinal))
            return Replace(text, "await " + prefix + "CanEditAsync(contributor, context, cancellationToken).ConfigureAwait(false)", "contributor.CanEdit(context)");
        return text;

        static string Replace(string source, string current, string original)
        {
            Assert.Equal(2, source.Split(current, StringSplitOptions.None).Length);
            return source.Replace(current, original, StringComparison.Ordinal);
        }
    }
}
