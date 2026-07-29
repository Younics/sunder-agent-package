using System.Security.Cryptography;
using System.Text;
using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Docker;

internal sealed class DockerFileSystemExecutor
{
    internal const string ClaimNamespacePrefix = "docker-host-resource-claim-v1:";

    public ValueTask<AgentFileReadResult> ReadFileAsync(
        DockerExecutionRuntimeConfig config,
        AgentFileReadRequest request,
        CancellationToken cancellationToken,
        ILocalSecureFileSystemHooks? hooks = null)
        => ReadFileAsync(config, request, approvalLease: null, cancellationToken, hooks);

    public ValueTask<AgentFileMutationResult> WriteFileAsync(
        DockerExecutionRuntimeConfig config,
        AgentFileWriteRequest request,
        CancellationToken cancellationToken,
        ILocalSecureFileSystemHooks? hooks = null)
        => WriteFileAsync(config, request, approvalLease: null, cancellationToken, hooks);

    public ValueTask<AgentFileMutationResult> DeleteFileAsync(
        DockerExecutionRuntimeConfig config,
        AgentFileDeleteRequest request,
        CancellationToken cancellationToken,
        ILocalSecureFileSystemHooks? hooks = null)
        => DeleteFileAsync(config, request, approvalLease: null, cancellationToken, hooks);

    public ValueTask<AgentFileSearchResult> SearchAsync(
        DockerExecutionRuntimeConfig config,
        AgentFileSearchRequest request,
        CancellationToken cancellationToken,
        ILocalSecureFileSystemHooks? hooks = null)
        => SearchAsync(config, request, approvalLease: null, cancellationToken, hooks);

    public AgentResolvedResource ResolveFileResource(
        DockerExecutionRuntimeConfig config,
        string requestedPath,
        string namespaceFingerprint,
        CancellationToken cancellationToken,
        LocalSecureApprovalLease? retainedResourceAuthority = null,
        IReadOnlyList<DockerMountRootIdentityChain>? mountIdentityChains = null,
        AgentExecutionTargetContext? context = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resourceAuthority = retainedResourceAuthority;
        try
        {
            var path = DockerPathResolver.ResolveHostBinding(config, requestedPath);
            if (resourceAuthority is null)
            {
                var mountRoot = HostSecurePathEngine.OpenRoot(path.Mount.HostPath, cancellationToken: cancellationToken);
                try
                {
                    resourceAuthority = HostSecurePathEngine.CaptureFromRoot(
                        mountRoot,
                        path.HostPath,
                        cancellationToken: cancellationToken,
                        allowMissingSuffix: true);
                }
                catch
                {
                    mountRoot.Dispose();
                    throw;
                }
            }
            if (!HostSecurePathEngine.BindingPathEquals(resourceAuthority.Binding, path.HostPath))
            {
                throw new LocalSecureApprovalChangedException(path.ContainerPath);
            }
            var resourceBinding = resourceAuthority.Binding;
            var claim = HostResourceClaim.Create(
                ClaimNamespacePrefix + namespaceFingerprint,
                path.ContainerPath,
                path.Mount.ContainerPath,
                resourceAuthority,
                context?.ResourceOperation);
            if (context is not null)
            {
                claim = HostResourceClaim.BindScope(claim, context);
            }
            var reference = HostResourceClaim.CreateReference(claim);
            return new AgentResolvedResource(
                resourceBinding.TargetKind == LocalSecureNodeKind.Directory ? "directory" : "file",
                path.ContainerPath,
                reference,
                AgentPermissionBoundaryIds.ConfiguredScope,
                resourceBinding.Exists)
            {
                ResourceClaim = claim,
                DeleteCanonicalReference = reference,
                DeleteResourceClaim = claim,
                DeletePermissionBoundaryId = AgentPermissionBoundaryIds.ConfiguredScope,
                IsScopedInstructionDocument = string.Equals(
                    GetPosixName(path.ContainerPath),
                    "AGENTS.md",
                    StringComparison.Ordinal),
            };
        }
        finally
        {
            resourceAuthority?.Dispose();
        }
    }

