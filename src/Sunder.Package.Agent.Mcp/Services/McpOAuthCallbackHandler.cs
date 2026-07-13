using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Callbacks;

namespace Sunder.Package.Agent.Mcp.Services;

internal sealed class McpOAuthCallbackHandler(
    McpServerCatalogService catalog,
    McpOAuthService oauth,
    IPackageContext packageContext) : IPackageCallbackHandler
{
    internal const string HandlerId = "mcp.oauth.v1";
    internal const string ServerIdParameter = "serverId";

    public string CallbackHandlerId => HandlerId;

    public async Task<PackageCallbackStartResult?> StartCallbackAsync(
        PackageCallbackStartContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Parameters.Count != 1
            || !context.Parameters.TryGetValue(ServerIdParameter, out var serverId)
            || string.IsNullOrWhiteSpace(serverId))
        {
            throw new InvalidOperationException("A valid MCP serverId callback parameter is required.");
        }
        var server = await catalog.GetServerAsync(serverId, cancellationToken)
            ?? throw new InvalidOperationException("The selected MCP server was not found.");
        var authorizationUri = await oauth.StartAuthorizationAsync(
            server,
            context.CallbackSessionId,
            context.CallbackUri,
            cancellationToken);
        return new PackageCallbackStartResult(
            packageContext.PackageId,
            context.CallbackSessionId,
            PackageCallbackFlowKind.Browser,
            authorizationUri.AbsoluteUri,
            $"Authorize MCP server '{server.DisplayName}' in your browser.");
    }

    public async Task<PackageCallbackCompletionResult> CompleteCallbackAsync(
        PackageCallbackCompletionContext context,
        CancellationToken cancellationToken = default)
    {
        var completion = await oauth.CompleteAsync(context.CallbackSessionId, context.QueryValues, cancellationToken);
        return new PackageCallbackCompletionResult(
            packageContext.PackageId,
            context.CallbackSessionId,
            completion.Success ? PackageCallbackCompletionState.Completed : PackageCallbackCompletionState.Failed,
            completion.Message);
    }

    public Task CancelCallbackAsync(
        PackageCallbackCancellationContext context,
        CancellationToken cancellationToken = default)
        => oauth.CancelAsync(context.CallbackSessionId);
}
