using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using ISelectableCapabilityProviderService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Selectable_Capability_Provider.ISelectableCapabilityProviderService;
using Sunder.Sdk.Services;

namespace RegressionTestGeneratedProviders;

[SunderServiceProvider(typeof(ISelectableCapabilityProviderService), ProviderId = "fixture.regression.selectable.capability")]
public sealed partial class SelectableCapabilityProvider(Func<string, Type, object?[], CancellationToken, object> invoke) : ISelectableCapabilityProviderService
{
    private ValueTask<T> Reply<T>(string operation, object?[] arguments, CancellationToken token) => (ValueTask<T>)invoke(operation, typeof(T), arguments, token);
    private IAsyncEnumerable<T> Stream<T>(string operation, object?[] arguments, CancellationToken token) => (IAsyncEnumerable<T>)invoke(operation, typeof(T), arguments, token);
    public ValueTask<AgentServiceIdentity> DescribeAsync(CancellationToken token = default) => Reply<AgentServiceIdentity>(nameof(DescribeAsync), [], token);
    public ValueTask<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListAsync(AgentProfileRecord? profile, CancellationToken token = default) => Reply<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>(nameof(ListAsync), [profile], token);
    public IAsyncEnumerable<AgentCapabilityCatalogChange> WatchAsync(CancellationToken token = default) => Stream<AgentCapabilityCatalogChange>(nameof(WatchAsync), [], token);
}