    public ValueTask<AgentFileReadResult> ReadFileAsync(
        DockerExecutionRuntimeConfig config,
        AgentFileReadRequest request,
        LocalSecureApprovalLease operationAuthority,
        CancellationToken cancellationToken,
        ILocalSecureFileSystemHooks? hooks = null)
        => ReadFileWithAuthorityAsync(config, request, operationAuthority, cancellationToken, hooks);

    public ValueTask<AgentFileMutationResult> WriteFileAsync(
        DockerExecutionRuntimeConfig config,
        AgentFileWriteRequest request,
        LocalSecureApprovalLease operationAuthority,
        CancellationToken cancellationToken,
        ILocalSecureFileSystemHooks? hooks = null,
        Action<LocalSecureApprovalLease>? postMutationAuthoritySink = null)
        => WriteFileWithAuthorityAsync(
            config,
            request,
            operationAuthority,
            cancellationToken,
            hooks,
            postMutationAuthoritySink);

    public ValueTask<AgentFileMutationResult> DeleteFileAsync(
        DockerExecutionRuntimeConfig config,
        AgentFileDeleteRequest request,
        LocalSecureApprovalLease operationAuthority,
        IReadOnlyList<DockerMountRootIdentityChain> mountIdentityChains,
        CancellationToken cancellationToken,
        ILocalSecureFileSystemHooks? hooks = null,
        Action<LocalSecureApprovalLease>? postMutationAuthoritySink = null)
        => DeleteFileWithAuthorityAsync(
            config,
            request,
            operationAuthority,
            mountIdentityChains,
            cancellationToken,
            hooks,
            postMutationAuthoritySink);

    public ValueTask<AgentFileSearchResult> SearchAsync(
        DockerExecutionRuntimeConfig config,
        AgentFileSearchRequest request,
        LocalSecureApprovalLease operationAuthority,
        CancellationToken cancellationToken,
        ILocalSecureFileSystemHooks? hooks = null)
        => SearchWithAuthorityAsync(config, request, operationAuthority, cancellationToken, hooks);

    private async ValueTask<AgentFileReadResult> ReadFileWithAuthorityAsync(
        DockerExecutionRuntimeConfig config,
        AgentFileReadRequest request,
        LocalSecureApprovalLease operationAuthority,
        CancellationToken cancellationToken,
        ILocalSecureFileSystemHooks? hooks)
    {
        try
        {
            var path = DockerPathResolver.ResolveHostBinding(config, request.Path);
            return await HostFileSystemExecutor.ReadFileAsync(
                CreateContext(path, operationAuthority),
                request,
                cancellationToken,
                hooks).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            operationAuthority.Dispose();
            return AgentFileReadResult.Failure(
                request.Path,
                AgentFileReadErrorCodes.PathCanonicalizationFailed,
                ex.Message);
        }
    }

    private async ValueTask<AgentFileMutationResult> WriteFileWithAuthorityAsync(
        DockerExecutionRuntimeConfig config,
        AgentFileWriteRequest request,
        LocalSecureApprovalLease operationAuthority,
        CancellationToken cancellationToken,
        ILocalSecureFileSystemHooks? hooks,
        Action<LocalSecureApprovalLease>? postMutationAuthoritySink)
    {
        try
        {
            var path = DockerPathResolver.ResolveHostBinding(config, request.Path);
            return await HostFileSystemExecutor.WriteFileAsync(
                CreateContext(path, operationAuthority, postMutationAuthoritySink),
                request,
                cancellationToken,
                hooks: hooks).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            operationAuthority.Dispose();
            return FileOperation.Failure(
                request.Path,
                ex.Message,
                AgentFileReadErrorCodes.PathCanonicalizationFailed);
        }
    }

