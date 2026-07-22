namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Provides common ordering operations for chat model catalogs.
/// </summary>
public static class AgentModelOrdering
{
    /// <summary>
    /// Orders dated models before undated models and dated models from newest to oldest.
    /// </summary>
    /// <remarks>
    /// The operation is deferred. Models with the same date, and models without dates, retain
    /// their relative order from <paramref name="models" />.
    /// </remarks>
    /// <param name="models">The model sequence to order.</param>
    /// <returns>An ordered view that enumerates the supplied sequence.</returns>
    public static IOrderedEnumerable<AgentModelDescriptor> OrderNewestFirst(
        this IEnumerable<AgentModelDescriptor> models)
        => models
            .OrderByDescending(model => model.ReleaseDate.HasValue)
            .ThenByDescending(model => model.ReleaseDate);
}
