extern alias MemoryWorker;

using MemoryRpc = MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Rpc;

namespace Sunder.Package.Agent.Tests;

internal static partial class RegressionTestFamilyConsumers
{
    internal static MemoryRpc.SemanticMemoryRpcCatalog Memory(this RegressionTestExtensionCatalog catalog)
        => catalog.OwnConsumer("sunder.package.agent.memory.semantic", rpc =>
        {
            var value = new MemoryRpc.SemanticMemoryRpcCatalog(rpc);
            value.StartAsync().GetAwaiter().GetResult();
            return value;
        });
}
