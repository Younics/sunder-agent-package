using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Profile_Capability_Consumer;
using Wire = Sunder.Generated.Rpc.ProfileCapabilityConsumer;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal sealed class GeneratedProfileCapabilityClient(ISunderRpcClient rpc, SunderRpcProviderSnapshot provider)
    : IAgentProfileCapabilityConsumer, IDisposable, IAsyncDisposable
{
    private readonly GeneratedProviderCallLifetime _calls = new(rpc, provider, "agent.capability.provider-stale");
    private Wire.ProfileCapabilityConsumerSnapshot? _snapshot;
    private Wire.ProfileCapabilityConsumerSnapshot Snapshot
    {
        get
        {
            _calls.ThrowIfStopped();
            return _snapshot ??= _calls.Invoke((scope, token) => new PackageApi(scope).ProfileCapabilityConsumer
                .CreateClient(scope, new Api.Provider(provider)).SProfile_2dcapability_2dconsumer.DescribeAsync(new(), token),
                default).AsTask().GetAwaiter().GetResult();
        }
    }
    public string ConsumerId => Snapshot.ConsumerId;
    public string DisplayName => Snapshot.DisplayName;
    public IReadOnlyList<AgentProfileCapabilityConsumerDescriptor> ListConsumedCapabilities()
        => Snapshot.Capabilities.Select(value => new AgentProfileCapabilityConsumerDescriptor(value.CapabilityKind, value.DisplayName, value.Description)).ToArray();

    public void Dispose() => _calls.Dispose();
    public ValueTask DisposeAsync() => _calls.DisposeAsync();
}
