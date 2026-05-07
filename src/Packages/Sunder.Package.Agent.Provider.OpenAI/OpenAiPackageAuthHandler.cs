using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Authentication;

namespace Sunder.Package.Agent.Provider.OpenAI;

public sealed class OpenAiPackageAuthHandler(
    IPackageContext packageContext,
    Auth.CodexConnectedAuthStrategy codexConnectedAuthStrategy) : IPackageAuthHandler
{
    private readonly IPackageContext _packageContext = packageContext;
    private readonly Auth.CodexConnectedAuthStrategy _codexConnectedAuthStrategy = codexConnectedAuthStrategy;

    public async ValueTask<PackageAuthStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var mode = _packageContext.Configuration.GetValue("auth.mode") ?? "codex-connected";
        if (!string.Equals(mode, _codexConnectedAuthStrategy.ModeId, StringComparison.OrdinalIgnoreCase))
        {
            return new PackageAuthStatus(
                "sunder.package.agent.provider.openai",
                PackageAuthStatusKind.Unavailable,
                "API-key mode is active. Codex authorization is not required.",
                CanAuthorize: false,
                CanDisconnect: false);
        }

        var session = await _codexConnectedAuthStrategy.TryEnsureAuthenticatedSilentlyAsync(cancellationToken);
        return session is null
            ? new PackageAuthStatus(
                "sunder.package.agent.provider.openai",
                PackageAuthStatusKind.NotConnected,
                "Not connected. Open Settings -> Packages -> Sunder Agent Provider OpenAI and click Authorize with ChatGPT Plus/Pro.",
                CanAuthorize: true,
                CanDisconnect: false)
            : new PackageAuthStatus(
                "sunder.package.agent.provider.openai",
                PackageAuthStatusKind.Connected,
                $"Connected. Session expires at {session.ExpiresAtUtc:O}.",
                CanAuthorize: true,
                CanDisconnect: true);
    }

    public Task<PackageAuthSessionStartResult?> StartAuthorizationAsync(PackageAuthSessionStartContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var mode = _packageContext.Configuration.GetValue("auth.mode") ?? "codex-connected";
        if (!string.Equals(mode, _codexConnectedAuthStrategy.ModeId, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<PackageAuthSessionStartResult?>(null);
        }

        var launchUrl = _codexConnectedAuthStrategy.CreateAuthorizationUrl(context.AuthSessionId, context.CallbackUri);
        return Task.FromResult<PackageAuthSessionStartResult?>(new PackageAuthSessionStartResult(
            "sunder.package.agent.provider.openai",
            context.AuthSessionId,
            PackageAuthFlowKind.Browser,
            launchUrl,
            "Browser authorization started. Finish sign-in in the opened browser window."
        ));
    }

    public async Task<PackageAuthStatus> CompleteAuthorizationAsync(PackageAuthSessionCompletionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var session = await _codexConnectedAuthStrategy.CompleteAuthorizationAsync(context.AuthSessionId, context.QueryValues, cancellationToken);
            return new PackageAuthStatus(
                "sunder.package.agent.provider.openai",
                PackageAuthStatusKind.Connected,
                $"Connected. Session expires at {session.ExpiresAtUtc:O}.",
                CanAuthorize: true,
                CanDisconnect: true);
        }
        catch (Exception ex)
        {
            return new PackageAuthStatus(
                "sunder.package.agent.provider.openai",
                PackageAuthStatusKind.Failed,
                ex.Message,
                CanAuthorize: true,
                CanDisconnect: false);
        }
    }

    public Task<PackageAuthStatus> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _codexConnectedAuthStrategy.ClearSession();
        return Task.FromResult(new PackageAuthStatus(
            "sunder.package.agent.provider.openai",
            PackageAuthStatusKind.NotConnected,
            "ChatGPT Plus/Pro OAuth session removed.",
            CanAuthorize: true,
            CanDisconnect: false));
    }
}
