extern alias SkillsWorker;
extern alias SubagentsWorker;
extern alias McpWorker;

using SkillsRpc = SkillsWorker::Sunder.Package.Agent.Skills.Rpc;
using SubagentsRpc = SubagentsWorker::Sunder.Package.Agent.Subagents.Rpc;

namespace Sunder.Package.Agent.Tests;

internal static partial class RegressionTestFamilyConsumers
{
    internal static McpWorker::Sunder.Package.Agent.Mcp.Rpc.RuntimeWorkspaceCatalog McpWorkspaces(this RegressionTestExtensionCatalog catalog)
        => catalog.OwnConsumer("sunder.package.agent.mcp", rpc =>
        {
            var value = new McpWorker::Sunder.Package.Agent.Mcp.Rpc.RuntimeWorkspaceCatalog(rpc);
            value.StartAsync().GetAwaiter().GetResult();
            return value;
        });

    internal static SubagentsRpc.SubagentRuntimeBindings Subagents(this RegressionTestExtensionCatalog catalog)
        => catalog.OwnConsumer("sunder.package.agent.subagents", rpc =>
        {
            var value = new SubagentsRpc.SubagentRuntimeBindings(rpc);
            value.StartAsync().GetAwaiter().GetResult();
            return value;
        });

    internal static SkillsRpc.SkillsRuntimeBindings Skills(this RegressionTestExtensionCatalog catalog)
        => catalog.OwnConsumer("sunder.package.agent.skills", rpc =>
        {
            var value = new SkillsRpc.SkillsRuntimeBindings(rpc);
            value.StartAsync().GetAwaiter().GetResult();
            return value;
        });
}
