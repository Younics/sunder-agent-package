namespace Sunder.Package.Agent.Tools.Files;

internal static class FilesExecutionFacets
{
    internal static bool SupportsFacet(FileExecutionTarget target, string facet) => target.Descriptor.Facets.Contains(facet, StringComparer.Ordinal);
}
