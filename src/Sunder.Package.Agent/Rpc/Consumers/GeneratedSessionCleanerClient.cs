using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Session_Cleaner;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal sealed class GeneratedSessionCleanerClient(ISunderRpcClient rpc, SunderRpcProviderSnapshot provider)
    : IAgentSessionDataCleaner, IDisposable, IAsyncDisposable
{
    private readonly GeneratedProviderCallLifetime _calls = new(rpc, provider, "agent.session-cleaner.provider-stale");
    private string? _cleanerId;

    public string CleanerId
    {
        get
        {
            _calls.ThrowIfStopped();
            return _cleanerId ??= _calls.Invoke(async (scope, token) => (await new PackageApi(scope).SessionCleaner
                .CreateClient(scope, new Api.Provider(provider)).SSession_2dcleaner.DescribeAsync(new(), token).ConfigureAwait(false)).Id,
                default).AsTask().GetAwaiter().GetResult();
        }
    }

    public void DeleteSessionData(Guid sessionId)
        => _ = _calls.Invoke((scope, token) => new PackageApi(scope).SessionCleaner
            .CreateClient(scope, new Api.Provider(provider)).SSession_2dcleaner.DeleteSessionAsync(new() { SessionId = sessionId }, token),
            default).AsTask().GetAwaiter().GetResult();

    public void Dispose() => _calls.Dispose();
    public ValueTask DisposeAsync() => _calls.DisposeAsync();
}
