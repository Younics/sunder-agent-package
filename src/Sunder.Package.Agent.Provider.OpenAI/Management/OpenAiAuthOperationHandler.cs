using Sunder.Package.Agent.Provider.OpenAI.Auth;

namespace Sunder.Package.Agent.Provider.OpenAI;

internal sealed class OpenAiAuthOperationHandler(CodexConnectedAuthStrategy authStrategy)
{
    internal async ValueTask<OpenAiAuthOperationResult> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var activeSession = await authStrategy.TryEnsureAuthenticatedSilentlyAsync(cancellationToken)
            .ConfigureAwait(false);
        var cachedSession = await authStrategy.GetCachedSessionAsync(cancellationToken).ConfigureAwait(false);
        return new OpenAiAuthOperationResult(
            activeSession is not null,
            cachedSession is not null,
            activeSession?.ExpiresAtUtc);
    }

    internal async ValueTask<OpenAiAuthOperationResult> DisconnectAsync(
        CancellationToken cancellationToken = default)
    {
        await authStrategy.ClearSessionAsync(cancellationToken).ConfigureAwait(false);
        return new OpenAiAuthOperationResult(false, false, null);
    }
}
