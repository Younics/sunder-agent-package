using Sunder.Package.Agent.Provider.OpenAI.Auth;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Provider.OpenAI;

internal static class OpenAiRuntimeOperations
{
    internal static readonly PackageRuntimeOperation<OpenAiAuthOperationRequest, OpenAiAuthOperationResult> Auth =
        new("openai.auth.v1");
}

internal enum OpenAiAuthOperationKind
{
    GetStatus,
    Disconnect,
}

internal sealed record OpenAiAuthOperationRequest(OpenAiAuthOperationKind Kind);

internal sealed record OpenAiAuthOperationResult(
    bool IsConnected,
    bool HasCachedSession,
    DateTimeOffset? ExpiresAtUtc,
    string? ErrorMessage = null);

internal sealed class OpenAiAuthOperationHandler(CodexConnectedAuthStrategy authStrategy)
    : IPackageRuntimeOperationHandler<OpenAiAuthOperationRequest, OpenAiAuthOperationResult>
{
    public async ValueTask<OpenAiAuthOperationResult> HandleAsync(
        OpenAiAuthOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return request.Kind switch
        {
            OpenAiAuthOperationKind.GetStatus => await GetStatusAsync(cancellationToken).ConfigureAwait(false),
            OpenAiAuthOperationKind.Disconnect => await DisconnectAsync(cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unknown OpenAI auth operation."),
        };
    }

    private async Task<OpenAiAuthOperationResult> GetStatusAsync(CancellationToken cancellationToken)
    {
        OpenAiCodexSession? activeSession = null;
        string? errorMessage = null;
        try
        {
            activeSession = await authStrategy.TryEnsureAuthenticatedSilentlyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }

        var cachedSession = await authStrategy.GetCachedSessionAsync(cancellationToken).ConfigureAwait(false);
        return new OpenAiAuthOperationResult(
            activeSession is not null,
            cachedSession is not null,
            activeSession?.ExpiresAtUtc,
            errorMessage);
    }

    private async Task<OpenAiAuthOperationResult> DisconnectAsync(CancellationToken cancellationToken)
    {
        await authStrategy.ClearSessionAsync(cancellationToken).ConfigureAwait(false);
        return new OpenAiAuthOperationResult(false, false, null);
    }

}

internal sealed class OpenAiAuthPresentationService(IPackageRuntimeClient runtimeClient)
{
    internal ValueTask<OpenAiAuthOperationResult> GetStatusAsync(CancellationToken cancellationToken = default)
        => InvokeAsync(OpenAiAuthOperationKind.GetStatus, cancellationToken);

    internal ValueTask<OpenAiAuthOperationResult> DisconnectAsync(CancellationToken cancellationToken = default)
        => InvokeAsync(OpenAiAuthOperationKind.Disconnect, cancellationToken);

    private ValueTask<OpenAiAuthOperationResult> InvokeAsync(
        OpenAiAuthOperationKind kind,
        CancellationToken cancellationToken)
        => runtimeClient.InvokeAsync(
            OpenAiRuntimeOperations.Auth,
            new OpenAiAuthOperationRequest(kind),
            cancellationToken);
}
