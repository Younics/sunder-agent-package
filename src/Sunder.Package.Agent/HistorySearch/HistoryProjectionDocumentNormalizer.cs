namespace Sunder.Package.Agent.HistorySearch;

internal static class HistoryProjectionDocumentNormalizer
{
    internal static HistoryProjectionDocument Normalize(HistoryProjectionDocument document)
    {
        var facets = document.Facets
            .Select(NormalizeFacet)
            .Where(static facet => facet is not null)
            .Select(static facet => facet!)
            .DistinctBy(static facet => (facet.Kind, facet.NormalizedValue))
            .Take(64)
            .ToArray();
        return document with
        {
            WorkspaceName = NormalizeDisplay(document.WorkspaceName),
            SessionTitle = NormalizeDisplay(document.SessionTitle),
            BodyText = NormalizeText(document.BodyText, HistorySearchLimits.MaximumBodyCharacters),
            DisplaySnippet = NormalizeText(document.DisplaySnippet, HistorySearchLimits.StoredSnippetCharacters),
            Facets = facets,
        };
    }

    private static HistoryProjectionFacet? NormalizeFacet(HistoryProjectionFacet facet)
    {
        var kind = HistorySearchText.BoundAtRuneBoundary(
                HistorySearchText.NormalizeForSearch(facet.Kind).Trim(),
                HistorySearchLimits.MaximumFilterCharacters)
            .Trim();
        var value = NormalizeText(facet.Value, HistorySearchLimits.MaximumFacetCharacters);
        if (kind.Length == 0 || value.Length == 0)
        {
            return null;
        }
        return new HistoryProjectionFacet(kind, value, HistorySearchText.NormalizeFacet(value));
    }

    private static string NormalizeDisplay(string? value)
        => NormalizeText(value, HistorySearchLimits.MaximumDisplayCharacters);

    private static string NormalizeText(string? value, int maximumCharacters)
        => HistorySearchText.BoundAtRuneBoundary(
            HistorySearchText.NormalizeStoredText(value).Trim(),
            maximumCharacters);
}
