using System.Globalization;
using Sunder.Package.Agent.Provider.OpenAI.Auth;
using Sunder.Package.Agent.Provider.OpenAI.Protocol.Generated;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Provider.OpenAI;

internal sealed class OpenAiManagementRpcProvider(
    OpenAiAuthOperationHandler auth,
    ProviderCredentialAccessor credentials) : IManagementProvider
{
    public async ValueTask<AuthStatus> GetAuthStatusAsync(
        SunderRpcInvocationContext context,
        ManagementQuery request,
        CancellationToken cancellationToken = default)
        => ToWire(await auth.GetStatusAsync(cancellationToken));

    public async ValueTask<AuthStatus> DisconnectAuthAsync(
        SunderRpcInvocationContext context,
        ManagementQuery request,
        CancellationToken cancellationToken = default)
        => ToWire(await auth.DisconnectAsync(cancellationToken));

    public async ValueTask<CredentialStatus> GetCredentialStatusAsync(
        SunderRpcInvocationContext context,
        ManagementQuery request,
        CancellationToken cancellationToken = default)
        => new()
        {
            HasStoredCredential = await credentials.HasCredentialAsync(cancellationToken),
        };

    public async ValueTask<CredentialStatus> SetCredentialAsync(
        SunderRpcInvocationContext context,
        CredentialRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Credential))
        {
            throw Domain("A non-empty OpenAI API key is required.");
        }
        await credentials.SetCredentialAsync(request.Credential, cancellationToken);
        return new CredentialStatus { HasStoredCredential = true };
    }

    public async ValueTask<CredentialStatus> ClearCredentialAsync(
        SunderRpcInvocationContext context,
        ManagementQuery request,
        CancellationToken cancellationToken = default)
    {
        await credentials.DeleteCredentialAsync(cancellationToken);
        return new CredentialStatus { HasStoredCredential = false };
    }

    private static AuthStatus ToWire(OpenAiAuthOperationResult result)
        => new()
        {
            IsConnected = result.IsConnected,
            HasCachedSession = result.HasCachedSession,
            ExpiresAtUtc = result.ExpiresAtUtc is { } expiry
                ? new SunderRpcOffsetDateTime(expiry.ToUniversalTime().ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
                    CultureInfo.InvariantCulture))
                : null,
            ErrorMessage = Bound(result.ErrorMessage),
        };

    private static SunderRpcException Domain(string message)
        => new(new SunderRpcError(
            SunderRpcErrorKind.Domain,
            "openai.management.failed",
            message));

    private static string? Bound(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= 512
                ? value
                : value[..512];
}

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
