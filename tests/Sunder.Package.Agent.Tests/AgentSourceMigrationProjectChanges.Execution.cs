using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeAgentExecutionBindings(string path, string text)
    {
        var normalized = path.Replace('\\', '/');
        const string prefix = "Sunder.Package.Agent.Runtime.Rpc.AgentExecutionBindings.";
        if (normalized.EndsWith("/src/Sunder.Package.Agent/Services/AgentExecutionTargetService.cs", StringComparison.Ordinal))
        {
            Assert.Equal(3, text.Split(prefix + "Service", StringSplitOptions.None).Length);
            text = text.Replace(prefix + "Service", "AgentRpcServices.ExecutionTargets", StringComparison.Ordinal);
            return Replace(text, "var descriptor = await " + prefix + "DescribeAsync(target, cancellationToken).ConfigureAwait(false);", """
                var descriptor = target is AgentExecutionTargetRpcClient rpcTarget
                            ? await rpcTarget.DescribeAsync(cancellationToken).ConfigureAwait(false)
                            : target.Descriptor;
                """);
        }
        if (normalized.EndsWith("/src/Sunder.Package.Agent/Services/AgentWorkspaceStackContributor.cs", StringComparison.Ordinal))
            return Replace(text, prefix + "Service", "AgentRpcServices.ExecutionTargets");
        if (normalized.EndsWith("/src/Sunder.Package.Agent/Services/AgentToolService.Invocation.cs", StringComparison.Ordinal))
        {
            text = Replace(text, prefix + "SupportsFacet(", "AgentExecutionTargetRpc.SupportsFacet(");
            return Replace(text, "var descriptor = await " + prefix + "DescribeAsync(lease.Service, invocation.Token).ConfigureAwait(false);", """
                var descriptor = lease.Service is AgentExecutionTargetRpcClient rpcTarget
                                ? await rpcTarget.DescribeAsync(invocation.Token).ConfigureAwait(false)
                                : lease.Service.Descriptor;
                """);
        }
        return text;

        string Replace(string source, string value, string original)
        {
            var count = source.Split(value, StringSplitOptions.None).Length - 1;
            Assert.True(count == 1, $"{path}: expected one pre-natural execution occurrence of '{value}', found {count}.");
            return source.Replace(value, original, StringComparison.Ordinal);
        }
    }
}
