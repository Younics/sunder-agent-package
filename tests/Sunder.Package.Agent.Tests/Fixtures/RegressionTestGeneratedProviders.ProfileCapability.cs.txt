using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using IProfileCapabilityConsumerService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Profile_Capability_Consumer.IProfileCapabilityConsumerService;
using Sunder.Sdk.Services;

namespace RegressionTestGeneratedProviders;

[SunderServiceProvider(typeof(IProfileCapabilityConsumerService), ProviderId = "fixture.regression.profile.capability")]
public sealed partial class ProfileCapabilityProvider(Func<string, Type, object?[], CancellationToken, object> invoke) : IProfileCapabilityConsumerService
{
    private ValueTask<T> Reply<T>(string operation, object?[] arguments, CancellationToken token) => (ValueTask<T>)invoke(operation, typeof(T), arguments, token);
    public ValueTask<AgentProfileCapabilityConsumerSnapshot> DescribeAsync(CancellationToken token = default) => Reply<AgentProfileCapabilityConsumerSnapshot>(nameof(DescribeAsync), [], token);
}
