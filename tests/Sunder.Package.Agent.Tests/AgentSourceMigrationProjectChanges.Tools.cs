using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeAgentToolBindings(string path, string text)
    {
        var normalized = path.Replace('\\', '/');
        const string prefix = "Sunder.Package.Agent.Runtime.Rpc.";
        if (normalized.EndsWith("/src/Sunder.Package.Agent.Contracts/Runtime/InternalsVisibleTo.cs", StringComparison.Ordinal))
            return Replace(text, "[assembly: InternalsVisibleTo(\"Sunder.Package.Agent.App\")]\n", "");
        if (normalized.EndsWith("/src/Sunder.Package.Agent/Services/AgentToolPresentationService.cs", StringComparison.Ordinal))
            return Replace(text, prefix + "AgentToolBindings.Service", "AgentRpcServices.ToolSources");
        if (!normalized.EndsWith("/src/Sunder.Package.Agent/Services/AgentToolService.Catalog.cs", StringComparison.Ordinal)) return text;
        text = Replace(text, prefix + "AgentToolBindings.Service", "AgentRpcServices.ToolSources");
        text = Replace(text, prefix + "AgentObservedToolBindings.Service", "AgentRpcServices.ObservedToolExecutions");
        return Replace(text, "var description = await " + prefix + "AgentToolBindings.DescribeAsync(source, cancellationToken).ConfigureAwait(false);", """
            var description = source is AgentToolSourceRpcClient rpcSource
                        ? await rpcSource.DescribeAsync(cancellationToken).ConfigureAwait(false)
                        : new ToolSourceDescription(source.SourceId, source.DisplayName, source.SourceKind);
            """);

        static string Replace(string source, string value, string original)
        {
            Assert.Equal(2, source.Split(value, StringSplitOptions.None).Length);
            return source.Replace(value, original, StringComparison.Ordinal);
        }
    }
}
