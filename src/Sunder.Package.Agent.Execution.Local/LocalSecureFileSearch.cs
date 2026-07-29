using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Local;

internal static class LocalSecureFileSearch
{
    public static async ValueTask<AgentFileSearchResult> ExecuteAsync(
        LocalExecutionRuntimeConfig config,
        AgentFileSearchRequest request,
        bool allowOutsideConfiguredScope,
        IReadOnlyList<string>? approvedResourceReferences,
        CancellationToken cancellationToken,
        ILocalSecureFileSystemHooks? hooks = null,
        AgentExecutionTargetContext? authorizationContext = null,
        LocalResourceReference? resourceReferences = null)
    {
        if (!HostSecureFileSearch.TryValidateRequest(request, out var validationError))
        {
            return AgentFileSearchResult.Failure(
                AgentFileSearchErrorCodes.InvalidRequest,
                validationError!);
        }
        try
        {
            return await HostSecureFileSearch.ExecuteAsync(
                LocalFileSystemExecutor.ResolveContext(
                    config,
                    request.Path,
                    allowOutsideConfiguredScope,
                    approvedResourceReferences,
                    authorizationContext,
                    cancellationToken,
                    resourceReferences: resourceReferences),
                request,
                cancellationToken,
                hooks).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is LocalResourceReapprovalRequiredException or LocalSecureApprovalChangedException)
        {
            return AgentFileSearchResult.Failure(
                LocalResourceReference.ReapprovalRequiredErrorCode,
                ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return AgentFileSearchResult.Failure(AgentFileSearchErrorCodes.OutsideConfiguredScope, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return AgentFileSearchResult.Failure(AgentFileSearchErrorCodes.PathUnresolvable, ex.Message);
        }
    }
}