    private async ValueTask<AgentFileMutationResult> DeleteFileWithAuthorityAsync(
        DockerExecutionRuntimeConfig config,
        AgentFileDeleteRequest request,
        LocalSecureApprovalLease operationAuthority,
        IReadOnlyList<DockerMountRootIdentityChain> mountIdentityChains,
        CancellationToken cancellationToken,
        ILocalSecureFileSystemHooks? hooks,
        Action<LocalSecureApprovalLease>? postMutationAuthoritySink)
    {
        try
        {
            var path = DockerPathResolver.ResolveHostBinding(config, request.Path);
            var targetIdentity = operationAuthority.Binding.TargetIdentity;
            if (DockerPathResolver.IsConfiguredMountRootOrAncestor(config, path.ContainerPath)
                || targetIdentity is not null
                   && mountIdentityChains.Any(chain => chain.Identities.Contains(targetIdentity.Value)))
            {
                operationAuthority.Dispose();
                return FileOperation.Failure(
                    request.Path,
                    DockerPathResolver.StructuredRootDeleteMessage,
                    DockerPathResolver.StructuredRootDeleteErrorCode);
            }
            return await HostFileSystemExecutor.DeleteFileAsync(
                CreateContext(path, operationAuthority, postMutationAuthoritySink),
                request,
                cancellationToken,
                hooks).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            operationAuthority.Dispose();
            return FileOperation.Failure(
                request.Path,
                ex.Message,
                AgentFileReadErrorCodes.PathCanonicalizationFailed);
        }
    }

    private async ValueTask<AgentFileSearchResult> SearchWithAuthorityAsync(
        DockerExecutionRuntimeConfig config,
        AgentFileSearchRequest request,
        LocalSecureApprovalLease operationAuthority,
        CancellationToken cancellationToken,
        ILocalSecureFileSystemHooks? hooks)
    {
        try
        {
            var path = DockerPathResolver.ResolveHostBinding(config, request.Path);
            return await HostSecureFileSearch.ExecuteAsync(
                CreateContext(path, operationAuthority),
                request,
                cancellationToken,
                hooks).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            operationAuthority.Dispose();
            return AgentFileSearchResult.Failure(AgentFileSearchErrorCodes.PathUnresolvable, ex.Message);
        }
    }

