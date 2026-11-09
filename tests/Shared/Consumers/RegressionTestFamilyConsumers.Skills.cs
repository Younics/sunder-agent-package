extern alias SkillsWorker;

using Sunder.Sdk.Services;
using SkillsRuntimeCatalogService = SkillsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Runtime_Catalog.IRuntimeCatalogService;

namespace Sunder.Package.Agent.Tests;

internal static partial class RegressionTestFamilyConsumers
{
    internal static IServiceProviders<SkillsRuntimeCatalogService> Skills(this RegressionTestExtensionCatalog catalog)
        => catalog.Receiver("younics.ai.agent.skills").Providers<SkillsRuntimeCatalogService>();
}
