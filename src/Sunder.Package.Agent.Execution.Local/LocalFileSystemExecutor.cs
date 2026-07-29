using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Local;

internal static class LocalFileSystemExecutor
{
    internal static int MutationGateCount => HostFileSystemExecutor.MutationGateCount;

    public static async ValueTask<AgentFileReadResult> ReadFileAsync(
        LocalExecutionRuntimeConfig config,
        AgentFileReadRequest request,
        bool allowOutsideConfiguredScope,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? approvedResourceReferences = null,
        ILocalSecureFileSystemHooks? hooks = null,
        AgentExecutionTargetContext? authorizationContext = null,
        LocalResourceReference? resourceReferences = null)
    {
        if (!FileOperation.TryValidateRange(request.Offset, request.Limit, out var rangeError))
        {
            return AgentFileReadResult.Failure(
                request.Path,
                AgentFileReadErrorCodes.InvalidRange,
                rangeError!);
        }
        try
        {
            return await HostFileSystemExecutor.ReadFileAsync(
                ResolveContext(
                    config,
                    request.Path,
                    allowOutsideConfiguredScope,
                    approvedResourceReferences,
                    authorizationContext,
                    cancellationToken,
                    resourceReferences: resourceReferences),
                request,
                cancellationToken,
                hooks);
        }
        catch (Exception ex) when (ex is LocalResourceReapprovalRequiredException or LocalSecureApprovalChangedException)
        {
            return AgentFileReadResult.Failure(
                request.Path,
                LocalResourceReference.ReapprovalRequiredErrorCode,
                ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return AgentFileReadResult.Failure(
                request.Path,
                AgentFileReadErrorCodes.OutsideConfiguredScope,
                ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AgentFileReadResult.Failure(
                request.Path,
                AgentFileReadErrorCodes.PathCanonicalizationFailed,
                ex.Message);
        }
    }

    public static async ValueTask<AgentFileMutationResult> WriteFileAsync(
        LocalExecutionRuntimeConfig config,
        AgentFileWriteRequest request,
        bool allowOutsideConfiguredScope,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? approvedResourceReferences = null,
        ILocalFileWriteFaultInjector? faultInjector = null,
        ILocalSecureFileSystemHooks? hooks = null,
        AgentExecutionTargetContext? authorizationContext = null,
        LocalResourceReference? resourceReferences = null)
    {
        LocalSecureApprovalLease? postMutationAuthority = null;
        try
        {
            var result = await HostFileSystemExecutor.WriteFileAsync(
                ResolveContext(
                    config,
                    request.Path,
                    allowOutsideConfiguredScope,
                    approvedResourceReferences,
                    authorizationContext,
                    cancellationToken,
                    authorizationContext?.CapturePostMutationResource == true
                        ? authority => postMutationAuthority = authority
                        : null,
                    resourceReferences),
                request,
                cancellationToken,
                faultInjector,
                hooks);
            return AttachPostMutationResource(
                config,
                request.Path,
                authorizationContext,
                result,
                ref postMutationAuthority,
                resourceReferences);
        }
        catch (Exception ex) when (ex is LocalResourceReapprovalRequiredException or LocalSecureApprovalChangedException)
        {
            return FileOperation.Failure(
                request.Path,
                ex.Message,
                LocalResourceReference.ReapprovalRequiredErrorCode);
        }
        catch (InvalidOperationException ex)
        {
            return FileOperation.Failure(
                request.Path,
                ex.Message,
                AgentFileReadErrorCodes.OutsideConfiguredScope);
        }
        catch (Exception ex) when (
            faultInjector is null
            && (ex is IOException or UnauthorizedAccessException))
        {
            return FileOperation.Failure(
                request.Path,
                ex.Message,
                AgentFileReadErrorCodes.PathCanonicalizationFailed);
        }
        finally
        {
            postMutationAuthority?.Dispose();
        }
    }

    public static async ValueTask<AgentFileMutationResult> DeleteFileAsync(
        LocalExecutionRuntimeConfig config,
        AgentFileDeleteRequest request,
        bool allowOutsideConfiguredScope,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? approvedResourceReferences = null,
        ILocalSecureFileSystemHooks? hooks = null,
        AgentExecutionTargetContext? authorizationContext = null,
        LocalResourceReference? resourceReferences = null)
    {
        LocalSecureApprovalLease? postMutationAuthority = null;
        try
        {
            var result = await HostFileSystemExecutor.DeleteFileAsync(
                ResolveContext(
                    config,
                    request.Path,
                    allowOutsideConfiguredScope,
                    approvedResourceReferences,
                    authorizationContext,
                    cancellationToken,
                    authorizationContext?.CapturePostMutationResource == true
                        ? authority => postMutationAuthority = authority
                        : null,
                    resourceReferences),
                request,
                cancellationToken,
                hooks);
            return AttachPostMutationResource(
                config,
                request.Path,
                authorizationContext,
                result,
                ref postMutationAuthority,
                resourceReferences);
        }
        catch (Exception ex) when (ex is LocalResourceReapprovalRequiredException or LocalSecureApprovalChangedException)
        {
            return FileOperation.Failure(
                request.Path,
                ex.Message,
                LocalResourceReference.ReapprovalRequiredErrorCode);
        }
        catch (InvalidOperationException ex)
        {
            return FileOperation.Failure(
                request.Path,
                ex.Message,
                AgentFileReadErrorCodes.OutsideConfiguredScope);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FileOperation.Failure(
                request.Path,
                ex.Message,
                AgentFileReadErrorCodes.PathCanonicalizationFailed);
        }
        finally
        {
            postMutationAuthority?.Dispose();
        }
    }

    internal static HostFileSystemPathContext ResolveContext(
        LocalExecutionRuntimeConfig config,
        string requestedPath,
        bool allowOutsideConfiguredScope,
        IReadOnlyList<string>? approvedResourceReferences,
        AgentExecutionTargetContext? authorizationContext = null,
        CancellationToken cancellationToken = default,
        Action<LocalSecureApprovalLease>? postMutationAuthoritySink = null,
        LocalResourceReference? resourceReferences = null)
    {
        var fullPath = LocalSecurePathEngine.ResolveLexicalPath(config, requestedPath);
        return new HostFileSystemPathContext(
            config.WorkspacePaths,
            fullPath,
            fullPath,
            LocalSecurePathEngine.ResolveApprovedAuthority(
                config,
                fullPath,
                requestedPath,
                allowOutsideConfiguredScope,
                approvedResourceReferences,
                authorizationContext,
                cancellationToken,
                resourceReferences),
            PostMutationAuthoritySink: postMutationAuthoritySink);
    }

    private static AgentFileMutationResult AttachPostMutationResource(
        LocalExecutionRuntimeConfig config,
        string path,
        AgentExecutionTargetContext? authorizationContext,
        AgentFileMutationResult result,
        ref LocalSecureApprovalLease? postMutationAuthority,
        LocalResourceReference? resourceReferences)
    {
        if (postMutationAuthority is null || authorizationContext is null)
        {
            return result;
        }

        var authority = postMutationAuthority;
        postMutationAuthority = null;
        return result with
        {
            PostMutationResource = LocalResourceResolver.ResolvePostMutationResource(
                config,
                path,
                authorizationContext,
                authority,
                resourceReferences ?? throw new InvalidOperationException(
                    "Post-mutation Local authority requires an activation-owned capability store.")),
        };
    }
}