    public async ValueTask<AgentFileReadResult> ReadFileAsync(
        DockerExecutionRuntimeConfig config,
        AgentFileReadRequest request,
        DockerApprovedResourceLease? approvalLease,
        CancellationToken cancellationToken,
        ILocalSecureFileSystemHooks? hooks = null)
    {
        try
        {
            var path = DockerPathResolver.ResolveHostBinding(config, request.Path);
            return await HostFileSystemExecutor.ReadFileAsync(
                CreateContext(path, approvalLease, cancellationToken),
                request,
                cancellationToken,
                hooks).ConfigureAwait(false);
        }
        catch (DockerStructuredBindRequiredException ex)
        {
            return AgentFileReadResult.Failure(
                request.Path,
                ex.ErrorCode,
                ex.Message);
        }
        catch (DockerResourceApprovalException ex)
        {
            return AgentFileReadResult.Failure(
                request.Path,
                DockerResourceReference.ApprovalRequiredErrorCode,
                ex.Message);
        }
        catch (InvalidOperationException)
        {
            return BindRequiredReadFailure(request.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return AgentFileReadResult.Failure(
                request.Path,
                AgentFileReadErrorCodes.PathCanonicalizationFailed,
                ex.Message);
        }
    }

    public async ValueTask<AgentFileMutationResult> WriteFileAsync(
        DockerExecutionRuntimeConfig config,
        AgentFileWriteRequest request,
        DockerApprovedResourceLease? approvalLease,
        CancellationToken cancellationToken,
        ILocalSecureFileSystemHooks? hooks = null)
    {
        try
        {
            var path = DockerPathResolver.ResolveHostBinding(config, request.Path);
            return await HostFileSystemExecutor.WriteFileAsync(
                CreateContext(path, approvalLease, cancellationToken),
                request,
                cancellationToken,
                hooks: hooks).ConfigureAwait(false);
        }
        catch (DockerStructuredBindRequiredException ex)
        {
            return FileOperation.Failure(request.Path, ex.Message, ex.ErrorCode);
        }
        catch (DockerResourceApprovalException ex)
        {
            return FileOperation.Failure(
                request.Path,
                ex.Message,
                DockerResourceReference.ApprovalRequiredErrorCode);
        }
        catch (InvalidOperationException)
        {
            return BindRequiredMutationFailure(request.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return FileOperation.Failure(
                request.Path,
                ex.Message,
                AgentFileReadErrorCodes.PathCanonicalizationFailed);
        }
    }

    public async ValueTask<AgentFileMutationResult> DeleteFileAsync(
        DockerExecutionRuntimeConfig config,
        AgentFileDeleteRequest request,
        DockerApprovedResourceLease? approvalLease,
        CancellationToken cancellationToken,
        ILocalSecureFileSystemHooks? hooks = null)
    {
        try
        {
            var path = DockerPathResolver.ResolveHostBinding(config, request.Path);
            if (DockerPathResolver.IsConfiguredMountRootOrAncestor(config, path.ContainerPath)
                || (approvalLease?.IsConfiguredRootOrAncestor()
                    ?? IsConfiguredRootOrAncestor(config, path, cancellationToken)))
            {
                return FileOperation.Failure(
                    request.Path,
                    DockerPathResolver.StructuredRootDeleteMessage,
                    DockerPathResolver.StructuredRootDeleteErrorCode);
            }
            return await HostFileSystemExecutor.DeleteFileAsync(
                CreateContext(path, approvalLease, cancellationToken),
                request,
                cancellationToken,
                hooks).ConfigureAwait(false);
        }
        catch (DockerStructuredBindRequiredException ex)
        {
            return FileOperation.Failure(request.Path, ex.Message, ex.ErrorCode);
        }
        catch (DockerResourceApprovalException ex)
        {
            return FileOperation.Failure(
                request.Path,
                ex.Message,
                DockerResourceReference.ApprovalRequiredErrorCode);
        }
        catch (InvalidOperationException)
        {
            return BindRequiredMutationFailure(request.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return FileOperation.Failure(
                request.Path,
                ex.Message,
                AgentFileReadErrorCodes.PathCanonicalizationFailed);
        }
    }

    public async ValueTask<AgentFileSearchResult> SearchAsync(
        DockerExecutionRuntimeConfig config,
        AgentFileSearchRequest request,
        DockerApprovedResourceLease? approvalLease,
        CancellationToken cancellationToken,
        ILocalSecureFileSystemHooks? hooks = null)
    {
        try
        {
            var path = DockerPathResolver.ResolveHostBinding(config, request.Path);
            return await HostSecureFileSearch.ExecuteAsync(
                CreateContext(path, approvalLease, cancellationToken),
                request,
                cancellationToken,
                hooks).ConfigureAwait(false);
        }
        catch (DockerStructuredBindRequiredException ex)
        {
            return AgentFileSearchResult.Failure(ex.ErrorCode, ex.Message);
        }
        catch (DockerResourceApprovalException ex)
        {
            return AgentFileSearchResult.Failure(
                DockerResourceReference.ApprovalRequiredErrorCode,
                ex.Message);
        }
        catch (InvalidOperationException)
        {
            return AgentFileSearchResult.Failure(
                DockerPathResolver.StructuredBindRequiredErrorCode,
                DockerPathResolver.StructuredBindRequiredMessage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return AgentFileSearchResult.Failure(AgentFileSearchErrorCodes.PathUnresolvable, ex.Message);
        }
    }

    public async ValueTask<AgentScopedInstructionDiscoveryResult> DiscoverScopedInstructionsAsync(
        DockerExecutionRuntimeConfig config,
        AgentScopedInstructionDiscoveryRequest request,
        string namespaceFingerprint,
        CancellationToken cancellationToken,
        ILocalSecureFileSystemHooks? hooks = null)
    {
        ValidateDiscoveryRequest(request);
        DockerHostPathBinding[] paths;
        try
        {
            paths = request.Probes
                .Select(probe => DockerPathResolver.ResolveHostBinding(config, probe.Path))
                .ToArray();
        }
        catch (DockerStructuredBindRequiredException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw new DockerStructuredBindRequiredException();
        }

        var mounts = config.Mounts
            .Select(mount => new HostScopedInstructionMount(
                Path.GetFullPath(mount.HostPath),
                mount.ContainerPath,
                HostReportedPathStyle.Posix))
            .ToArray();
        var probes = request.Probes
            .Select((probe, index) => new HostScopedInstructionProbe(
                probe.Path,
                paths[index].HostPath,
                paths[index].ContainerPath,
                probe.IsDirectory,
                paths[index].Mount.HostPath))
            .ToArray();
        var result = await HostScopedInstructionDiscovery.DiscoverAsync(
            mounts,
            probes,
            cancellationToken,
            hooks).ConfigureAwait(false);
        return result with
        {
            TargetFingerprint = ComputeHashSegments(
                ["docker-host-structured-v3", namespaceFingerprint, result.TargetFingerprint]),
            ScopeFingerprint = ComputeHashSegments(
                [namespaceFingerprint, result.ScopeFingerprint]),
        };
    }

    private static HostFileSystemPathContext CreateContext(
        DockerHostPathBinding path,
        DockerApprovedResourceLease? approvalLease,
        CancellationToken cancellationToken)
    {
        return new(
            [path.Mount.HostPath],
            path.HostPath,
            path.ContainerPath,
            ApprovedAuthority: approvalLease?.TakeResourceAuthority(),
            HostReportedPathStyle.Posix);
    }

    private static HostFileSystemPathContext CreateContext(
        DockerHostPathBinding path,
        LocalSecureApprovalLease operationAuthority,
        Action<LocalSecureApprovalLease>? postMutationAuthoritySink = null)
        => new(
            [path.Mount.HostPath],
            path.HostPath,
            path.ContainerPath,
            ApprovedAuthority: operationAuthority,
            HostReportedPathStyle.Posix,
            PostMutationAuthoritySink: postMutationAuthoritySink);

    private static bool IsConfiguredRootOrAncestor(
        DockerExecutionRuntimeConfig config,
        DockerHostPathBinding path,
        CancellationToken cancellationToken)
    {
        using var authority = HostSecurePathEngine.Capture(
            [path.Mount.HostPath],
            path.HostPath,
            cancellationToken: cancellationToken);
        var targetIdentity = authority.Binding.TargetIdentity;
        return targetIdentity is not null
               && CaptureConfiguredMountRootIdentityChains(config)
                    .Any(chain => chain.Identities.Contains(targetIdentity.Value));
    }

    private static IReadOnlyList<DockerMountRootIdentityChain> CaptureConfiguredMountRootIdentityChains(
        DockerExecutionRuntimeConfig config)
    {
        var roots = new List<DockerVerifiedMount>(config.Mounts.Count);
        try
        {
            foreach (var mount in config.Mounts)
            {
                roots.Add(new DockerVerifiedMount(mount, HostSecurePathEngine.OpenRoot(mount.HostPath)));
            }
            return DockerMountRootIdentityChains.Capture(roots);
        }
        finally
        {
            foreach (var root in roots)
            {
                root.Root.Dispose();
            }
        }
    }

    private static AgentFileReadResult BindRequiredReadFailure(string path)
        => AgentFileReadResult.Failure(
            path,
            DockerPathResolver.StructuredBindRequiredErrorCode,
            DockerPathResolver.StructuredBindRequiredMessage);

    private static AgentFileMutationResult BindRequiredMutationFailure(string path)
        => FileOperation.Failure(
            path,
            DockerPathResolver.StructuredBindRequiredMessage,
            DockerPathResolver.StructuredBindRequiredErrorCode);

    private static void ValidateDiscoveryRequest(AgentScopedInstructionDiscoveryRequest request)
    {
        if (request.Probes is null
            || request.Probes.Count == 0
            || request.Probes.Count > 64
            || request.Probes.Any(probe => probe is null || string.IsNullOrWhiteSpace(probe.Path))
            || request.Probes
                .Select(probe => (probe.Path, probe.IsDirectory, probe.FollowFinalSymbolicLink))
                .Distinct()
                .Count() != request.Probes.Count)
        {
            throw new InvalidOperationException(
                "Docker scoped-instruction discovery requires 1 to 64 unique, non-empty probes.");
        }
    }

    private static string GetPosixName(string path)
        => path[(path.LastIndexOf('/') + 1)..];

    private static string ComputeHashSegments(IEnumerable<string> values)
    {
        var material = new StringBuilder();
        foreach (var value in values)
        {
            material.Append(value.Length).Append(':').Append(value);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()))).ToLowerInvariant();
    }
}
