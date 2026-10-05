using System.Runtime.CompilerServices;
using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Protocol;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Chat_Provider;

namespace Sunder.Package.Agent.Provider.Gemini.Rpc;

[SunderRpcProvider(typeof(IChatService), ProviderId = "gemini.chat")]
internal sealed partial class ChatProvider(IAgentChatProvider provider) : IChatService
{
    public ValueTask<Wire.AgentProviderDescriptor> DescribeAsync(SunderRpcInvocationContext context, Wire.Unit request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(GeminiProviderValues.Convert<Wire.AgentProviderDescriptor>(provider.Descriptor)); }
    public async ValueTask<IReadOnlyList<Wire.AgentModelDescriptor>> ListModelsAsync(SunderRpcInvocationContext context, Wire.Unit request, CancellationToken token)
        => GeminiProviderValues.Convert<Wire.AgentModelDescriptor[]>(await provider.GetAvailableModelsAsync(token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentProviderReadiness> GetReadinessAsync(SunderRpcInvocationContext context, Wire.Unit request, CancellationToken token)
        => GeminiProviderValues.Convert<Wire.AgentProviderReadiness>(await provider.GetReadinessAsync(token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentProviderRunCapabilities> GetCapabilitiesAsync(SunderRpcInvocationContext context, Wire.ChatCapabilitiesRequest request, CancellationToken token)
        => GeminiProviderValues.Convert<Wire.AgentProviderRunCapabilities>(await provider.GetRunCapabilitiesAsync(request.ModelId, token).ConfigureAwait(false));
    public async ValueTask<string?> ResolveUtilityModelAsync(SunderRpcInvocationContext context, Wire.Unit request, CancellationToken token)
        => provider is IAgentUtilityModelProvider utility ? await utility.ResolveUtilityModelIdAsync(token).ConfigureAwait(false) : null;
    public async IAsyncEnumerable<Wire.AgentChatStreamEvent> StreamChatAsync(SunderRpcInvocationContext context, Wire.AgentChatStreamRequest request,
        [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var item in ChatStream.ReadAsync(provider, GeminiProviderValues.Convert<Domain.AgentChatStreamRequest>(request), context, token).ConfigureAwait(false))
            yield return GeminiProviderValues.Convert<Wire.AgentChatStreamEvent>(item, typeof(Domain.AgentChatStreamEvent));
    }
}
