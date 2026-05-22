namespace Sunder.Package.Agent.Contracts.Contracts;

public interface IAgentUtilityModelProvider
{
    ValueTask<string?> ResolveUtilityModelIdAsync(CancellationToken cancellationToken = default);
}
