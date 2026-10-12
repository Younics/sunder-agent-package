namespace Sunder.Package.Agent.Tests;

internal static partial class RegressionTestFamilyConsumers
{
    internal static GeneratedServiceTestScope Memory(this RegressionTestExtensionCatalog catalog)
        => catalog.Receiver("sunder.package.agent.memory.semantic");
}
