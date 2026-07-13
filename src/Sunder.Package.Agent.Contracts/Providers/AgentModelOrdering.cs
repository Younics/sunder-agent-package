namespace Sunder.Package.Agent.Contracts.Models;

public static class AgentModelOrdering
{
    public static IOrderedEnumerable<AgentModelDescriptor> OrderNewestFirst(
        this IEnumerable<AgentModelDescriptor> models)
        => models
            .OrderByDescending(model => model.ReleaseDate.HasValue)
            .ThenByDescending(model => model.ReleaseDate);
}
