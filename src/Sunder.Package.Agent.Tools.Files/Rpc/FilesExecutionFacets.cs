using Sunder.Package.Agent.Contracts.Contracts;

namespace Sunder.Package.Agent.Tools.Files;

internal static class FilesExecutionFacets
{
    internal static bool SupportsFacet(IAgentExecutionTarget target, string facet) => target.Descriptor.SupportsFacet(facet);
}
